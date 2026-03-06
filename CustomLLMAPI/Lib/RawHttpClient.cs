using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CustomLLMAPI.Lib
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using UnityEngine;

    /// <summary>
    /// A minimal HTTP/1.1 client backed by raw TcpClient + SslStream.
    ///
    /// Exists because UnityWebRequest is unavailable on background threads (and in
    /// some Unity/platform configurations altogether).  The public surface is
    /// deliberately shaped to mirror UnityWebRequest so that the call-sites only
    /// need mechanical changes when we eventually swap the implementation:
    ///
    ///   RawHttpClient:                       UnityWebRequest equivalent:
    ///   ─────────────────────────────────    ───────────────────────────────────
    ///   PostAsync(url, json, headers)   →    UnityWebRequest.Post(url, json)
    ///   RawHttpResponse.IsSuccess       →    !request.isNetworkError
    ///   RawHttpResponse.Text            →    request.downloadHandler.text
    ///   RawHttpResponse.StatusCode      →    request.responseCode
    ///
    /// Only POST + JSON is supported for now (that is all the proxy needs).
    /// SSE / streaming is handled by <see cref="SseStreamReader"/>.
    /// </summary>
    public static class RawHttpClient
    {
        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// POST <paramref name="jsonBody"/> to <paramref name="url"/> and return
        /// the full response body as a string.
        /// </summary>
        public static async Task<RawHttpResponse> PostAsync(
            string url,
            string jsonBody,
            Dictionary<string, string> headers = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url))
                return RawHttpResponse.Failure(0, "URL is null or empty.");

            Uri uri;
            try { uri = new Uri(url); }
            catch (Exception e) { return RawHttpResponse.Failure(0, $"Bad URL: {e.Message}"); }

            string host = uri.Host;
            int port = uri.Port == -1 ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
            string pathQuery = uri.PathAndQuery;
            bool useSsl = uri.Scheme == "https";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody ?? "");

            try
            {
                using (var tcp = new TcpClient())
                {
                    await tcp.ConnectAsync(host, port).WithCancellation(ct);
                    Stream net = tcp.GetStream();

                    if (useSsl)
                    {
                        var ssl = new SslStream(net, false, (_, __, ___, ____) => true);
                        await ssl.AuthenticateAsClientAsync(
                            host, null,
                            System.Security.Authentication.SslProtocols.None,
                            checkCertificateRevocation: false)
                            .WithCancellation(ct);
                        net = ssl;
                    }

                    // ── Write request ──────────────────────────────────────────
                    var hdr = new StringBuilder();
                    hdr.Append($"POST {pathQuery} HTTP/1.1\r\n");
                    hdr.Append($"Host: {host}\r\n");
                    hdr.Append("Content-Type: application/json\r\n");
                    hdr.Append($"Content-Length: {bodyBytes.Length}\r\n");
                    hdr.Append("Connection: close\r\n");
                    if (headers != null)
                        foreach (var kv in headers)
                            hdr.Append($"{kv.Key}: {kv.Value}\r\n");
                    hdr.Append("\r\n");

                    byte[] hdrBytes = Encoding.UTF8.GetBytes(hdr.ToString());
                    await net.WriteAsync(hdrBytes, 0, hdrBytes.Length, ct);
                    if (bodyBytes.Length > 0)
                        await net.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);

                    // ── Read raw response ──────────────────────────────────────
                    var ms = new MemoryStream();
                    var buf = new byte[8192];
                    int read;
                    while ((read = await net.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                        ms.Write(buf, 0, read);

                    return ParseResponse(ms.ToArray());
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Debug.LogWarning($"[RawHttpClient] POST {url} failed: {e.Message}");
                return RawHttpResponse.Failure(0, e.Message);
            }
        }

        // ── Response parsing ──────────────────────────────────────────────────────

        private static RawHttpResponse ParseResponse(byte[] raw)
        {
            // Find \r\n\r\n separator between headers and body
            int headerEnd = FindHeaderEnd(raw);
            if (headerEnd < 0)
            {
                Debug.LogError("[RawHttpClient] Could not find end of HTTP headers.");
                return RawHttpResponse.Failure(0, "Malformed HTTP response (no header end).");
            }

            string headerBlock = Encoding.ASCII.GetString(raw, 0, headerEnd);
            int statusCode = ParseStatusCode(headerBlock);
            bool chunked = headerBlock.IndexOf("Transfer-Encoding: chunked",
                                     StringComparison.OrdinalIgnoreCase) >= 0;

            int bodyStart = headerEnd + 4; // skip \r\n\r\n

            string bodyText = chunked
                ? DecodeChunkedBody(raw, bodyStart)
                : Encoding.UTF8.GetString(raw, bodyStart, Math.Max(0, raw.Length - bodyStart));

            return new RawHttpResponse(statusCode, bodyText);
        }

        private static int FindHeaderEnd(byte[] data)
        {
            // Search for \r\n\r\n (0D 0A 0D 0A)
            for (int i = 0; i <= data.Length - 4; i++)
                if (data[i] == 0x0D && data[i + 1] == 0x0A && data[i + 2] == 0x0D && data[i + 3] == 0x0A)
                    return i;
            return -1;
        }

        private static int ParseStatusCode(string headerBlock)
        {
            // "HTTP/1.1 200 OK"
            var firstLine = headerBlock.Split(new[] { "\r\n" }, 2, StringSplitOptions.None)[0];
            var parts = firstLine.Split(' ');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int code))
                return code;
            return 0;
        }

        /// <summary>
        /// Decodes HTTP chunked transfer-encoding directly from the raw byte array,
        /// which avoids UTF-8 multi-byte characters being split across chunk
        /// boundaries (a bug that appears when working on the decoded string).
        /// </summary>
        private static string DecodeChunkedBody(byte[] data, int pos)
        {
            var result = new MemoryStream();

            while (pos < data.Length)
            {
                // Read the hex chunk-size line, terminated by \r\n
                int lineStart = pos;
                while (pos < data.Length - 1 &&
                       !(data[pos] == '\r' && data[pos + 1] == '\n'))
                    pos++;
                if (pos >= data.Length - 1) break;

                string sizeLine = Encoding.ASCII
                    .GetString(data, lineStart, pos - lineStart).Trim();
                pos += 2; // skip \r\n

                // Strip chunk extensions (e.g. "1a; ext=foo")
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi).Trim();

                int chunkSize;
                try { chunkSize = Convert.ToInt32(sizeLine, 16); }
                catch { break; }

                if (chunkSize == 0) break; // terminal chunk

                int available = Math.Min(chunkSize, data.Length - pos);
                result.Write(data, pos, available);
                pos += chunkSize + 2; // skip data + trailing \r\n
            }

            return Encoding.UTF8.GetString(result.ToArray());
        }
    }

    // ── Response DTO ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the relevant members of UnityWebRequest / DownloadHandler.
    /// </summary>
    public class RawHttpResponse
    {
        public readonly int StatusCode;
        public readonly string Text;
        public readonly string Error;

        /// <summary>True when StatusCode is 2xx and no network error occurred.</summary>
        public bool IsSuccess => Error == null && StatusCode >= 200 && StatusCode < 300;

        public RawHttpResponse(int statusCode, string text)
        {
            StatusCode = statusCode;
            Text = text;
            Error = null;
        }

        private RawHttpResponse(int statusCode, string text, string error)
        {
            StatusCode = statusCode;
            Text = text;
            Error = error;
        }

        internal static RawHttpResponse Failure(int code, string error) =>
            new RawHttpResponse(code, null, error);
    }

    // ── SSE streaming reader ──────────────────────────────────────────────────────

    /// <summary>
    /// Streams Server-Sent Events from an HTTP endpoint, firing
    /// <see cref="OnData"/> for each payload line.
    ///
    /// Mirrors the shape of UnityWebRequest + DownloadHandlerScript so that the
    /// swap is mechanical:
    ///   SseStreamReader.SendAsync(url, body, headers, onData)
    ///   → UnityWebRequest.Post(url, body) + custom DownloadHandler
    /// </summary>
    public class SseStreamReader
    {
        private const int ReadBufferSize = 8192;

        private readonly Action<string> _onData;

        public SseStreamReader(Action<string> onData) => _onData = onData;

        public async Task SendAsync(
            string url,
            string jsonBody,
            Dictionary<string, string> headers = null,
            CancellationToken ct = default)
        {
            Uri uri = new Uri(url);
            string host = uri.Host;
            int port = uri.Port == -1 ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
            string path = uri.PathAndQuery;
            bool ssl = uri.Scheme == "https";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody ?? "");

            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(host, port).WithCancellation(ct);
                Stream net = tcp.GetStream();

                if (ssl)
                {
                    var sslStream = new SslStream(net, false, (_, __, ___, ____) => true);
                    await sslStream.AuthenticateAsClientAsync(
                        host, null,
                        System.Security.Authentication.SslProtocols.None, false)
                        .WithCancellation(ct);
                    net = sslStream;
                }

                // ── Write request ──────────────────────────────────────────────
                var hdr = new StringBuilder();
                hdr.Append($"POST {path} HTTP/1.1\r\n");
                hdr.Append($"Host: {host}\r\n");
                if (headers != null)
                    foreach (var kv in headers) hdr.Append($"{kv.Key}: {kv.Value}\r\n");
                hdr.Append("Content-Type: application/json\r\n");
                hdr.Append("Accept: text/event-stream\r\n");
                hdr.Append("Connection: close\r\n");
                hdr.Append($"Content-Length: {bodyBytes.Length}\r\n\r\n");

                byte[] hdrBytes = Encoding.UTF8.GetBytes(hdr.ToString());
                await net.WriteAsync(hdrBytes, 0, hdrBytes.Length, ct);
                if (bodyBytes.Length > 0)
                    await net.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);

                // ── Read response headers ──────────────────────────────────────
                var hdrMs = new MemoryStream();
                byte[] oneByte = new byte[1];
                byte[] termSeq = { 13, 10, 13, 10 }; // \r\n\r\n

                while (true)
                {
                    int r = await net.ReadAsync(oneByte, 0, 1, ct);
                    if (r <= 0) throw new IOException("EOF while reading SSE response header.");
                    hdrMs.WriteByte(oneByte[0]);
                    long len = hdrMs.Length;
                    if (len >= 4)
                    {
                        var b = hdrMs.GetBuffer();
                        if (b[len - 4] == termSeq[0] && b[len - 3] == termSeq[1] &&
                            b[len - 2] == termSeq[2] && b[len - 1] == termSeq[3]) break;
                    }
                    if (hdrMs.Length > 256 * 1024)
                        throw new IOException("SSE response header too large.");
                }

                string hdrText = Encoding.UTF8.GetString(hdrMs.ToArray());
                bool isChunked = hdrText.IndexOf("Transfer-Encoding: chunked",
                                     StringComparison.OrdinalIgnoreCase) >= 0;

                // ── Stream body, fire SSE events ───────────────────────────────
                byte[] readBuf = new byte[ReadBufferSize];

                if (isChunked)
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // Read chunk-size line
                        var sizeSb = new StringBuilder();
                        while (true)
                        {
                            int r = await net.ReadAsync(oneByte, 0, 1, ct);
                            if (r <= 0) goto done;
                            sizeSb.Append((char)oneByte[0]);
                            if (sizeSb.Length >= 2 &&
                                sizeSb[sizeSb.Length - 2] == '\r' &&
                                sizeSb[sizeSb.Length - 1] == '\n') break;
                        }
                        string sizeLine = sizeSb.ToString().Trim();
                        int semi = sizeLine.IndexOf(';');
                        if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                        int chunkSize = 0;
                        try { chunkSize = Convert.ToInt32(sizeLine.Trim(), 16); } catch { }
                        if (chunkSize == 0) break;

                        var chunk = new byte[chunkSize];
                        int got = 0;
                        while (got < chunkSize)
                        {
                            int r = await net.ReadAsync(chunk, got, chunkSize - got, ct);
                            if (r <= 0) goto done;
                            got += r;
                        }
                        await net.ReadAsync(new byte[2], 0, 2, ct); // trailing \r\n
                        FireSseEvents(Encoding.UTF8.GetString(chunk, 0, got));
                    }
                }
                else
                {
                    int r;
                    while ((r = await net.ReadAsync(readBuf, 0, readBuf.Length, ct)) > 0)
                        FireSseEvents(Encoding.UTF8.GetString(readBuf, 0, r));
                }

            done:
                _onData?.Invoke("[DONE]");
            }
        }

        private void FireSseEvents(string text)
        {
            var segments = text.Split(
                new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
                foreach (var line in seg.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("data:")) continue;
                    string payload = t.Substring(5).Trim();
                    _onData?.Invoke(payload == "[DONE]" ? "[DONE]" : payload);
                }
        }
    }

    // ── Task cancellation extension ───────────────────────────────────────────────

    internal static class TaskExtensions
    {
        /// <summary>
        /// Wraps a non-cancellable Task so it respects the given CancellationToken.
        /// The underlying operation still runs to completion — the token only
        /// unblocks the awaiter early.
        /// </summary>
        public static async Task WithCancellation(this Task task, CancellationToken ct)
        {
            if (ct == CancellationToken.None) { await task; return; }
            var tcs = new TaskCompletionSource<bool>();
            using (ct.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task))
                    throw new OperationCanceledException("Request was cancelled.");
                await task; // propagate original exception if any
            }
        }
    }
}