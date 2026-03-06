using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CustomLLMAPI.Lib
{
    /// <summary>
    /// A minimal inbound HTTP/1.1 server backed by raw TCP sockets.
    ///
    /// Exists because HttpListener requires manifest ACL entries on Windows and
    /// is unavailable in some Unity configurations.  The public surface mirrors
    /// HttpListener / HttpListenerContext so the swap is mechanical when possible:
    ///
    ///   MiniHttpServer:                      HttpListener equivalent:
    ///   ──────────────────────────────────── ──────────────────────────────────
    ///   new MiniHttpServer(port)        →    new HttpListener()
    ///   server.OnRequest += Handler     →    while(true) ctx = listener.GetContext()
    ///   await server.StartAsync()       →    listener.Start()
    ///   server.Stop()                   →    listener.Stop()
    ///   HttpContext.Method / Path       →    ctx.Request.HttpMethod / RawUrl
    ///   HttpContext.Body                →    new StreamReader(ctx.Request.InputStream)
    ///   ctx.SendJsonAsync(200, json)    →    ctx.Response.OutputStream.Write(...)
    ///   ctx.SendTextAsync(200, html,ct) →    (same with content-type)
    ///   ctx.SendSseAsync(payload)       →    ctx.Response.OutputStream.Write(sse)
    ///   ctx.SendOptionsAsync()          →    ctx.Response with Allow headers
    ///
    /// Callers register route logic via the <see cref="OnRequest"/> event.
    /// Multiple servers (on different ports) can run simultaneously.
    /// </summary>
    public class MiniHttpServer
    {
        // ── Config ────────────────────────────────────────────────────────────────

        /// <summary>Port this server is listening on (may change after port-fallback).</summary>
        public int Port { get; private set; }
        public bool IsRunning { get; private set; }

        /// <summary>How many port increments to try when the requested port is busy.</summary>
        public int MaxPortFallbacks { get; set; } = 10;

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired for every incoming request on a thread-pool thread.
        /// Handlers must be thread-safe.  If no handler is registered the server
        /// returns 501 Not Implemented.
        /// </summary>
        public event Func<HttpContext, Task> OnRequest;

        /// <summary>
        /// Fired (on the thread-pool) when the port changes due to fallback, so
        /// callers can update their UI / stored config.
        /// </summary>
        public event Action<int /*newPort*/> OnPortChanged;

        // ── Private state ─────────────────────────────────────────────────────────

        private Socket _listener;
        private Thread _acceptThread;
        private readonly string _tag;

        public MiniHttpServer(int port, string logTag = "MiniHttpServer")
        {
            Port = port;
            _tag = logTag;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <summary>Bind, listen, and start the accept loop.  Safe to await on any thread.</summary>
        public Task StartAsync()
        {
            if (_listener != null && IsRunning)
            {
                Debug.LogWarning($"[{_tag}] Already running on port {Port}.");
                return Task.CompletedTask;
            }

            try
            {
                _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);

                int original = Port;
                bool bound = false;

                for (int i = 0; i < MaxPortFallbacks; i++)
                {
                    try
                    {
                        _listener.Bind(new IPEndPoint(IPAddress.Loopback, Port));
                        bound = true;
                        break;
                    }
                    catch (SocketException se) when (se.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                        Port++;
                    }
                }

                if (!bound)
                    throw new Exception(
                        $"[{_tag}] Failed to bind after {MaxPortFallbacks} attempts " +
                        $"starting from port {original}.");

                if (Port != original)
                {
                    Debug.LogWarning($"[{_tag}] Port {original} in use — using {Port}.");
                    OnPortChanged?.Invoke(Port);
                }

                _listener.Listen(50);
                IsRunning = true;

                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = $"{_tag}-Accept"
                };
                _acceptThread.Start();

                Debug.Log($"[{_tag}] Listening on http://localhost:{Port}/");
            }
            catch (Exception e)
            {
                Debug.LogError($"[{_tag}] Failed to start: {e.Message}");
                throw;
            }

            return Task.CompletedTask;
        }

        public void Stop()
        {
            IsRunning = false;
            try { _listener?.Close(); } catch { }
            _listener = null;
            try
            {
                if (_acceptThread != null && _acceptThread.IsAlive)
                    _acceptThread.Join(200);
            }
            catch { }
            Debug.Log($"[{_tag}] Stopped.");
        }

        // ── Accept loop ───────────────────────────────────────────────────────────

        private void AcceptLoop()
        {
            Debug.Log($"[{_tag}] Accept thread running.");
            while (IsRunning)
            {
                try
                {
                    Socket client = _listener.Accept();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch (SocketException se)
                {
                    if (IsRunning) Debug.LogError($"[{_tag}] Accept error: {se.Message}");
                }
                catch (Exception e) { Debug.LogError($"[{_tag}] Accept thread error: {e.Message}"); }
            }
            Debug.Log($"[{_tag}] Accept thread exiting.");
        }

        // ── Per-connection handling ───────────────────────────────────────────────

        private void HandleClient(Socket socket)
        {
            try
            {
                using (socket)
                {
                    string raw = ReadAll(socket);
                    if (string.IsNullOrEmpty(raw)) return;

                    var ctx = HttpContext.Parse(raw, socket, _tag);
                    if (ctx == null) return;

                    // OPTIONS pre-flight — handled automatically
                    if (ctx.Method == "OPTIONS")
                    {
                        ctx.SendOptionsAsync().GetAwaiter().GetResult();
                        return;
                    }

                    if (OnRequest != null)
                        OnRequest(ctx).GetAwaiter().GetResult();
                    else
                        ctx.SendJsonAsync(501, "{\"error\":\"no handler registered\"}")
                           .GetAwaiter().GetResult();
                }
            }
            catch (Exception e) { Debug.LogError($"[{_tag}] HandleClient error: {e.Message}"); }
        }

        // ── Socket read ───────────────────────────────────────────────────────────

        private string ReadAll(Socket socket)
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

                        if (ms.Length > 20 * 1024 * 1024)
                        {
                            Debug.LogError($"[{_tag}] Request too large, dropping.");
                            return null;
                        }

                        string s = Encoding.UTF8.GetString(ms.ToArray());
                        int headerEnd = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd < 0) continue;

                        // Parse Content-Length and wait for full body
                        int contentLength = 0;
                        foreach (var line in s.Substring(0, headerEnd)
                                             .Split(new[] { "\r\n" }, StringSplitOptions.None))
                        {
                            int colon = line.IndexOf(':');
                            if (colon > 0 &&
                                line.Substring(0, colon).Trim()
                                    .Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(line.Substring(colon + 1).Trim(), out contentLength);
                        }

                        int bodyAlready = total - (headerEnd + 4);
                        int need = contentLength - bodyAlready;
                        while (need > 0)
                        {
                            int r = socket.Receive(buf, 0, Math.Min(buf.Length, need), SocketFlags.None);
                            if (r <= 0) break;
                            ms.Write(buf, 0, r);
                            total += r;
                            need -= r;
                        }
                        break;
                    }
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[{_tag}] ReadAll error: {e.Message}");
                return null;
            }
        }
    }

    // ── HttpContext ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents a single inbound HTTP request and exposes response helpers.
    /// Mirrors the members of <c>HttpListenerContext</c> used in practice.
    /// </summary>
    public class HttpContext
    {
        // ── Request ───────────────────────────────────────────────────────────────

        public string Method { get; private set; }
        public string Path { get; private set; }
        public string Body { get; private set; }
        public Dictionary<string, string> Headers { get; private set; }

        // ── Private ───────────────────────────────────────────────────────────────

        private Socket _socket;
        private string _tag;

        private HttpContext() { }

        internal static HttpContext Parse(string raw, Socket socket, string tag)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            var lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            var rl = lines[0].Split(' ');

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int bodyStart = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrEmpty(lines[i])) { bodyStart = i + 1; break; }
                int sep = lines[i].IndexOf(": ", StringComparison.Ordinal);
                if (sep > 0) headers[lines[i].Substring(0, sep)] = lines[i].Substring(sep + 2);
            }

            string body = bodyStart > 0 && bodyStart < lines.Length
                ? string.Join("\r\n", lines, bodyStart, lines.Length - bodyStart)
                : "";

            return new HttpContext
            {
                Method = rl.Length > 0 ? rl[0] : "GET",
                Path = rl.Length > 1 ? rl[1].Split('?')[0] : "/",
                Body = body,
                Headers = headers,
                _socket = socket,
                _tag = tag
            };
        }

        // ── Response helpers ──────────────────────────────────────────────────────

        /// <summary>Send a JSON body.  Mirrors writing to HttpListenerResponse.</summary>
        public Task SendJsonAsync(int status, string json) =>
            SendTextAsync(status, json, "application/json");

        /// <summary>Send a plain-text or HTML body.</summary>
        public Task SendTextAsync(int status, string body, string contentType = "text/plain; charset=utf-8")
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
            string phrase = StatusPhrase(status);
            string header =
                $"HTTP/1.1 {status} {phrase}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Connection: close\r\n\r\n";

            WriteBytes(Encoding.UTF8.GetBytes(header));
            WriteBytes(bodyBytes);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Send a single payload as a chunked SSE response.
        /// LLMUnity expects the llama.cpp format wrapped in SSE framing.
        /// </summary>
        public Task SendSseAsync(string jsonPayload, string terminator = null)
        {
            if (terminator == null) terminator = "{\"content\":\"\",\"stop\":true,\"id_slot\":0}";
            string sse = $"data: {jsonPayload}\r\n\r\ndata: {terminator}\r\n\r\n";
            byte[] sseBytes = Encoding.UTF8.GetBytes(sse);
            string chunkHex = sseBytes.Length.ToString("x");
            string full =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "Connection: close\r\n\r\n" +
                chunkHex + "\r\n" + sse + "\r\n0\r\n\r\n";

            WriteBytes(Encoding.UTF8.GetBytes(full));
            return Task.CompletedTask;
        }

        /// <summary>Respond to CORS OPTIONS pre-flight.</summary>
        public Task SendOptionsAsync()
        {
            WriteBytes(Encoding.UTF8.GetBytes(
                "HTTP/1.1 204 No Content\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type\r\n" +
                "Connection: close\r\n\r\n"));
            return Task.CompletedTask;
        }

        // ── Socket write ──────────────────────────────────────────────────────────

        private void WriteBytes(byte[] data)
        {
            try
            {
                int sent = 0;
                while (sent < data.Length)
                {
                    int n = _socket.Send(data, sent, data.Length - sent, SocketFlags.None);
                    if (n <= 0) break;
                    sent += n;
                }
            }
            catch (Exception e) { Debug.LogError($"[{_tag}] Write error: {e.Message}"); }
        }

        private static string StatusPhrase(int code)
        {
            if (code == 200) return "OK";
            if (code == 204) return "No Content";
            if (code == 404) return "Not Found";
            if (code == 500) return "Internal Server Error";
            if (code == 501) return "Not Implemented";
            return "Unknown";
        }
    }
}