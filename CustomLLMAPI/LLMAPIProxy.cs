using CustomLLMAPI.Lib;
using CustomLLMAPI.PuppetMaster;
using LLMUnity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace LLMUnity
{
    public class PendingChatExchange
    {
        /// <summary>
        /// Full conversation history as parsed from the ChatML prompt, including
        /// the final assistant reply appended at the end. This is the complete
        /// context LLMUnity sent — the autonomy tick uses it to produce a summary.
        /// </summary>
        public List<LLMAPIProxy.APIMessage> Messages;
    }

    public class LLMAPIProxy : MonoBehaviour
    {
        [Header("Proxy Settings")]
        public int proxyPort = 13333;
        public List<LLMProxySettings.LLMProxySettingsData.APIConfigData> configs =
            new List<LLMProxySettings.LLMProxySettingsData.APIConfigData>();
        public int currentConfigIndex = 0;

        private LLMProxySettings _settingsRef;
        private MiniHttpServer _server;

        public bool isRunning => _server?.IsRunning ?? false;

        /// <summary>
        /// Set by HandleCompletion after each chat exchange.
        /// PetAutonomyController drains this on the next tick to update its summary.
        /// Written from background threads — read on the autonomy tick thread.
        /// Volatile is sufficient since it's a single reference swap.
        /// </summary>
        public volatile PendingChatExchange PendingExchange;

        public enum APIProvider { OpenAI, Anthropic, Custom }

        // ─── DTOs ────────────────────────────────────────────────────────────

        public class TemplateResponse { public string template; }
        public class ErrorResponse { public string error; }
        public class PropsResponse
        {
            public GenerationSettings default_generation_settings;
            public int total_slots;
            public string model;
            public string system_prompt;
        }
        public class GenerationSettings { public int n_predict; public double temperature; public double top_p; }
        public class SlotResponse { public int slot; public bool in_use; public string current_task; public string user; public string system_prompt; }
        public class TokenizeResponse { public int[] tokens; }
        public class DetokenizeResponse { public string text; }
        public class APIMessage { public string role; public string content; }

        // OpenAI /v1/chat/completions request
        public class OpenAIRequest
        {
            public string model;
            public APIMessage[] messages;
            public double? temperature;
            public double? top_p;
            public int? max_tokens;
            public object stop;
            public bool stream;
        }

        // Anthropic /v1/messages request
        public class AnthropicRequest
        {
            public string model;
            public APIMessage[] messages;
            [JsonProperty("system")]
            public string system;
            public int max_tokens;
            public double? temperature;
            public bool stream;
        }

        // llama.cpp-style completion response (what LLMUnity expects back)
        public class ChatResult
        {
            public string role = "assistant";
            public string content;
            public bool stop;
            public int id_slot;
        }

        // ─── Lifecycle ───────────────────────────────────────────────────────

        void Awake()
        {
            MainThreadDispatcher.CreateIfNeeded();

            if (FindAnyObjectByType<PuppetMaster>() == null)
                gameObject.AddComponent<PuppetMaster>();

            if (FindAnyObjectByType<PetAutonomyController>() == null)
                gameObject.AddComponent<PetAutonomyController>();

            _settingsRef = FindAnyObjectByType<LLMProxySettings>();
            if (_settingsRef == null)
                Debug.LogWarning("[LLMAPIProxy] No LLMProxySettings found in scene.");
        }

        void OnDestroy() => StopProxyServer();

        // ─── Server start / stop ─────────────────────────────────────────────

        public async Task StartProxyServer()
        {
            if (_server != null && _server.IsRunning)
            {
                Debug.LogWarning("[LLMAPIProxy] Server already running.");
                return;
            }

            _server = new MiniHttpServer(proxyPort, "LLMProxy");

            // Sync port-fallback back to settings UI
            _server.OnPortChanged += newPort =>
            {
                proxyPort = newPort;
                MainThreadDispatcher.Instance.Enqueue(() =>
                {
                    if (_settingsRef?.portInput != null)
                    {
                        _settingsRef.data.proxyPort = newPort;
                        _settingsRef.portInput.text = newPort.ToString();
                    }
                });
            };

            _server.OnRequest += HandleRequest;

            await _server.StartAsync();
        }

        public void StopProxyServer()
        {
            _server?.Stop();
            _server = null;
        }

        // ─── Request routing ─────────────────────────────────────────────────

        private async Task HandleRequest(HttpContext ctx)
        {
            string lp = ctx.Path.ToLower();

            if (lp.Contains("completion"))
            {
                bool isStream = false;
                try
                {
                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(ctx.Body ?? "{}");
                    isStream = parsed != null && parsed.ContainsKey("stream") &&
                               Convert.ToBoolean(parsed["stream"]);
                }
                catch { }

                try { await HandleCompletion(ctx, isStream); }
                catch (Exception e)
                {
                    await ctx.SendJsonAsync(500, JsonConvert.SerializeObject(
                        new ErrorResponse { error = e.Message }));
                }
                return;
            }

            string body;
            int status = 200;

            if (lp.Contains("template")) body = HandleTemplate();
            else if (lp.Contains("tokenize")) body = HandleTokenize();
            else if (lp.Contains("detokenize")) body = HandleDetokenize();
            else if (lp.Contains("slot")) body = HandleSlot();
            else if (lp.Contains("props") ||
                     lp.Contains("health")) body = HandleProps();
            else
            {
                status = 404;
                body = JsonConvert.SerializeObject(new ErrorResponse { error = $"Not found: {ctx.Path}" });
            }

            await ctx.SendJsonAsync(status, body);
        }

        // ─── Stub endpoint handlers ──────────────────────────────────────────

        private string HandleProps()
        {
            string model = configs.Count > 0 ? configs[currentConfigIndex].model : "unknown";
            return JsonConvert.SerializeObject(new PropsResponse
            {
                default_generation_settings = new GenerationSettings
                { n_predict = 512, temperature = 0.8, top_p = 0.9 },
                total_slots = 1,
                model = model,
                system_prompt = ""
            });
        }

        // Always report "chatml" so LLMUnity formats the prompt in a known way we can parse.
        private string HandleTemplate() => JsonConvert.SerializeObject(new TemplateResponse { template = "chatml" });
        private string HandleTokenize() => JsonConvert.SerializeObject(new TokenizeResponse { tokens = Array.Empty<int>() });
        private string HandleDetokenize() => JsonConvert.SerializeObject(new DetokenizeResponse { text = "" });
        private string HandleSlot() => JsonConvert.SerializeObject(new SlotResponse { slot = 0, in_use = false });

        // ─── Completion handler ──────────────────────────────────────────────

        private async Task HandleCompletion(HttpContext ctx, bool isStream)
        {
            var llmReq = JsonConvert.DeserializeObject<Dictionary<string, object>>(ctx.Body ?? "{}");
            var config = configs[currentConfigIndex];

            string rawPrompt = llmReq != null && llmReq.ContainsKey("prompt")
                ? llmReq["prompt"]?.ToString() : "(no prompt)";
            Debug.Log($"[LLM Proxy] >>> LLMUnity raw prompt (first 300):\n" +
                      $"{(rawPrompt.Length > 300 ? rawPrompt.Substring(0, 300) + "..." : rawPrompt)}");

            List<APIMessage> messages = ParseChatMLPrompt(llmReq);

            Debug.Log($"[LLM Proxy] >>> Parsed {messages.Count} messages:");
            for (int i = 0; i < messages.Count; i++)
                Debug.Log($"[LLM Proxy]   [{i}] {messages[i].role}: " +
                          $"{(messages[i].content.Length > 120 ? messages[i].content.Substring(0, 120) + "..." : messages[i].content)}");

            double temperature = GetDouble(llmReq, "temperature", 0.8);
            double top_p = GetDouble(llmReq, "top_p", 0.9);
            int max_tokens = GetInt(llmReq, "n_predict", 512);

            // n_predict:0 is LLMUnity's system-prompt preload/cache request — no generation wanted
            if (max_tokens == 0)
            {
                Debug.Log("[LLM Proxy] n_predict=0 — returning empty completion.");
                var empty = JsonConvert.SerializeObject(new ChatResult { content = "", stop = true, id_slot = 0 });
                if (isStream) await ctx.SendSseAsync(empty);
                else await ctx.SendJsonAsync(200, empty);
                return;
            }
            if (max_tokens < 0 || max_tokens > 4096) max_tokens = 512;

            string apiBody = config.provider == APIProvider.Anthropic
                ? BuildAnthropicRequest(messages, config, temperature, max_tokens, stream: false)
                : BuildOpenAIRequest(messages, config, temperature, top_p, max_tokens, stream: false);

            Debug.Log($"[LLM Proxy] >>> Sending to {config.provider} ({config.apiEndpoint}):\n{apiBody}");

            // ── Forward to the upstream LLM via RawHttpClient ────────────────
            var headers = BuildRequestHeaders(config);
            RawHttpResponse apiResp;
            try
            {
                apiResp = await RawHttpClient.PostAsync(config.apiEndpoint, apiBody, headers);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LLM Proxy] Network error: {ex.Message}");
                var empty = JsonConvert.SerializeObject(new ChatResult { content = "", stop = true, id_slot = 0 });
                if (isStream) await ctx.SendSseAsync(empty);
                else await ctx.SendJsonAsync(200, empty);
                return;
            }

            if (!apiResp.IsSuccess)
                Debug.LogWarning($"[LLM Proxy] Upstream returned {apiResp.StatusCode}: {apiResp.Text}");

            Debug.Log($"[LLM Proxy] <<< Raw API response (first 500):\n" +
                      $"{(apiResp.Text?.Length > 500 ? apiResp.Text.Substring(0, 500) + "..." : apiResp.Text)}");

            string result = ConvertResponseToLlamaCpp(apiResp.Text, config.provider, stop: true);

            // ── Chat → Autonomy bridge ────────────────────────────────────────
            try
            {
                string assistantText = ExtractFullContent(JToken.Parse(apiResp.Text), config.provider);
                if (!string.IsNullOrEmpty(assistantText) && messages.Count > 0)
                {
                    var fullHistory = new List<APIMessage>(messages)
                    {
                        new APIMessage { role = "assistant", content = assistantText }
                    };
                    PendingExchange = new PendingChatExchange { Messages = fullHistory };
                    Debug.Log($"[LLM Proxy] Chat exchange captured: {fullHistory.Count} messages.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[LLM Proxy] Failed to record chat exchange: " + ex.Message);
            }

            Debug.Log($"[LLM Proxy] <<< Sending to LLMUnity (isStream={isStream}):\n{result}");

            if (isStream) await ctx.SendSseAsync(result);
            else await ctx.SendJsonAsync(200, result);
        }

        // ─── ChatML parser ───────────────────────────────────────────────────

        private List<APIMessage> ParseChatMLPrompt(Dictionary<string, object> llmReq)
        {
            var messages = new List<APIMessage>();

            if (llmReq == null || !llmReq.ContainsKey("prompt"))
                return messages;

            string prompt = llmReq["prompt"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(prompt))
                return messages;

            var rawBlocks = prompt.Split(
                new[] { "<|im_start|>" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in rawBlocks)
            {
                string cleaned = block.Replace("<|im_end|>", "").Trim();
                string role, content = cleaned;

                if (cleaned.StartsWith("system ", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.StartsWith("system\n", StringComparison.OrdinalIgnoreCase))
                {
                    role = "system";
                    content = cleaned.Substring("system".Length).Trim();
                }
                else
                {
                    int nonSystem = messages.Count(m => m.role != "system");
                    role = (nonSystem % 2 == 0) ? "user" : "assistant";
                }

                messages.Add(new APIMessage { role = role, content = content });
            }

            messages = messages.Where(m => !string.IsNullOrWhiteSpace(m.content)).ToList();

            if (messages.Count > 0 && messages[messages.Count - 1].role != "user")
                messages[messages.Count - 1].role = "user";

            messages = MergeConsecutiveRoles(messages);

            if (messages.Count == 0 && !string.IsNullOrWhiteSpace(prompt))
            {
                Debug.LogWarning("[LLM Proxy] ChatML parse found no messages – sending as plain user.");
                messages.Add(new APIMessage { role = "user", content = prompt });
            }

            return messages;
        }

        // ─── Request builders ────────────────────────────────────────────────

        private string BuildOpenAIRequest(
            List<APIMessage> messages,
            LLMProxySettings.LLMProxySettingsData.APIConfigData config,
            double temperature, double top_p, int max_tokens, bool stream)
        {
            var req = new OpenAIRequest
            {
                model = config.model,
                messages = messages.ToArray(),
                temperature = temperature,
                top_p = top_p,
                max_tokens = max_tokens,
                stream = stream
            };
            return JsonConvert.SerializeObject(req,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        private string BuildAnthropicRequest(
            List<APIMessage> messages,
            LLMProxySettings.LLMProxySettingsData.APIConfigData config,
            double temperature, int max_tokens, bool stream)
        {
            string systemPrompt = null;
            var nonSystem = new List<APIMessage>();
            foreach (var m in messages)
            {
                if (m.role == "system") systemPrompt = m.content;
                else nonSystem.Add(m);
            }
            nonSystem = MergeConsecutiveRoles(nonSystem);
            if (nonSystem.Count == 0 || nonSystem[0].role != "user")
                nonSystem.Insert(0, new APIMessage { role = "user", content = "(start)" });

            var req = new AnthropicRequest
            {
                model = config.model,
                messages = nonSystem.ToArray(),
                system = systemPrompt,
                max_tokens = max_tokens > 0 ? max_tokens : 1024,
                temperature = temperature,
                stream = stream
            };
            return JsonConvert.SerializeObject(req,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        /// <summary>
        /// Build the HTTP headers dict passed to <see cref="RawHttpClient.PostAsync"/>.
        /// Centralised here so auth logic for each provider lives in one place.
        /// </summary>
        private Dictionary<string, string> BuildRequestHeaders(
            LLMProxySettings.LLMProxySettingsData.APIConfigData config)
        {
            var h = new Dictionary<string, string>();
            if (config.provider == APIProvider.Anthropic)
            {
                h["x-api-key"] = config.apiKey;
                h["anthropic-version"] = "2023-06-01";
            }
            else
            {
                h["Authorization"] = $"Bearer {config.apiKey}";
            }
            return h;
        }

        private List<APIMessage> MergeConsecutiveRoles(List<APIMessage> msgs)
        {
            var result = new List<APIMessage>();
            foreach (var m in msgs)
            {
                if (result.Count > 0 && result[result.Count - 1].role == m.role)
                    result[result.Count - 1].content += "\n" + m.content;
                else
                    result.Add(new APIMessage { role = m.role, content = m.content });
            }
            return result;
        }

        // ─── Response conversion ─────────────────────────────────────────────

        private string ConvertResponseToLlamaCpp(string apiResponse, APIProvider provider, bool stop)
        {
            if (string.IsNullOrEmpty(apiResponse))
                return JsonConvert.SerializeObject(
                    new ChatResult { content = "Error: empty response", stop = true, id_slot = 0 });
            try
            {
                JToken r = JToken.Parse(apiResponse);
                string content = ExtractFullContent(r, provider);
                return JsonConvert.SerializeObject(new ChatResult { content = content, stop = stop, id_slot = 0 });
            }
            catch (Exception e)
            {
                Debug.LogError($"[LLM Proxy] Response parse error: {e.Message}");
                return JsonConvert.SerializeObject(
                    new ChatResult { content = $"Error: {e.Message}", stop = true, id_slot = 0 });
            }
        }

        private string ExtractFullContent(JToken r, APIProvider provider)
        {
            var choices = r["choices"] as JArray;
            if (choices != null && choices.Count > 0)
            {
                var first = choices[0];
                if (first["message"]?["content"] != null)
                    return first["message"]["content"].ToString();
                if (first["text"] != null)
                    return first["text"].ToString();
            }

            if (provider == APIProvider.Anthropic)
            {
                var content = r["content"] as JArray;
                if (content != null && content.Count > 0)
                    return content[0]["text"]?.ToString() ?? "";
            }

            return r["content"]?.ToString()
                ?? r["text"]?.ToString()
                ?? r["output"]?.ToString()
                ?? "";
        }

        // ─── Autonomy API ────────────────────────────────────────────────────

        /// <summary>
        /// Returns true when the user's chat window is currently open/active.
        /// </summary>
        public static bool IsChatActive
        {
            get
            {
                var llmChar = UnityEngine.Object.FindAnyObjectByType<LLMUnity.LLMCharacter>();
                return llmChar != null && llmChar.gameObject.activeInHierarchy;
            }
        }

        /// <summary>
        /// Send a one-off request to the configured LLM backend on behalf of the
        /// autonomy system.  Uses the current active config but is entirely separate
        /// from the LLMUnity chat pipeline — never appears in user conversation history.
        /// </summary>
        public async Task<string> SendAutonomyRequest(string systemPrompt, string userPrompt)
        {
            if (configs == null || configs.Count == 0)
                throw new Exception("No API configs available in LLMAPIProxy.");

            var config = configs[currentConfigIndex];

            var messages = new List<APIMessage>
            {
                new APIMessage { role = "system", content = systemPrompt },
                new APIMessage { role = "user",   content = userPrompt   }
            };

            string body = config.provider == APIProvider.Anthropic
                ? BuildAnthropicRequest(messages, config, temperature: 0.9, max_tokens: 512, stream: false)
                : BuildOpenAIRequest(messages, config, temperature: 0.9, top_p: 1.0, max_tokens: 512, stream: false);

            var headers = BuildRequestHeaders(config);
            var resp = await RawHttpClient.PostAsync(config.apiEndpoint, body, headers);

            if (!resp.IsSuccess)
                throw new Exception($"Upstream error {resp.StatusCode}: {resp.Error ?? resp.Text}");

            return ExtractFullContent(JToken.Parse(resp.Text), config.provider);
        }

        // ─── Utility ─────────────────────────────────────────────────────────

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            if (d != null && d.TryGetValue(key, out var v) &&
                double.TryParse(v?.ToString(), out double r)) return r;
            return def;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            if (d != null && d.TryGetValue(key, out var v) &&
                int.TryParse(v?.ToString(), out int r)) return r;
            return def;
        }
    }
}