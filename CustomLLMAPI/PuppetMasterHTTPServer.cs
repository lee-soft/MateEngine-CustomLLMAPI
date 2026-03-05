using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace CustomLLMAPI
{
    /// <summary>
    /// PuppetMasterHttpServer — raw TCP HTTP server that exposes PuppetMasterActions
    /// over a local REST API and serves the web control panel.
    ///
    /// This class owns all socket, threading, and JSON serialisation concerns.
    /// It holds no avatar state of its own; every action is delegated to
    /// <see cref="PuppetMasterActions"/> via the main-thread queue provided by
    /// <see cref="PuppetMaster"/>.
    ///
    /// Endpoints:
    ///   GET  /              -> Web control panel UI
    ///   GET  /ping          -> Health check
    ///   GET  /status        -> JSON current state
    ///   GET  /mood/list     -> JSON array of mood names for the current avatar
    ///   POST /mood/reload   -> Reload MoodProfile.json from disk
    ///   POST /mood          -> body: {"mood":"Joy"}
    ///   POST /dance/start   -> body: {"index":0}
    ///   POST /dance/stop
    ///   POST /walk/start
    ///   POST /walk/stop
    ///   POST /message       -> body: {"text":"Hello!"}
    ///   POST /bigscreen     -> body: {"active":true|false}  (omit to toggle)
    ///   GET  /animations    -> JSON array of exposed animator parameters
    ///   POST /animation/trigger -> body: {"param":"...", "value":"true|false"}
    ///   POST /headpat
    ///   GET  /blendshapes   -> JSON blendshape list for debug / profile authoring
    ///   GET  /size          -> JSON {"size": 1.0}
    ///   POST /size          -> body: {"size": 1.5}  (clamped 0.1–5.0)
    /// </summary>
    public class PuppetMasterHttpServer
    {
        private readonly int _port;
        private readonly PuppetMasterActions _actions;
        private readonly Func<Func<string>, string> _enqueueAndWait;

        private Socket _listener;
        private Thread _acceptThread;
        private bool _running;

        /// <param name="port">TCP port to listen on.</param>
        /// <param name="actions">The avatar action surface (must be called on main thread).</param>
        /// <param name="enqueueAndWait">
        ///   Callback that marshals a <c>Func&lt;string&gt;</c> onto the Unity main thread,
        ///   blocks until it completes, and returns its result.
        /// </param>
        public PuppetMasterHttpServer(int port, PuppetMasterActions actions, Func<Func<string>, string> enqueueAndWait)
        {
            _port = port;
            _actions = actions;
            _enqueueAndWait = enqueueAndWait;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Start()
        {
            if (_running) return;
            try
            {
                _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                _listener.Bind(new IPEndPoint(IPAddress.Loopback, _port));
                _listener.Listen(20);
            }
            catch (Exception ex)
            {
                Debug.LogError("[PuppetMasterHttp] Failed to bind port " + _port + ": " + ex.Message);
                return;
            }
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "PuppetMaster-Accept" };
            _acceptThread.Start();
            Debug.Log("[PuppetMasterHttp] Running -> http://localhost:" + _port + "/");
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Close(); } catch { }
            Debug.Log("[PuppetMasterHttp] Stopped.");
        }

        // ── Accept / dispatch ─────────────────────────────────────────────────

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    Socket client = _listener.Accept();
                    Socket cap = client;
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(cap));
                }
                catch (SocketException) { break; }
                catch (Exception ex) { Debug.LogError("[PuppetMasterHttp] Accept error: " + ex.Message); }
            }
        }

        private void HandleClient(Socket client)
        {
            try
            {
                using (client)
                {
                    string raw = SocketReadAll(client);
                    if (string.IsNullOrEmpty(raw)) return;

                    ParseHttp(raw, out string method, out string path, out string body);

                    if (method == "OPTIONS")
                    {
                        SocketWrite(client,
                            "HTTP/1.1 204 No Content\r\n" +
                            "Access-Control-Allow-Origin: *\r\n" +
                            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                            "Access-Control-Allow-Headers: Content-Type\r\n" +
                            "Connection: close\r\n\r\n");
                        return;
                    }

                    Route(client, method, path, body);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[PuppetMasterHttp] HandleClient error: " + ex.Message);
            }
        }

        private void Route(Socket client, string method, string path, string body)
        {
            if (method == "GET" && (path == "/" || path == "/index.html"))
            { SendText(client, 200, PuppetMasterUI.Build(_port), "text/html; charset=utf-8"); return; }

            if (method == "GET" && path == "/ping")
            { SendText(client, 200, "pong", "text/plain; charset=utf-8"); return; }

            if (method == "GET" && path == "/status") { RouteStatus(client); return; }
            if (method == "GET" && path == "/mood/list") { RouteMoodList(client); return; }
            if (method == "POST" && path == "/mood/reload") { RouteMoodReload(client); return; }
            if (method == "POST" && path == "/mood") { RouteMood(client, body); return; }
            if (method == "POST" && path == "/dance/start") { RouteDanceStart(client, body); return; }
            if (method == "POST" && path == "/dance/stop") { RouteDanceStop(client); return; }
            if (method == "POST" && path == "/walk/start") { RouteWalkStart(client); return; }
            if (method == "POST" && path == "/walk/stop") { RouteWalkStop(client); return; }
            if (method == "POST" && path == "/message") { RouteMessage(client, body); return; }
            if (method == "POST" && path == "/bigscreen") { RouteBigScreen(client, body); return; }
            if (method == "GET" && path == "/animations") { RouteAnimations(client); return; }
            if (method == "POST" && path == "/animation/trigger") { RouteAnimTrigger(client, body); return; }
            if (method == "POST" && path == "/headpat") { RouteHeadpat(client); return; }
            if (method == "GET" && path == "/blendshapes") { RouteBlendshapes(client); return; }
            if (method == "GET" && path == "/size") { RouteSizeGet(client); return; }
            if (method == "POST" && path == "/size") { RouteSizeSet(client, body); return; }

            SendText(client, 404, "{\"error\":\"not found\"}", "application/json");
        }

        // ── Route handlers ────────────────────────────────────────────────────

        private void RouteStatus(Socket client)
        {
            string json = MainThread(() =>
            {
                var s = _actions.GetStatus();
                return "{\"mood\":\"" + s.mood + "\"" +
                       ",\"dancing\":" + Bool(s.dancing) +
                       ",\"walking\":" + Bool(s.walking) +
                       ",\"bigscreen\":" + Bool(s.bigscreen) +
                       ",\"avatar\":\"" + s.avatar + "\"" +
                       ",\"size\":" + s.size.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}";
            });
            SendText(client, 200, json, "application/json");
        }

        private void RouteMoodList(Socket client)
        {
            string json = MainThread(() =>
            {
                var names = _actions.GetMoodList();
                return "[" + string.Join(",", names.ConvertAll(n => "\"" + n + "\"")) + "]";
            });
            SendText(client, 200, json, "application/json");
        }

        private void RouteMoodReload(Socket client)
        {
            string json = MainThread(() =>
            {
                int count = _actions.ReloadMoodProfile();
                return "{\"status\":\"ok\",\"moods\":" + count + "}";
            });
            SendText(client, 200, json, "application/json");
        }

        private void RouteMood(Socket client, string body)
        {
            string mood = ParseString(body, "mood", "Neutral");
            string json = MainThread(() =>
            {
                string r = _actions.SetMood(mood);
                return "{\"status\":\"" + r + "\",\"mood\":\"" + mood + "\"}";
            });
            SendText(client, 200, json, "application/json");
        }

        private void RouteDanceStart(Socket client, string body)
        {
            int index = ParseInt(body, "index", 0);
            string json = MainThread(() => StatusJson(_actions.StartDance(index)));
            SendText(client, 200, json, "application/json");
        }

        private void RouteDanceStop(Socket client)
        {
            string json = MainThread(() => StatusJson(_actions.StopDance()));
            SendText(client, 200, json, "application/json");
        }

        private void RouteWalkStart(Socket client)
        {
            string json = MainThread(() => StatusJson(_actions.StartWalk()));
            SendText(client, 200, json, "application/json");
        }

        private void RouteWalkStop(Socket client)
        {
            string json = MainThread(() => StatusJson(_actions.StopWalk()));
            SendText(client, 200, json, "application/json");
        }

        private void RouteMessage(Socket client, string body)
        {
            string text = ParseString(body, "text", "Hello!");
            string json = MainThread(() => StatusJson(_actions.ShowMessage(text)));
            SendText(client, 200, json, "application/json");
        }

        private void RouteBigScreen(Socket client, string body)
        {
            bool? active = ParseBoolOrNull(body, "active");
            string json = MainThread(() =>
            {
                string r = _actions.SetBigScreen(active);
                return "{\"status\":\"" + r + "\",\"bigscreen\":" + Bool(_actions.IsBigScreen) + "}";
            });
            SendText(client, 200, json, "application/json");
        }

        private void RouteAnimations(Socket client)
        {
            string json = MainThread(() =>
            {
                var list = _actions.GetAnimations();
                var sb = new StringBuilder("[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"name\":\"" + list[i].name + "\",\"type\":\"" + list[i].type + "\"}");
                }
                sb.Append("]");
                return sb.ToString();
            });
            SendText(client, 200, json, "application/json");
        }

        private void RouteAnimTrigger(Socket client, string body)
        {
            string param = ParseString(body, "param", null);
            bool value = ParseBool(body, "value", true);
            string json = MainThread(() => StatusJson(_actions.TriggerAnimation(param, value)));
            SendText(client, 200, json, "application/json");
        }

        private void RouteHeadpat(Socket client)
        {
            string json = MainThread(() => StatusJson(_actions.Headpat()));
            SendText(client, 200, json, "application/json");
        }

        private void RouteBlendshapes(Socket client)
        {
            string json = MainThread(() =>
            {
                var list = _actions.GetBlendshapes();
                var outer = new StringBuilder("[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) outer.Append(",");
                    outer.Append("{\"mesh\":\"" + list[i].mesh + "\",\"shapes\":[");
                    outer.Append(string.Join(",", list[i].shapes.ConvertAll(s => "\"" + s + "\"")));
                    outer.Append("]}");
                }
                outer.Append("]");
                return outer.ToString();
            });
            SendText(client, 200, json, "application/json");
        }

        private void RouteSizeGet(Socket client)
        {
            string json = MainThread(() =>
            {
                float size = _actions.GetAvatarSize();
                return "{\"size\":" + size.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}";
            });
            SendText(client, 200, json, "application/json");
        }

        private void RouteSizeSet(Socket client, string body)
        {
            float size = ParseFloat(body, "size", 1f);
            string json = MainThread(() => StatusJson(_actions.SetAvatarSize(size)));
            SendText(client, 200, json, "application/json");
        }

        // ── Main-thread bridge ────────────────────────────────────────────────

        /// Marshals a Func<string> onto the Unity main thread and returns its result.
        private string MainThread(Func<string> work) => _enqueueAndWait(() => work());

        // ── JSON helpers ──────────────────────────────────────────────────────

        private static string StatusJson(string result) => "{\"status\":\"" + result + "\"}";
        private static string Bool(bool v) => v ? "true" : "false";

        private static string ParseString(string body, string key, string fallback)
        {
            try
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(body ?? "{}");
                if (d != null && d.TryGetValue(key, out var v)) return v;
            }
            catch { }
            return fallback;
        }

        private static int ParseInt(string body, string key, int fallback)
        {
            try
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, int>>(body ?? "{}");
                if (d != null && d.TryGetValue(key, out var v)) return v;
            }
            catch { }
            return fallback;
        }

        private static bool ParseBool(string body, string key, bool fallback)
        {
            try
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(body ?? "{}");
                if (d != null && d.TryGetValue(key, out var v))
                    return v != "false" && v != "0";
            }
            catch { }
            return fallback;
        }

        private static float ParseFloat(string body, string key, float fallback)
        {
            try
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, float>>(body ?? "{}");
                if (d != null && d.TryGetValue(key, out var v)) return v;
            }
            catch { }
            return fallback;
        }

        /// Returns null if the key is absent (caller interprets null as "toggle").
        private static bool? ParseBoolOrNull(string body, string key)
        {
            try
            {
                string b = (body ?? "").Trim();
                int idx = b.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (idx < 0) return null;
                int colon = b.IndexOf(':', idx);
                if (colon < 0) return null;
                string val = b.Substring(colon + 1).Trim().TrimEnd('}').Trim();
                if (val.StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (val.StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { }
            return null;
        }

        // ── Socket helpers ────────────────────────────────────────────────────

        private static string SocketReadAll(Socket s)
        {
            var sb = new StringBuilder();
            byte[] buf = new byte[4096];
            int contentLength = 0;
            bool headersDone = false;
            int bodyRead = 0;

            while (true)
            {
                int n = 0;
                try { IAsyncResult ar = s.BeginReceive(buf, 0, buf.Length, SocketFlags.None, null, null); n = s.EndReceive(ar); }
                catch { break; }
                if (n <= 0) break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                string sofar = sb.ToString();

                if (!headersDone)
                {
                    int split = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (split < 0) continue;
                    headersDone = true;
                    foreach (string line in sofar.Substring(0, split).Split(new[] { "\r\n" }, StringSplitOptions.None))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line.Substring(15).Trim(), out contentLength);
                    bodyRead = sofar.Length - split - 4;
                }
                else { bodyRead += n; }

                if (headersDone && bodyRead >= contentLength) break;
            }
            return sb.ToString();
        }

        private static void ParseHttp(string raw, out string method, out string path, out string body)
        {
            method = "GET"; path = "/"; body = null;
            int headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headers = headerEnd >= 0 ? raw.Substring(0, headerEnd) : raw;
            string[] lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] parts = lines[0].Split(' ');
            if (parts.Length >= 2) { method = parts[0]; path = parts[1].Split('?')[0]; }
            int contentLength = 0;
            foreach (string line in lines)
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.Substring(15).Trim(), out contentLength);
            if (headerEnd >= 0 && contentLength > 0)
                body = raw.Substring(headerEnd + 4, Math.Min(contentLength, raw.Length - headerEnd - 4));
        }

        private static void SendText(Socket s, int code, string body, string ct)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string statusText = code == 200 ? "OK" : code == 204 ? "No Content" : "Not Found";
            string header =
                "HTTP/1.1 " + code + " " + statusText + "\r\n" +
                "Content-Type: " + ct + "\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Connection: close\r\n\r\n";
            SocketWrite(s, header);
            SocketWriteBytes(s, bodyBytes);
        }

        private static void SocketWrite(Socket s, string text) =>
            SocketWriteBytes(s, Encoding.UTF8.GetBytes(text));

        private static void SocketWriteBytes(Socket s, byte[] data)
        {
            int sent = 0;
            while (sent < data.Length)
            {
                IAsyncResult ar = s.BeginSend(data, sent, data.Length - sent, SocketFlags.None, null, null);
                int n = s.EndSend(ar);
                if (n <= 0) break;
                sent += n;
            }
        }
    }







}