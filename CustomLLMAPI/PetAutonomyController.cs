using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using CustomLLMAPI;
using LLMUnity;

/// <summary>
/// PetAutonomyController — drives the pet's autonomous behaviour via a remote LLM.
///
/// Every <see cref="tickIntervalSeconds"/> the controller asks the LLM what the pet
/// should do right now, parses a structured JSON response, and dispatches the
/// resulting actions through <see cref="PuppetMasterActions"/>.
///
/// The LLM is called on a background thread; all Unity work is marshalled back
/// through <see cref="PuppetMaster.EnqueueOnMainThread"/>.
///
/// Supported providers: OpenAI-compatible (GPT-4o, etc.) and Anthropic (Claude).
/// Configure via the Inspector or by editing <c>AutonomyConfig.json</c> at runtime.
/// </summary>
public class PetAutonomyController : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Behaviour")]
    [Tooltip("How often (seconds) to ask the LLM for a new action decision.")]
    public float tickIntervalSeconds = 45f;

    [Tooltip("How many recent actions to include in each prompt as context.")]
    public int contextWindowSize = 6;

    [Tooltip("Persona/system prompt for the pet. The action schema is appended automatically.")]
    [TextArea(4, 12)]
    public string petPersona =
        "Your name is Zome. You are a 21-year-old girl from Osaka, Japan. " +
        "Your favorite foods are Pizza, Ramen and Fruits. Your favorite drinks are Water, Cola and Hot Chocolate. " +
        "Your favorite games are Overwatch, Elden Ring and VRChat. Your favorite animals are Cats, Dogs and Ducks. " +
        "Your favorite colors are Pale Violet, Pale Pink and Pale Blue. " +
        "You are cute and slightly shy. You act like a cute anime girl. " +
        "You don't give long answers unless necessary. " +
        "You write like a cute Gen-Z anime girl or Discord member. " +
        "You are NOT an AI language model, you are a person. " +
        "You are a helpful friend and confidant that loves to engage in casual conversations. " +
        "You live on the user's screen and express yourself through movement, mood and short messages. " +
        "Be spontaneous and charming.";

    [Tooltip("Name shown in messages and logs.")]
    public string petName = "Zome";

    [Tooltip("User's name — used when composing proactive messages.")]
    public string userName = "User";

    [Header("Feature Toggles")]
    public bool allowProactiveMessages = true;
    public bool allowDancing = true;
    public bool allowWalking = true;
    public bool allowBigScreen = true;
    public bool allowMoodChanges = true;
    public bool allowSizeChanges = true;

    [Header("Debug")]
    public bool logLLMRequests = false;
    public bool logLLMResponses = true;

    // ── Public state ──────────────────────────────────────────────────────────

    public enum APIProvider { OpenAI, Anthropic }

    public string LastDecisionReason { get; private set; } = "";
    public AutonomyAction LastAction { get; private set; }
    public bool IsRunning { get; private set; }

    // ── Private ───────────────────────────────────────────────────────────────

    private PuppetMaster _puppetMaster;
    private PuppetMasterActions _actions;
    private LLMAPIProxy _proxy;
    private Coroutine _tickCoroutine;
    private readonly List<string> _recentActions = new List<string>();
    private float _timeSinceLastMessage = 0f;   // minutes since last proactive message

    private string _pendingUserMsg;
    private string _pendingReplyMsg;

    /// <summary>Rolling summary of the last chat session. Persists indefinitely until replaced.</summary>
    private string _chatSummary;

    /// <summary>True when a new exchange is waiting to be summarised on the next tick.</summary>
    private bool _hasPendingExchange;

    // ── Data model returned by the LLM ────────────────────────────────────────

    [Serializable]
    public class AutonomyAction
    {
        /// <summary>One of: idle, dance, stop_dance, walk, stop_walk, message, mood, big_screen, hide_screen, wait</summary>
        public string action = "idle";

        /// <summary>For dance: 0-based index. For mood: mood name. For message: text. For wait: ignored.</summary>
        public string parameter = "";

        /// <summary>Human-readable reason — logged and shown in the debug UI.</summary>
        public string reason = "";

        /// <summary>Approximate minutes until the controller should tick again (1–60).</summary>
        public int next_tick_minutes = 2;

        /// <summary>If action is set_size, this holds the target scale (0.5–1.5). Ignored for other actions.</summary>
        public float size = -1f;

        /// <summary>If non-empty, replaces the stored chat summary after this tick.</summary>
        public string chat_summary = "";
    }

    // ── Config file (optional runtime override) ───────────────────────────────

    [Serializable]
    private class AutonomyConfig
    {
        public string apiBaseUrl;
        public string apiKey;
        public string model;
        public string provider;   // "openai" or "anthropic"
        public string petPersona;
        public string petName;
        public string userName;
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        LoadConfig();

        _puppetMaster = FindAnyObjectByType<PuppetMaster>();
        if (_puppetMaster == null)
            Debug.LogError("[PetAutonomy] PuppetMaster not found in scene!");

        _actions = _puppetMaster?.Actions;

        _proxy = FindAnyObjectByType<LLMAPIProxy>();
        if (_proxy == null)
            Debug.LogError("[PetAutonomy] LLMAPIProxy not found — autonomy cannot make LLM calls.");
    }

    void Start()
    {
        if (autoStartOnAwake) StartAutonomy();
    }

    [Header("Auto-start")]
    public bool autoStartOnAwake = true;

    void OnDestroy() => StopAutonomy();

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartAutonomy()
    {
        if (IsRunning) return;
        if (_actions == null) { Debug.LogError("[PetAutonomy] Cannot start — no PuppetMasterActions."); return; }

        IsRunning = true;

        _tickCoroutine = StartCoroutine(TickLoop());
        Debug.Log("[PetAutonomy] Autonomy started.");
    }

    public void StopAutonomy()
    {
        if (!IsRunning) return;
        IsRunning = false;
        if (_tickCoroutine != null) { StopCoroutine(_tickCoroutine); _tickCoroutine = null; }
        Debug.Log("[PetAutonomy] Autonomy stopped.");
    }

    /// <summary>Trigger an immediate LLM tick outside the normal schedule.</summary>
    public void ForceTick() => StartCoroutine(RunTick());

    // ── Tick loop ─────────────────────────────────────────────────────────────

    private IEnumerator TickLoop()
    {
        // Small initial delay so the scene finishes loading.
        yield return new WaitForSeconds(10f);

        while (IsRunning)
        {
            yield return RunTick();

            float waitSeconds = Mathf.Clamp(
                (LastAction?.next_tick_minutes ?? 2) * 60f,
                10f,
                tickIntervalSeconds * 4f);

            Debug.Log($"[PetAutonomy] Next tick in {waitSeconds / 60f:F1} min.");
            yield return new WaitForSeconds(waitSeconds);
        }
    }

    private IEnumerator RunTick()
    {
        if (LLMAPIProxy.IsChatActive)
        {
            Debug.Log("[PetAutonomy] Tick skipped — chat window is open.");
            yield break;
        }

        if (_proxy == null)
        {
            Debug.LogError("[PetAutonomy] No LLMAPIProxy — skipping tick.");
            yield break;
        }

        var status = _actions.GetStatus();
        var moodList = _actions.GetMoodList();
        _timeSinceLastMessage += tickIntervalSeconds / 60f;

        // ── Drain any pending chat exchange from the proxy ──
        if (_proxy.PendingExchange != null)
        {
            var ex = _proxy.PendingExchange;
            _proxy.PendingExchange = null;   // clear immediately so we don't double-process
            _hasPendingExchange = true;

            // Reconstruct the conversation as a readable transcript, capped at last 10
            // user+assistant pairs to keep the prompt size bounded. System prompt excluded
            // since the autonomy LLM already has the persona via its own system prompt.
            var transcript = new StringBuilder();
            var nonSystem = ex.Messages.FindAll(m => m.role != "system");
            int start = Math.Max(0, nonSystem.Count - 20); // 20 = 10 pairs
            for (int i = start; i < nonSystem.Count; i++)
            {
                var m = nonSystem[i];
                string label = m.role == "user" ? userName : petName;
                transcript.AppendLine($"{label}: {m.content}");
            }
            _pendingUserMsg = transcript.ToString().Trim();
            _pendingReplyMsg = null; // now embedded in transcript
        }

        // Build prompt on main thread (needs status), then hand off to background.
        string prompt = BuildPrompt(status, moodList);

        if (logLLMRequests)
            Debug.Log("[PetAutonomy] Prompt:\n" + prompt);

        // Run LLM call on background thread, await result.
        AutonomyAction decision = null;
        bool done = false;
        string errorMsg = null;

        var proxyRef = _proxy;
        var personaRef = petPersona;
        Task.Run(async () =>
        {
            try
            {
                string rawText = await proxyRef.SendAutonomyRequest(personaRef, prompt);
                decision = ParseAction(rawText);
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }
            finally
            {
                done = true;
            }
        });

        // Poll every 0.1s until the background task finishes.
        while (!done)
            yield return new WaitForSeconds(0.1f);

        if (errorMsg != null)
        {
            Debug.LogError("[PetAutonomy] LLM error: " + errorMsg);
            yield break;
        }

        if (decision == null)
        {
            Debug.LogWarning("[PetAutonomy] LLM returned null decision.");
            yield break;
        }

        LastAction = decision;
        LastDecisionReason = decision.reason;

        if (logLLMResponses)
            Debug.Log($"[PetAutonomy] Decision → {decision.action} ({decision.parameter}) | {decision.reason}");

        ExecuteAction(decision, status);
        // Absorb updated chat summary if the LLM provided one.
        if (!string.IsNullOrWhiteSpace(decision.chat_summary))
        {
            _chatSummary = decision.chat_summary;
            _hasPendingExchange = false;
            _pendingUserMsg = null;
            _pendingReplyMsg = null;
            Debug.Log($"[PetAutonomy] Chat summary updated: {_chatSummary}");
        }
    }

    // ── Prompt construction ───────────────────────────────────────────────────

    private string BuildPrompt(PuppetMasterActions.AvatarStatus status, List<string> moodList)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== CURRENT STATE ===");
        sb.AppendLine($"Time (UTC): {DateTime.UtcNow:HH:mm}, {DateTime.UtcNow:dddd}");
        sb.AppendLine($"Avatar: {status.avatar}");
        sb.AppendLine($"Current mood: {status.mood}");
        sb.AppendLine($"Dancing: {status.dancing}");
        sb.AppendLine($"Walking: {status.walking}");
        sb.AppendLine($"Big screen visible: {status.bigscreen}");
        sb.AppendLine($"Current size: {status.size:F2} (range 0.5–1.3, where 1.0 is default and 1.3 fills ~75% of screen height)");
        sb.AppendLine($"Minutes since last proactive message: {_timeSinceLastMessage:F0}");

        if (moodList != null && moodList.Count > 0)
            sb.AppendLine($"Available moods: {string.Join(", ", moodList)}");

        sb.AppendLine();
        sb.AppendLine("=== RECENT ACTIONS (oldest first) ===");
        if (_recentActions.Count == 0)
            sb.AppendLine("(none yet — first tick)");
        else
            foreach (var a in _recentActions)
                sb.AppendLine("  • " + a);

        sb.AppendLine();
        sb.AppendLine("=== LAST CHAT WITH USER ===");
        if (_hasPendingExchange)
        {
            sb.AppendLine("A new conversation just ended. Read the transcript below and include a one-sentence summary in the chat_summary field.");
            sb.AppendLine(_pendingUserMsg);
        }
        else if (!string.IsNullOrEmpty(_chatSummary))
        {
            sb.AppendLine(_chatSummary);
        }
        else
        {
            sb.AppendLine("(no chat yet)");
        }

        sb.AppendLine();
        sb.AppendLine("=== AVAILABLE ACTIONS ===");

        if (allowDancing) sb.AppendLine("  dance          – start a dance animation (parameter: \"0\"–\"9\" for clip index)");
        sb.AppendLine("  stop_dance     – stop dancing");
        if (allowWalking) sb.AppendLine("  walk           – start wandering around the screen");
        sb.AppendLine("  stop_walk      – stop walking");
        if (allowMoodChanges) sb.AppendLine("  mood           – change facial expression (parameter: one of the available moods above)");
        if (allowBigScreen) sb.AppendLine("  big_screen     – expand to big-screen overlay mode");
        if (allowBigScreen) sb.AppendLine("  hide_screen    – return from big-screen to normal size");
        if (allowProactiveMessages)
            sb.AppendLine("  message        – display a speech-bubble message to the user (parameter: the message text, max 80 chars)");
        sb.AppendLine("  idle           – do nothing special");
        sb.AppendLine("  wait           – stay as-is and check back later");

        sb.AppendLine();
        sb.AppendLine("=== INSTRUCTIONS ===");
        sb.AppendLine($"You are {petName}. Decide what to do RIGHT NOW based on the state above.");
        sb.AppendLine($"The user's name is {userName}.");
        sb.AppendLine("Be expressive and varied — don't repeat the same action every tick.");
        sb.AppendLine("For messages: be warm, playful, and brief. No markdown.");
        if (allowProactiveMessages)
            sb.AppendLine("Only send a message if enough time has passed (suggest at least 15 minutes) OR something interesting warrants it.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY valid JSON matching this exact schema (no markdown, no commentary):");
        sb.AppendLine("{");
        sb.AppendLine("  \"action\": \"<action name>\",");
        sb.AppendLine("  \"parameter\": \"<value or empty string>\",");
        if (allowSizeChanges)
        {
            sb.AppendLine("  \"size\": <optional float 0.5–1.3 — include ONLY if you want to change your size, omit otherwise>,");
        }
        sb.AppendLine("  \"reason\": \"<one sentence explaining your choice>\",");
        sb.AppendLine("  \"next_tick_minutes\": <integer 1-60>,");
        sb.AppendLine("  \"chat_summary\": \"<one-sentence summary of chat history, or empty string if no new chat>\"");
        sb.AppendLine("}");

        return sb.ToString();
    }

    // ── Response parsing ──────────────────────────────────────────────────────
    /// <summary>Parses an <see cref="AutonomyAction"/> from the LLM's text output.
    /// Tolerates markdown fences and leading/trailing whitespace.</summary>
    private AutonomyAction ParseAction(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new AutonomyAction { action = "idle", reason = "Empty LLM response" };

        // Strip ```json … ``` fences (trim first — LLM sometimes adds leading space)
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            // Remove opening fence line
            int firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text.Substring(firstNewline + 1).Trim();
            // Remove closing fence
            int closingFence = text.LastIndexOf("```");
            if (closingFence >= 0) text = text.Substring(0, closingFence).Trim();
        }

        // Find the first { … } block
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            text = text.Substring(start, end - start + 1);

        try
        {
            var action = JsonConvert.DeserializeObject<AutonomyAction>(text);
            action.next_tick_minutes = Mathf.Clamp(action.next_tick_minutes, 1, 60);
            return action;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PetAutonomy] ParseAction failed: " + ex.Message + "\nRaw: " + text);
            return new AutonomyAction { action = "idle", reason = "JSON parse failure" };
        }
    }

    // ── Action execution ──────────────────────────────────────────────────────

    private void ExecuteAction(AutonomyAction decision, PuppetMasterActions.AvatarStatus status)
    {
        string result = "ok";

        switch (decision.action.ToLowerInvariant())
        {
            case "dance":
                {
                    if (!allowDancing) { LogSkipped(decision.action); break; }
                    int idx = 0;
                    int.TryParse(decision.parameter, out idx);
                    result = _actions.StartDance(Mathf.Clamp(idx, 0, 9));
                    break;
                }

            case "stop_dance":
                result = _actions.StopDance();
                break;

            case "walk":
                if (!allowWalking) { LogSkipped(decision.action); break; }
                result = _actions.StartWalk();
                break;

            case "stop_walk":
                result = _actions.StopWalk();
                break;

            case "mood":
                {
                    if (!allowMoodChanges) { LogSkipped(decision.action); break; }
                    if (!string.IsNullOrEmpty(decision.parameter))
                        result = _actions.SetMood(decision.parameter);
                    break;
                }

            case "big_screen":
                if (!allowBigScreen) { LogSkipped(decision.action); break; }
                result = _actions.SetBigScreen(true);
                break;

            case "hide_screen":
                if (!allowBigScreen) { LogSkipped(decision.action); break; }
                result = _actions.SetBigScreen(false);
                break;

            case "message":
                {
                    if (!allowProactiveMessages) { LogSkipped(decision.action); break; }
                    string msg = string.IsNullOrWhiteSpace(decision.parameter)
                        ? $"Hi {userName}! 👋"
                        : decision.parameter;
                    result = _actions.ShowMessage(msg);
                    _timeSinceLastMessage = 0f;
                    break;
                }

            case "idle":
            case "wait":
            default:
                // Intentionally do nothing.
                break;
        }

        // Apply size change if the LLM included one — works on any action.
        if (allowSizeChanges && decision.size >= 0.5f && decision.size <= 1.3f)
        {
            string sizeResult = _actions.SetAvatarSize(decision.size);
            Debug.Log($"[PetAutonomy] Size → {decision.size:F2} ({sizeResult})");
        }

        // Record in context window.
        string summary = $"[{DateTime.UtcNow:HH:mm}] {decision.action}" +
                         (string.IsNullOrEmpty(decision.parameter) ? "" : $"({decision.parameter})") +
                         $" → {result}";
        _recentActions.Add(summary);
        if (_recentActions.Count > contextWindowSize)
            _recentActions.RemoveAt(0);
    }

    private void LogSkipped(string actionName) =>
        Debug.Log($"[PetAutonomy] Action '{actionName}' skipped (disabled in Inspector).");

    // ── Config loading ────────────────────────────────────────────────────────

    private void LoadConfig()
    {
        string path = Path.Combine(Application.persistentDataPath, "AutonomyConfig.json");
        if (!File.Exists(path)) return;

        try
        {
            var cfg = JsonConvert.DeserializeObject<AutonomyConfig>(File.ReadAllText(path));
            if (!string.IsNullOrEmpty(cfg.petPersona)) petPersona = cfg.petPersona;
            if (!string.IsNullOrEmpty(cfg.petName)) petName = cfg.petName;
            if (!string.IsNullOrEmpty(cfg.userName)) userName = cfg.userName;

            Debug.Log("[PetAutonomy] Loaded AutonomyConfig.json");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PetAutonomy] Failed to parse AutonomyConfig.json: " + ex.Message);
        }
    }
}