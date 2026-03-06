using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using CustomLLMAPI.Lib;
using Newtonsoft.Json;
using UnityEngine;

namespace CustomLLMAPI.PuppetMaster
{
    /// <summary>
    /// PuppetMasterHttpServer — exposes PuppetMasterActions over a local REST API
    /// and serves the web control panel.
    ///
    /// All socket / threading / HTTP framing is now delegated to
    /// <see cref="MiniHttpServer"/>.  This class owns only routing and
    /// action dispatch.
    ///
    /// Endpoints:
    ///   GET  /                    -> Web control panel UI
    ///   GET  /ping                -> Health check
    ///   GET  /status              -> JSON current state
    ///   GET  /mood/list           -> JSON array of mood names
    ///   POST /mood/reload         -> Reload MoodProfile.json from disk
    ///   POST /mood                -> body: {"mood":"Joy"}
    ///   POST /dance/start         -> body: {"index":0}
    ///   POST /dance/stop
    ///   POST /walk/start
    ///   POST /walk/stop
    ///   POST /message             -> body: {"text":"Hello!"}
    ///   POST /bigscreen           -> body: {"active":true|false}  (omit to toggle)
    ///   GET  /animations          -> JSON array of animator parameters
    ///   POST /animation/trigger   -> body: {"param":"...", "value":"true|false"}
    ///   POST /headpat
    ///   GET  /blendshapes         -> JSON blendshape list
    ///   GET  /size                -> JSON {"size": 1.0}
    ///   POST /size                -> body: {"size": 1.5}
    ///   GET  /windows             -> JSON window title list
    ///   GET  /windows/visible     -> JSON visible window title list
    ///   POST /window/snap         -> body: {"title":"..."}
    ///   POST /window/snap/focused
    ///   POST /window/unsit
    /// </summary>
    public class PuppetMasterHttpServer
    {
        private readonly PuppetMasterActions _actions;
        private readonly Func<Func<string>, string> _enqueueAndWait;
        private MiniHttpServer _server;

        public bool IsRunning => _server?.IsRunning ?? false;
        public int Port => _server?.Port ?? 0;

        /// <param name="port">TCP port to listen on.</param>
        /// <param name="actions">Avatar action surface (must be called on main thread).</param>
        /// <param name="enqueueAndWait">
        ///   Marshals a Func&lt;string&gt; onto the Unity main thread,
        ///   blocks until it completes, and returns its result.
        /// </param>
        public PuppetMasterHttpServer(
            int port,
            PuppetMasterActions actions,
            Func<Func<string>, string> enqueueAndWait)
        {
            _actions = actions;
            _enqueueAndWait = enqueueAndWait;
            _server = new MiniHttpServer(port, "PuppetMasterHttp");
            _server.OnRequest += Route;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Start() => _server.StartAsync().GetAwaiter().GetResult();
        public void Stop() => _server.Stop();

        // ── Routing ───────────────────────────────────────────────────────────

        private Task Route(HttpContext ctx)
        {
            string m = ctx.Method;
            string p = ctx.Path;

            if (m == "GET" && (p == "/" || p == "/index.html"))
                return ctx.SendTextAsync(200, PuppetMasterUI.Build(Port), "text/html; charset=utf-8");
            if (m == "GET" && p == "/ping")
                return ctx.SendTextAsync(200, "pong", "text/plain; charset=utf-8");

            if (m == "GET" && p == "/status") return RouteStatus(ctx);
            if (m == "GET" && p == "/mood/list") return RouteMoodList(ctx);
            if (m == "POST" && p == "/mood/reload") return RouteMoodReload(ctx);
            if (m == "POST" && p == "/mood") return RouteMood(ctx);
            if (m == "POST" && p == "/dance/start") return RouteDanceStart(ctx);
            if (m == "POST" && p == "/dance/stop") return RouteDanceStop(ctx);
            if (m == "POST" && p == "/walk/start") return RouteWalkStart(ctx);
            if (m == "POST" && p == "/walk/stop") return RouteWalkStop(ctx);
            if (m == "POST" && p == "/message") return RouteMessage(ctx);
            if (m == "POST" && p == "/bigscreen") return RouteBigScreen(ctx);
            if (m == "GET" && p == "/animations") return RouteAnimations(ctx);
            if (m == "POST" && p == "/animation/trigger") return RouteAnimTrigger(ctx);
            if (m == "POST" && p == "/headpat") return RouteHeadpat(ctx);
            if (m == "GET" && p == "/blendshapes") return RouteBlendshapes(ctx);
            if (m == "GET" && p == "/size") return RouteSizeGet(ctx);
            if (m == "POST" && p == "/size") return RouteSizeSet(ctx);
            if (m == "GET" && p == "/windows") return RouteWindowList(ctx);
            if (m == "GET" && p == "/windows/visible") return RouteVisibleWindowList(ctx);
            if (m == "POST" && p == "/window/snap") return RouteWindowSnap(ctx);
            if (m == "POST" && p == "/window/snap/focused") return RouteWindowSnapFocused(ctx);
            if (m == "POST" && p == "/window/unsit") return RouteWindowUnsit(ctx);

            return ctx.SendJsonAsync(404, "{\"error\":\"not found\"}");
        }

        // ── Route handlers ────────────────────────────────────────────────────

        private Task RouteStatus(HttpContext ctx)
        {
            string json = MainThread(() =>
            {
                var s = _actions.GetStatus();
                return "{\"mood\":\"" + s.mood + "\"" +
                       ",\"dancing\":" + Bool(s.dancing) +
                       ",\"walking\":" + Bool(s.walking) +
                       ",\"bigscreen\":" + Bool(s.bigscreen) +
                       ",\"avatar\":\"" + s.avatar + "\"" +
                       ",\"size\":" + s.size.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                       ",\"isWindowSit\":" + Bool(s.isWindowSit) +
                       ",\"snappedWindowTitle\":\"" + (s.snappedWindowTitle ?? "").Replace("\"", "\\\"") + "\"}";
            });
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteMoodList(HttpContext ctx)
        {
            string json = MainThread(() =>
            {
                var names = _actions.GetMoodList();
                return "[" + string.Join(",", names.ConvertAll(n => "\"" + n + "\"")) + "]";
            });
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteMoodReload(HttpContext ctx)
        {
            string json = MainThread(() =>
            {
                int count = _actions.ReloadMoodProfile();
                return "{\"status\":\"ok\",\"moods\":" + count + "}";
            });
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteMood(HttpContext ctx)
        {
            string mood = ParseString(ctx.Body, "mood", "Neutral");
            string json = MainThread(() =>
            {
                string r = _actions.SetMood(mood);
                return "{\"status\":\"" + r + "\",\"mood\":\"" + mood + "\"}";
            });
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteDanceStart(HttpContext ctx)
        {
            int index = ParseInt(ctx.Body, "index", 0);
            string json = MainThread(() => StatusJson(_actions.StartDance(index)));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteDanceStop(HttpContext ctx)
        {
            string json = MainThread(() => StatusJson(_actions.StopDance()));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteWalkStart(HttpContext ctx)
        {
            string json = MainThread(() => StatusJson(_actions.StartWalk()));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteWalkStop(HttpContext ctx)
        {
            string json = MainThread(() => StatusJson(_actions.StopWalk()));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteMessage(HttpContext ctx)
        {
            string text = ParseString(ctx.Body, "text", "Hello!");
            string json = MainThread(() => StatusJson(_actions.ShowMessage(text)));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteBigScreen(HttpContext ctx)
        {
            bool? active = ParseBoolOrNull(ctx.Body, "active");
            string json = MainThread(() =>
            {
                string r = _actions.SetBigScreen(active);
                return "{\"status\":\"" + r + "\",\"bigscreen\":" + Bool(_actions.IsBigScreen) + "}";
            });
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteAnimations(HttpContext ctx)
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
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteAnimTrigger(HttpContext ctx)
        {
            string param = ParseString(ctx.Body, "param", null);
            bool value = ParseBool(ctx.Body, "value", true);
            string json = MainThread(() => StatusJson(_actions.TriggerAnimation(param, value)));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteHeadpat(HttpContext ctx)
        {
            string json = MainThread(() => StatusJson(_actions.Headpat()));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteBlendshapes(HttpContext ctx)
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
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteSizeGet(HttpContext ctx)
        {
            string json = MainThread(() =>
            {
                float size = _actions.GetAvatarSize();
                return "{\"size\":" + size.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}";
            });
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteSizeSet(HttpContext ctx)
        {
            float size = ParseFloat(ctx.Body, "size", 1f);
            string json = MainThread(() => StatusJson(_actions.SetAvatarSize(size)));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteWindowList(HttpContext ctx)
        {
            string json = MainThread(() =>
            {
                var list = _actions.GetWindowList();
                return "[" + string.Join(",", list.ConvertAll(
                    t => "\"" + t.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";
            });
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteVisibleWindowList(HttpContext ctx)
        {
            string json = MainThread(() =>
            {
                var list = _actions.GetVisibleWindowList();
                return "[" + string.Join(",", list.ConvertAll(
                    t => "\"" + t.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";
            });
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteWindowSnap(HttpContext ctx)
        {
            string title = ParseString(ctx.Body, "title", "");
            string json = MainThread(() => StatusJson(_actions.SnapToWindow(title)));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteWindowSnapFocused(HttpContext ctx)
        {
            string json = MainThread(() => StatusJson(_actions.SnapToFocusedWindow()));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        private Task RouteWindowUnsit(HttpContext ctx)
        {
            string json = MainThread(() => StatusJson(_actions.Unsit()));
            return ctx.SendTextAsync(200, json, "application/json");
        }

        // ── Main-thread bridge ────────────────────────────────────────────────

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
    }
}