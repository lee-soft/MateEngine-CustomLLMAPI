using LLMUnity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
        public List<LLMProxySettings.LLMProxySettingsData.APIConfigData> configs = new List<LLMProxySettings.LLMProxySettingsData.APIConfigData>();
        public int currentConfigIndex = 0;

        private LLMProxySettings settingsRef;
        private Socket listenerSocket;
        public bool isRunning = false;
        private Thread acceptThread;
        private const int MaxPortFallbacks = 10;

        /// <summary>
        /// Set by HandleCompletion after each chat exchange.
        /// PetAutonomyController drains this on the next tick to update its summary.
        /// Written from background threads — read on the autonomy tick thread.
        /// Volatile is sufficient since it's a single reference swap.
        /// </summary>
        public volatile PendingChatExchange PendingExchange;

        public enum APIProvider { OpenAI, Anthropic, Custom }

        // ─── DTOs ────────────────────────────────────────────────────────────────

        public class TemplateResponse { public string template; }
        public class ErrorResponse { public string error; }

        // llama.cpp /props response – keeps LLMUnity happy
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

        // Structured message used throughout
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
            public string system;        // top-level system field (Anthropic spec)
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

        // ─── Lifecycle ───────────────────────────────────────────────────────────

        void Awake()
        {
            MainThreadDispatcher.CreateIfNeeded();

            if (FindAnyObjectByType<PuppetMaster>() == null)
                gameObject.AddComponent<PuppetMaster>();

            if (FindAnyObjectByType<PetAutonomyController>() == null)
                gameObject.AddComponent<PetAutonomyController>();

            settingsRef = FindAnyObjectByType<LLMProxySettings>();
            if (settingsRef == null)
                Debug.LogWarning("[LLMAPIProxy] No LLMProxySettings found in scene.");
        }

        void OnDestroy() => StopProxyServer();

        // ─── Server start / stop ─────────────────────────────────────────────────

        public async Task StartProxyServer()
        {
            if (listenerSocket != null && isRunning)
            {
                Debug.LogWarning("[LLM Proxy] Server already running.");
                return;
            }

            try
            {
                listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                int originalPort = proxyPort;
                bool bound = false;

                for (int i = 0; i < MaxPortFallbacks; i++)
                {
                    try
                    {
                        listenerSocket.Bind(new IPEndPoint(IPAddress.Loopback, proxyPort));
                        bound = true;
                        break;
                    }
                    catch (SocketException se) when (se.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                        proxyPort++;
                    }
                }

                if (!bound)
                    throw new Exception($"Failed to bind after {MaxPortFallbacks} attempts starting from port {originalPort}.");

                if (proxyPort != originalPort)
                {
                    Debug.LogWarning($"[LLM Proxy] Port {originalPort} in use. Falling back to {proxyPort}.");
                    MainThreadDispatcher.Instance.Enqueue(() =>
                    {
                        if (settingsRef != null && settingsRef.portInput != null)
                        {
                            settingsRef.data.proxyPort = proxyPort;
                            settingsRef.portInput.text = proxyPort.ToString();
                        }
                    });
                }

                listenerSocket.Listen(50);
                isRunning = true;
                Debug.Log($"[LLM Proxy] Server started on port {proxyPort}");

                acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LLMProxy-AcceptThread" };
                acceptThread.Start();
            }
            catch (Exception e)
            {
                Debug.LogError($"[LLM Proxy] Failed to start: {e.Message}");
                throw;
            }

            await Task.CompletedTask;
        }

        public void StopProxyServer()
        {
            isRunning = false;
            try { listenerSocket?.Close(); } catch { }
            listenerSocket = null;
            try { if (acceptThread != null && acceptThread.IsAlive) acceptThread.Join(200); } catch { }
            Debug.Log("[LLM Proxy] Server stopped.");
        }

        // ─── Accept loop ─────────────────────────────────────────────────────────

        private void AcceptLoop()
        {
            Debug.Log("[LLM Proxy] Accept thread running.");
            while (isRunning)
            {
                try
                {
                    Socket client = listenerSocket.Accept();
                    ThreadPool.QueueUserWorkItem(s => ProcessRequest((Socket)s), client);
                }
                catch (SocketException se) { if (isRunning) Debug.LogError($"[LLM Proxy] Accept error: {se.Message}"); }
                catch (Exception e) { Debug.LogError($"[LLM Proxy] Accept thread error: {e.Message}"); }
            }
            Debug.Log("[LLM Proxy] Accept thread exiting.");
        }

        // ─── Per-request processing ──────────────────────────────────────────────

        private void ProcessRequest(Socket client)
        {
            try
            {
                using (client)
                {
                    string raw = ReadRequest(client);
                    if (string.IsNullOrEmpty(raw)) { Debug.LogWarning("[LLM Proxy] Empty request."); return; }

                    var (method, path, headers, body) = ParseHttp(raw);

                    bool isStream = false;
                    try
                    {
                        var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(body ?? "{}");
                        isStream = parsed != null && parsed.ContainsKey("stream") && Convert.ToBoolean(parsed["stream"]);
                    }
                    catch { }

                    string lp = path?.ToLower() ?? "";

                    if (lp.Contains("completion"))
                    {
                        try
                        {
                            var task = HandleCompletion(body, client, isStream);
                            task.GetAwaiter().GetResult();
                            // streaming: response already sent inside HandleCompletion
                            // non-streaming: also sent inside HandleCompletion
                            return;
                        }
                        catch (Exception e)
                        {
                            SendJson(client, 500, JsonConvert.SerializeObject(
                                new ErrorResponse { error = e.Message }));
                            return;
                        }
                    }

                    string responseBody;
                    int status = 200;

                    if (lp.Contains("template")) responseBody = HandleTemplate();
                    else if (lp.Contains("tokenize")) responseBody = HandleTokenize();
                    else if (lp.Contains("detokenize")) responseBody = HandleDetokenize();
                    else if (lp.Contains("slot")) responseBody = HandleSlot();
                    else if (lp.Contains("props") || lp.Contains("health")) responseBody = HandleProps();
                    else { status = 404; responseBody = JsonConvert.SerializeObject(new ErrorResponse { error = $"Not found: {path}" }); }

                    SendJson(client, status, responseBody);
                }
            }
            catch (Exception e) { Debug.LogError($"[LLM Proxy] ProcessRequest error: {e.Message}"); }
        }

        // ─── Stub endpoint handlers ──────────────────────────────────────────────

        private string HandleProps()
        {
            string model = configs.Count > 0 ? configs[currentConfigIndex].model : "unknown";
            return JsonConvert.SerializeObject(new PropsResponse
            {
                default_generation_settings = new GenerationSettings { n_predict = 512, temperature = 0.8, top_p = 0.9 },
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

        // ─── Completion handler ──────────────────────────────────────────────────

        private async Task HandleCompletion(string body, Socket client, bool isStream)
        {
            var llmReq = JsonConvert.DeserializeObject<Dictionary<string, object>>(body ?? "{}");
            var config = configs[currentConfigIndex];

            // ── Debug: raw request from LLMUnity ──
            string rawPrompt = llmReq != null && llmReq.ContainsKey("prompt") ? llmReq["prompt"]?.ToString() : "(no prompt)";
            Debug.Log($"[LLM Proxy] >>> LLMUnity raw prompt (first 300 chars):\n{(rawPrompt.Length > 300 ? rawPrompt.Substring(0, 300) + "..." : rawPrompt)}");

            List<APIMessage> messages = ParseChatMLPrompt(llmReq);

            // ── Debug: parsed messages ──
            Debug.Log($"[LLM Proxy] >>> Parsed {messages.Count} messages:");
            for (int i = 0; i < messages.Count; i++)
                Debug.Log($"[LLM Proxy]   [{i}] {messages[i].role}: {(messages[i].content.Length > 120 ? messages[i].content.Substring(0, 120) + "..." : messages[i].content)}");

            double temperature = GetDouble(llmReq, "temperature", 0.8);
            double top_p = GetDouble(llmReq, "top_p", 0.9);
            int max_tokens = GetInt(llmReq, "n_predict", 512);

            // n_predict:0 is LLMUnity's system-prompt preload/cache request — no generation wanted
            if (max_tokens == 0)
            {
                Debug.Log("[LLM Proxy] n_predict=0 — returning empty completion (system prompt cache request).");
                SendJson(client, 200, JsonConvert.SerializeObject(new ChatResult { content = "", stop = true, id_slot = 0 }));
                return;
            }

            if (max_tokens < 0 || max_tokens > 4096) max_tokens = 512;

            string apiBody;
            if (config.provider == APIProvider.Anthropic)
                apiBody = BuildAnthropicRequest(messages, config, temperature, max_tokens, stream: false);
            else
                apiBody = BuildOpenAIRequest(messages, config, temperature, top_p, max_tokens, stream: false);

            // ── Debug: outbound request to OpenAI ──
            Debug.Log($"[LLM Proxy] >>> Sending to {config.provider} ({config.apiEndpoint}):\n{apiBody}");

            string authHeader = config.provider == APIProvider.Anthropic
                ? null
                : $"Bearer {config.apiKey}";

            string apiResp;
            try
            {
                apiResp = await ForwardNonStreaming(apiBody, config, authHeader);
            }
            catch (Exception ex)
            {
                // Network failure — return a clean empty response so LLMUnity doesn't retry endlessly.
                // Our empty-pair cleanup in ParseChatMLPrompt will drop this exchange from history
                // on the next message, so it stays consistent.
                Debug.LogWarning($"[LLM Proxy] Network error forwarding to LLM: {ex.Message}");
                string emptyResult = JsonConvert.SerializeObject(new ChatResult { content = "", stop = true, id_slot = 0 });
                if (isStream)
                {
                    string ssePayload = $"data: {emptyResult}\r\n\r\ndata: {{\"content\":\"\",\"stop\":true,\"id_slot\":0}}\r\n\r\n";
                    byte[] sseBytes = Encoding.UTF8.GetBytes(ssePayload);
                    string header = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
                    string chunkHex = sseBytes.Length.ToString("x");
                    string fullResp = header + chunkHex + "\r\n" + ssePayload + "\r\n0\r\n\r\n";
                    byte[] fullBytes = Encoding.UTF8.GetBytes(fullResp);
                    try { client.Send(fullBytes); } catch { }
                }
                else
                {
                    SendJson(client, 200, emptyResult);
                }
                return;
            }

            // ── Debug: raw response from OpenAI ──
            Debug.Log($"[LLM Proxy] <<< Raw API response (first 500 chars):\n{(apiResp != null && apiResp.Length > 500 ? apiResp.Substring(0, 500) + "..." : apiResp)}");

            string result = ConvertResponseToLlamaCpp(apiResp, config.provider, stop: true);
            // ── Chat → Autonomy bridge: snapshot full history + assistant reply ──
            try
            {
                string assistantText = ExtractFullContent(JToken.Parse(apiResp), config.provider);

                if (!string.IsNullOrEmpty(assistantText) && messages.Count > 0)
                {
                    // Build a complete history: everything LLMUnity sent + the reply we got back.
                    // This gives the autonomy tick the full conversation to summarise — for free,
                    // since it's already in memory here.
                    var fullHistory = new List<APIMessage>(messages)
                    {
                        new APIMessage { role = "assistant", content = assistantText }
                    };

                    PendingExchange = new PendingChatExchange { Messages = fullHistory };
                    Debug.Log($"[LLM Proxy] Chat exchange captured: {fullHistory.Count} messages for autonomy summary.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[LLM Proxy] Failed to record chat exchange: " + ex.Message);
            }

            // ── Debug: what we send back to LLMUnity ──
            Debug.Log($"[LLM Proxy] <<< Sending to LLMUnity (isStream={isStream}):\n{result}");

            if (isStream)
            {
                // LLMUnity expects chunked SSE — wrap the single result in that format
                string ssePayload = $"data: {result}\r\n\r\ndata: {{\"content\":\"\",\"stop\":true,\"id_slot\":0}}\r\n\r\n";
                byte[] sseBytes = Encoding.UTF8.GetBytes(ssePayload);
                string header = "HTTP/1.1 200 OK\r\n" +
                                    "Content-Type: text/event-stream\r\n" +
                                    "Transfer-Encoding: chunked\r\n" +
                                    "Connection: close\r\n\r\n";
                string chunkHex = sseBytes.Length.ToString("x");
                string fullResp = header + chunkHex + "\r\n" + ssePayload + "\r\n0\r\n\r\n";
                byte[] fullBytes = Encoding.UTF8.GetBytes(fullResp);
                try
                {
                    int sent = 0;
                    while (sent < fullBytes.Length)
                    {
                        int s = client.Send(fullBytes, sent, fullBytes.Length - sent, SocketFlags.None);
                        if (s <= 0) break;
                        sent += s;
                    }
                }
                catch (Exception e) { Debug.LogError($"[LLM Proxy] SSE send error: {e.Message}"); }
            }
            else
            {
                SendJson(client, 200, result);
            }
        }

        // ─── chatml parser ───────────────────────────────────────────────────────
        // LLMUnity's actual output looks like:
        //   <|im_start|>system <|im_end|> <|im_start|> hello<|im_end|> <|im_start|> Hello! How can I...<|im_end|> <|im_start|>
        // Note:
        //   - separator between role and content is a SPACE, not \n
        //   - only the first block has a named role ("system")
        //   - subsequent blocks have NO role name — just content
        //   - blocks alternate user/assistant after the system block
        //   - the final dangling <|im_start|> is the prompt for the next assistant turn

        private List<APIMessage> ParseChatMLPrompt(Dictionary<string, object> llmReq)
        {
            var messages = new List<APIMessage>();

            if (llmReq == null || !llmReq.ContainsKey("prompt"))
                return messages;

            string prompt = llmReq["prompt"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(prompt))
                return messages;

            var rawBlocks = prompt.Split(new[] { "<|im_start|>" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in rawBlocks)
            {
                string cleaned = block.Replace("<|im_end|>", "").Trim();

                // Detect explicit role prefix
                string role = null;
                string content = cleaned;

                foreach (var knownRole in new[] { "system", "user", "assistant" })
                {
                    if (cleaned.StartsWith(knownRole + " ", StringComparison.OrdinalIgnoreCase) ||
                        cleaned.StartsWith(knownRole + "\n", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Log($"Matched-Cleaned Role to: {knownRole}");

                        role = knownRole;
                        content = cleaned.Substring(knownRole.Length).Trim();
                        break;
                    }
                    if (cleaned.Equals(knownRole, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.Log($"Matched Role to: {knownRole}");

                        role = knownRole;
                        content = "";
                        break;
                    }
                }

                // No role prefix — infer from alternation position after the system block.
                // Non-system blocks strictly alternate user/assistant, so position is deterministic.
                if (role == null)
                {
                    int nonSystemCount = messages.Count(m => m.role != "system");
                    role = (nonSystemCount % 2 == 0) ? "user" : "assistant";
                }

                if (string.IsNullOrEmpty(content))
                    continue;

                messages.Add(new APIMessage { role = role, content = content });
            }

            // Drop failed exchange pairs — an empty assistant turn means the preceding user
            // message never reached the LLM. Remove both so the history only reflects what
            // the LLM actually saw and responded to.
            var filteredMessages = new List<APIMessage>();
            for (int i = 0; i < messages.Count; i++)
            {
                // If this is a user turn and the next is an empty assistant reply, skip both
                if (i + 1 < messages.Count
                    && messages[i].role == "user"
                    && messages[i + 1].role == "assistant"
                    && string.IsNullOrWhiteSpace(messages[i + 1].content))
                {
                    i++; // skip the empty assistant turn too
                    continue;
                }

                // Also drop any stray empty turns that don't fit the pair pattern
                if (!string.IsNullOrWhiteSpace(messages[i].content))
                    filteredMessages.Add(messages[i]);
            }
            messages = filteredMessages;

            if (messages.Count == 0 && !string.IsNullOrWhiteSpace(prompt))
            {
                Debug.LogWarning("[LLM Proxy] ChatML parse found no messages – sending as plain user message.");
                messages.Add(new APIMessage { role = "user", content = prompt });
            }

            return messages;
        }

        // ─── Request builders ────────────────────────────────────────────────────

        private string BuildOpenAIRequest(
            List<APIMessage> messages, LLMProxySettings.LLMProxySettingsData.APIConfigData config,
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
            return JsonConvert.SerializeObject(req, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        private string BuildAnthropicRequest(
            List<APIMessage> messages, LLMProxySettings.LLMProxySettingsData.APIConfigData config,
            double temperature, int max_tokens, bool stream)
        {
            // Anthropic requires system prompt at the root level, NOT in messages[]
            string systemPrompt = null;
            var nonSystem = new List<APIMessage>();

            foreach (var m in messages)
            {
                if (m.role == "system") systemPrompt = m.content;
                else nonSystem.Add(m);
            }

            // Anthropic also requires messages to strictly alternate user/assistant.
            // Merge consecutive same-role turns.
            nonSystem = MergeConsecutiveRoles(nonSystem);

            // Must start with a user turn
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
            return JsonConvert.SerializeObject(req, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        // Merge consecutive messages with the same role into one (Anthropic requirement)
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

        // ─── Non-streaming forward ───────────────────────────────────────────────

        private async Task<string> ForwardNonStreaming(
            string body, LLMProxySettings.LLMProxySettingsData.APIConfigData config, string authHeader)
        {
            var extraHeaders = new Dictionary<string, string>();
            if (config.provider == APIProvider.Anthropic)
            {
                extraHeaders["x-api-key"] = config.apiKey;
                extraHeaders["anthropic-version"] = "2023-06-01";
            }

            Uri uri = new Uri(config.apiEndpoint);
            string host = uri.Host;
            int port = uri.Port == -1 ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
            string path = uri.PathAndQuery;
            bool useSsl = uri.Scheme == "https";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? "");

            var hdrSb = new StringBuilder();
            hdrSb.Append($"POST {path} HTTP/1.1\r\n");
            hdrSb.Append($"Host: {host}\r\n");
            if (!string.IsNullOrEmpty(authHeader)) hdrSb.Append($"Authorization: {authHeader}\r\n");
            foreach (var kv in extraHeaders) hdrSb.Append($"{kv.Key}: {kv.Value}\r\n");
            hdrSb.Append("Content-Type: application/json\r\n");
            hdrSb.Append("Connection: close\r\n");
            hdrSb.Append($"Content-Length: {bodyBytes.Length}\r\n\r\n");

            using (var tcpClient = new TcpClient())
            {
                await tcpClient.ConnectAsync(host, port);
                Stream net = tcpClient.GetStream();

                if (useSsl)
                {
                    var ssl = new SslStream(net, false, (s, c, ch, e) => true);
                    await ssl.AuthenticateAsClientAsync(host, null, System.Security.Authentication.SslProtocols.None, false);
                    net = ssl;
                }

                // Send request
                byte[] hdrBytes = Encoding.UTF8.GetBytes(hdrSb.ToString());
                await net.WriteAsync(hdrBytes, 0, hdrBytes.Length);
                if (bodyBytes.Length > 0) await net.WriteAsync(bodyBytes, 0, bodyBytes.Length);

                // Read raw response into memory
                var rawMs = new MemoryStream();
                byte[] buf = new byte[8192];
                int r;
                while ((r = await net.ReadAsync(buf, 0, buf.Length)) > 0)
                    rawMs.Write(buf, 0, r);

                byte[] rawBytes = rawMs.ToArray();
                string raw = Encoding.UTF8.GetString(rawBytes);

                // Split headers from body
                int headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                {
                    Debug.LogError("[LLM Proxy] Could not find end of HTTP headers in response.");
                    return raw;
                }

                string headers = raw.Substring(0, headerEnd);
                string rawBodyText = raw.Substring(headerEnd + 4);

                // ── Debug: HTTP response headers and raw body ──
                Debug.Log($"[LLM Proxy] <<< HTTP response headers:\n{headers}");
                Debug.Log($"[LLM Proxy] <<< Raw body after header strip (first 300 chars):\n{(rawBodyText.Length > 300 ? rawBodyText.Substring(0, 300) + "..." : rawBodyText)}");

                bool isChunked = headers.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0;
                Debug.Log($"[LLM Proxy] <<< isChunked: {isChunked}");

                if (!isChunked)
                    return rawBodyText;

                // Work on raw bytes to avoid UTF-8 split across chunk boundaries
                byte[] responseBytes = rawBytes;
                int bodyStart = 0;
                // find \r\n\r\n in bytes to get body start
                byte[] sep = new byte[] { 13, 10, 13, 10 };
                for (int i = 0; i <= responseBytes.Length - sep.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < sep.Length; j++)
                        if (responseBytes[i + j] != sep[j]) { match = false; break; }
                    if (match) { bodyStart = i + sep.Length; break; }
                }

                string decoded = DecodeChunkedBodyBytes(responseBytes, bodyStart);
                Debug.Log($"[LLM Proxy] <<< Decoded chunked body (first 300 chars):\n{(decoded.Length > 300 ? decoded.Substring(0, 300) + "..." : decoded)}");
                return decoded;
            }
        }

        // Decodes HTTP chunked transfer encoding from raw bytes, returns UTF-8 string.
        // Working on bytes avoids UTF-8 characters being split across chunk boundaries.
        private string DecodeChunkedBodyBytes(byte[] data, int pos)
        {
            var result = new MemoryStream();

            while (pos < data.Length)
            {
                // Read chunk size line (terminated by \r\n)
                int lineStart = pos;
                while (pos < data.Length - 1 && !(data[pos] == '\r' && data[pos + 1] == '\n'))
                    pos++;
                if (pos >= data.Length - 1) break;

                string sizeLine = Encoding.ASCII.GetString(data, lineStart, pos - lineStart).Trim();
                pos += 2; // skip \r\n after size

                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi).Trim();

                int chunkSize = 0;
                try { chunkSize = Convert.ToInt32(sizeLine, 16); } catch { break; }

                if (chunkSize == 0) break; // terminal zero-chunk

                int available = Math.Min(chunkSize, data.Length - pos);
                result.Write(data, pos, available);
                pos += chunkSize + 2; // skip chunk data + trailing \r\n
            }

            return Encoding.UTF8.GetString(result.ToArray());
        }


        // ─── Streaming forward ───────────────────────────────────────────────────
        // Strategy: always collect the full OpenAI stream internally, then send
        // one complete llama.cpp-format response back to LLMUnity.
        // This avoids all socket race conditions and double-termination bugs.

        private async Task ForwardStreaming(
            string body, LLMProxySettings.LLMProxySettingsData.APIConfigData config,
            string authHeader, Socket client)
        {
            var tcs = new TaskCompletionSource<string>();
            var fullText = new System.Text.StringBuilder();

            // Force stream:false in the outbound request to OpenAI — simpler and more reliable
            var requestDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            requestDict["stream"] = false;
            string nonStreamBody = JsonConvert.SerializeObject(requestDict);

            // Forward as non-streaming to OpenAI, collect full response
            string apiResponse = await ForwardNonStreaming(nonStreamBody, config, authHeader);
            string content = ConvertResponseToLlamaCpp(apiResponse, config.provider, stop: true);

            // Send back to LLMUnity as a single non-chunked response
            SendJson(client, 200, content);
        }

        // ─── Response conversion ─────────────────────────────────────────────────

        private string ConvertResponseToLlamaCpp(string apiResponse, APIProvider provider, bool stop)
        {
            if (string.IsNullOrEmpty(apiResponse))
                return JsonConvert.SerializeObject(new ChatResult { content = "Error: empty response", stop = true, id_slot = 0 });

            try
            {
                JToken r = JToken.Parse(apiResponse);
                string content = ExtractFullContent(r, provider);
                return JsonConvert.SerializeObject(new ChatResult { content = content, stop = stop, id_slot = 0 });
            }
            catch (Exception e)
            {
                Debug.LogError($"[LLM Proxy] Response parse error: {e.Message}");
                return JsonConvert.SerializeObject(new ChatResult { content = $"Error: {e.Message}", stop = true, id_slot = 0 });
            }
        }

        private string ExtractFullContent(JToken r, APIProvider provider)
        {
            // OpenAI / OpenAI-compatible
            var choices = r["choices"] as JArray;
            if (choices != null && choices.Count > 0)
            {
                var first = choices[0];
                Debug.Log($"[LLM Proxy] <<< choices[0] keys: {string.Join(", ", first.Children<JProperty>().Select(p => p.Name))}");
                if (first["message"] != null)
                    Debug.Log($"[LLM Proxy] <<< message keys: {string.Join(", ", first["message"].Children<JProperty>().Select(p => p.Name))}");

                if (first["message"]?["content"] != null)
                {
                    string c = first["message"]["content"].ToString();
                    Debug.Log($"[LLM Proxy] <<< Extracted content via message.content: '{c.Substring(0, Math.Min(100, c.Length))}'");
                    return c;
                }
                if (first["text"] != null)
                {
                    string c = first["text"].ToString();
                    Debug.Log($"[LLM Proxy] <<< Extracted content via text: '{c.Substring(0, Math.Min(100, c.Length))}'");
                    return c;
                }
            }

            // Anthropic
            if (provider == APIProvider.Anthropic)
            {
                var content = r["content"] as JArray;
                if (content != null && content.Count > 0)
                {
                    string c = content[0]["text"]?.ToString() ?? "";
                    Debug.Log($"[LLM Proxy] <<< Extracted content via Anthropic content[0].text: '{c.Substring(0, Math.Min(100, c.Length))}'");
                    return c;
                }
            }

            // Generic fallback
            string fallback = r["content"]?.ToString() ?? r["text"]?.ToString() ?? r["output"]?.ToString() ?? "";
            Debug.Log($"[LLM Proxy] <<< Extracted content via fallback: '{fallback.Substring(0, Math.Min(100, fallback.Length))}'");
            return fallback;
        }

        // ─── Autonomy API ────────────────────────────────────────────────────────

        /// <summary>
        /// Set this true while the user's chat window is open.
        /// PetAutonomyController checks this flag and skips ticks while chatting.
        /// </summary>
        // ─── Autonomy API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true when the user's chat window is currently open/active.
        /// Resolved by finding the LLMCharacter component and checking its
        /// GameObject's active state in the hierarchy — no source changes needed.
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
        /// autonomy system. Uses the current active config (same API key, model,
        /// endpoint) but is entirely separate from the LLMUnity chat pipeline —
        /// it never appears in the user's conversation history.
        /// </summary>
        /// <param name="systemPrompt">The pet persona / instruction prompt.</param>
        /// <param name="userPrompt">The autonomy decision prompt.</param>
        /// <returns>The raw assistant text from the LLM.</returns>
        public async Task<string> SendAutonomyRequest(string systemPrompt, string userPrompt)
        {
            if (configs == null || configs.Count == 0)
                throw new Exception("No API configs available in LLMAPIProxy.");

            var config = configs[currentConfigIndex];

            var messages = new List<APIMessage>
            {
                new APIMessage { role = "system", content = systemPrompt },
                new APIMessage { role = "user",   content = userPrompt }
            };

            string body;
            string authHeader;

            if (config.provider == APIProvider.Anthropic)
            {
                body = BuildAnthropicRequest(messages, config, temperature: 0.9, max_tokens: 512, stream: false);
                authHeader = null; // Anthropic uses x-api-key header set inside BuildAnthropicRequest path
            }
            else
            {
                body = BuildOpenAIRequest(messages, config, temperature: 0.9, top_p: 1.0, max_tokens: 512, stream: false);
                authHeader = $"Bearer {config.apiKey}";
            }

            string rawResponse = await ForwardNonStreaming(body, config, authHeader);

            if (string.IsNullOrEmpty(rawResponse))
                throw new Exception("Empty response from LLM.");

            JToken r = JToken.Parse(rawResponse);
            return ExtractFullContent(r, config.provider);
        }

        // ─── HTTP helpers ────────────────────────────────────────────────────────

        private void SendJson(Socket client, int status, string body)
        {
            try
            {
                string phrase = status == 200 ? "OK" : status == 404 ? "Not Found" : "Error";
                string resp = $"HTTP/1.1 {status} {phrase}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                byte[] bytes = Encoding.UTF8.GetBytes(resp);
                int sent = 0;
                while (sent < bytes.Length)
                {
                    int s = client.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                    if (s <= 0) break;
                    sent += s;
                }
            }
            catch (Exception e) { Debug.LogError($"[LLM Proxy] SendJson error: {e.Message}"); }
        }

        private void SendChunk(Socket client, string data)
        {
            if (client == null || !client.Connected) return;
            try
            {
                if (data == null)
                {
                    client.Send(Encoding.ASCII.GetBytes("0\r\n\r\n"));
                    return;
                }
                byte[] payload = Encoding.UTF8.GetBytes(data);
                string sizeHex = payload.Length.ToString("x");
                client.Send(Encoding.ASCII.GetBytes(sizeHex + "\r\n"));
                client.Send(payload);
                client.Send(Encoding.ASCII.GetBytes("\r\n"));
            }
            catch (SocketException se) { Debug.LogError($"[LLM Proxy] SendChunk socket error: {se.Message}"); }
        }

        private string ReadRequest(Socket socket)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    byte[] buf = new byte[4096];
                    int total = 0;

                    while (true)
                    {
                        int read = socket.Receive(buf, 0, buf.Length, SocketFlags.None);
                        if (read <= 0) break;
                        ms.Write(buf, 0, read);
                        total += read;
                        if (ms.Length > 20 * 1024 * 1024) { Debug.LogError("[LLM Proxy] Request too large."); return null; }

                        string s = Encoding.UTF8.GetString(ms.ToArray());
                        int headerEnd = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd < 0) continue;

                        // Parse Content-Length and wait for full body
                        int contentLength = 0;
                        foreach (var line in s.Substring(0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None))
                        {
                            int idx = line.IndexOf(':');
                            if (idx > 0 && line.Substring(0, idx).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(line.Substring(idx + 1).Trim(), out contentLength);
                        }

                        int bodyAlready = total - (headerEnd + 4);
                        int need = contentLength - bodyAlready;
                        while (need > 0)
                        {
                            int r = socket.Receive(buf, 0, Math.Min(buf.Length, need), SocketFlags.None);
                            if (r <= 0) break;
                            ms.Write(buf, 0, r); total += r; need -= r;
                        }
                        break;
                    }
                    return Encoding.UTF8.GetString(ms.ToArray());
                } // end using ms
            }
            catch (Exception e) { Debug.LogError($"[LLM Proxy] ReadRequest error: {e.Message}"); return null; }
        }

        private (string method, string path, Dictionary<string, string> headers, string body) ParseHttp(string request)
        {
            if (string.IsNullOrEmpty(request)) return (null, null, null, null);
            var lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return (null, null, null, null);

            var rl = lines[0].Split(' ');
            string method = rl.Length > 0 ? rl[0] : null;
            string path = rl.Length > 1 ? rl[1] : null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int bodyStart = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrEmpty(lines[i])) { bodyStart = i + 1; break; }
                var p = lines[i].Split(new[] { ": " }, 2, StringSplitOptions.None);
                if (p.Length == 2) headers[p[0]] = p[1];
            }

            string body = bodyStart > 0 && bodyStart < lines.Length
                ? string.Join("\r\n", lines, bodyStart, lines.Length - bodyStart)
                : "";

            return (method, path, headers, body);
        }

        // ─── Utility ─────────────────────────────────────────────────────────────

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            if (d != null && d.TryGetValue(key, out var v) && double.TryParse(v?.ToString(), out double r)) return r;
            return def;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            if (d != null && d.TryGetValue(key, out var v) && int.TryParse(v?.ToString(), out int r)) return r;
            return def;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        // ─── Streaming download handler ──────────────────────────────────────────

        public class StreamingDownloadHandler
        {
            private readonly Action<string> _onData;
            private const int ReadBufferSize = 8192;

            public StreamingDownloadHandler(Action<string> onData) => _onData = onData;

            public async Task SendRequestAsync(
                string host, int port, string path, string body,
                bool useSsl, string authHeader,
                Dictionary<string, string> extraHeaders = null)
            {
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(host, port);
                    Stream net = client.GetStream();

                    if (useSsl)
                    {
                        var ssl = new SslStream(net, false, (s, c, ch, e) => true);
                        await ssl.AuthenticateAsClientAsync(host, null, System.Security.Authentication.SslProtocols.None, false);
                        net = ssl;
                    }

                    byte[] bodyBytes = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);

                    var hdrSb = new StringBuilder();
                    hdrSb.Append($"POST {path} HTTP/1.1\r\n");
                    hdrSb.Append($"Host: {host}\r\n");
                    if (!string.IsNullOrEmpty(authHeader)) hdrSb.Append($"Authorization: {authHeader}\r\n");
                    if (extraHeaders != null)
                        foreach (var kv in extraHeaders) hdrSb.Append($"{kv.Key}: {kv.Value}\r\n");
                    hdrSb.Append("Content-Type: application/json\r\n");
                    hdrSb.Append("Accept: text/event-stream\r\n");
                    hdrSb.Append("Connection: close\r\n");
                    hdrSb.Append($"Content-Length: {bodyBytes.Length}\r\n\r\n");

                    byte[] hdrBytes = Encoding.UTF8.GetBytes(hdrSb.ToString());
                    await net.WriteAsync(hdrBytes, 0, hdrBytes.Length);
                    if (bodyBytes.Length > 0) await net.WriteAsync(bodyBytes, 0, bodyBytes.Length);

                    // Read response headers
                    var hdrMs = new MemoryStream();
                    var termSeq = Encoding.ASCII.GetBytes("\r\n\r\n");
                    byte[] oneByte = new byte[1];
                    while (true)
                    {
                        int r = await net.ReadAsync(oneByte, 0, 1);
                        if (r <= 0) throw new IOException("EOF reading response header");
                        hdrMs.WriteByte(oneByte[0]);
                        if (hdrMs.Length >= 4)
                        {
                            var buf = hdrMs.GetBuffer();
                            long len = hdrMs.Length;
                            if (buf[len - 4] == termSeq[0] && buf[len - 3] == termSeq[1] &&
                                buf[len - 2] == termSeq[2] && buf[len - 1] == termSeq[3]) break;
                        }
                        if (hdrMs.Length > 256 * 1024) throw new IOException("Response header too large");
                    }

                    string hdrText = Encoding.UTF8.GetString(hdrMs.ToArray());
                    bool isChunked = hdrText.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0;

                    // Read and fire SSE events
                    void ProcessSSE(string text)
                    {
                        // Split on SSE double-newline boundaries
                        var segments = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var seg in segments)
                        {
                            foreach (var line in seg.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                string t = line.Trim();
                                if (!t.StartsWith("data:")) continue;
                                string payload = t.Substring(5).Trim();
                                _onData?.Invoke(payload == "[DONE]" ? "[DONE]" : payload);
                            }
                        }
                    }

                    byte[] readBuf = new byte[ReadBufferSize];

                    if (isChunked)
                    {
                        while (true)
                        {
                            // Read chunk size line
                            var sizeSb = new StringBuilder();
                            while (true)
                            {
                                int r = await net.ReadAsync(oneByte, 0, 1);
                                if (r <= 0) goto done;
                                sizeSb.Append((char)oneByte[0]);
                                if (sizeSb.Length >= 2 && sizeSb[sizeSb.Length - 2] == '\r' && sizeSb[sizeSb.Length - 1] == '\n') break;
                            }
                            string sizeLine = sizeSb.ToString().Trim();
                            int semi = sizeLine.IndexOf(';');
                            if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                            int chunkSize = 0;
                            try { chunkSize = Convert.ToInt32(sizeLine.Trim(), 16); } catch { }
                            if (chunkSize == 0) break; // last chunk

                            var chunkBuf = new byte[chunkSize];
                            int got = 0;
                            while (got < chunkSize)
                            {
                                int r = await net.ReadAsync(chunkBuf, got, chunkSize - got);
                                if (r <= 0) goto done;
                                got += r;
                            }
                            // consume trailing CRLF
                            await net.ReadAsync(new byte[2], 0, 2);
                            ProcessSSE(Encoding.UTF8.GetString(chunkBuf, 0, got));
                        }
                    }
                    else
                    {
                        int r;
                        while ((r = await net.ReadAsync(readBuf, 0, readBuf.Length)) > 0)
                            ProcessSSE(Encoding.UTF8.GetString(readBuf, 0, r));
                    }

                done:
                    _onData?.Invoke("[DONE]");
                } // end using client
            }
        }
    }
}