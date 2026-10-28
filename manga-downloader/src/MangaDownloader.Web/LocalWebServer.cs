using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MangaDownloader.Core.Library;

namespace MangaDownloader.Web
{
    /// <summary>
    /// The local control panel: a JSON API plus one HTML page, served on 127.0.0.1 only.
    /// A server that can start downloads and read your queue must not be driveable by whatever web page you happen to have open, so every
    /// request needs the per-run token (cookie or X-Token header), a loopback Host header (DNS-rebinding defence), and POSTs must be
    /// same-origin JSON.
    /// </summary>
    public sealed class LocalWebServer : IDisposable, MangaDownloader.Net.IVerificationPrompt
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        private const string CookieName = "md_token";

        private readonly LibraryDb library;
        private readonly QueueRunner runner;
        private readonly SeriesTracker tracker;
        private readonly HttpListener listener = new HttpListener();
        private readonly ConcurrentDictionary<long, bool> checking = new ConcurrentDictionary<long, bool>();
        private CancellationTokenSource stop;
        private readonly object verificationGate = new object();
        private PendingVerification pending;
        private Task acceptLoop;

        public LocalWebServer(LibraryDb library, QueueRunner runner, SeriesTracker tracker, int port, string token = null)
        {
            this.library = library;
            this.runner = runner;
            this.tracker = tracker;
            Port = port;
            Token = token ?? NewToken();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public int Port { get; }

        public string Token { get; }

        /// <summary>The address to open: it carries the token once, then a cookie takes over.</summary>
        public string Url => $"http://127.0.0.1:{Port}/?token={Token}";

        public static string NewToken()
        {
            var bytes = new byte[24];

            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        public void Start()
        {
            listener.Start();
            stop = new CancellationTokenSource();
            acceptLoop = Task.Run(() => Accept(stop.Token));
        }

        public void Dispose()
        {
            stop?.Cancel();

            try
            {
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                acceptLoop?.Wait(2000);
            }
            catch (AggregateException)
            {
            }
        }

        private async Task Accept(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(() => Handle(context));
            }
        }

        // ---- request handling ----------------------------------------------------------------------------------

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var path = req.Url.AbsolutePath;

                if (!HostAllowed(req.Headers["Host"]))
                {
                    Send(ctx, 403, new { error = "bad host" });
                    return;
                }

                // First visit: the token arrives in the query string, becomes a cookie, and disappears from the address bar.
                if (req.HttpMethod == "GET" && path == "/" && req.QueryString["token"] != null)
                {
                    if (TokenMatches(req.QueryString["token"]))
                    {
                        ctx.Response.AddHeader("Set-Cookie", $"{CookieName}={Token}; Path=/; HttpOnly; SameSite=Strict");
                        ctx.Response.StatusCode = 302;
                        ctx.Response.RedirectLocation = "/";
                        ctx.Response.Close();
                    }
                    else
                    {
                        Send(ctx, 403, new { error = "bad token" });
                    }

                    return;
                }

                if (!Authorized(req))
                {
                    Send(ctx, 401, new { error = "missing or wrong token; open the address printed by `serve`" });
                    return;
                }

                if (req.HttpMethod == "GET" && path == "/")
                {
                    SendHtml(ctx);
                    return;
                }

                if (req.HttpMethod == "GET" && path == "/api/state")
                {
                    Send(ctx, 200, State());
                    return;
                }

                if (req.HttpMethod == "POST")
                {
                    if (!SameOriginJson(req))
                    {
                        Send(ctx, 403, new { error = "POST must be same-origin application/json" });
                        return;
                    }

                    Send(ctx, 200, Post(path, ReadBody(req)));
                    return;
                }

                Send(ctx, 404, new { error = "not found" });
            }
            catch (ApiException e)
            {
                Send(ctx, e.Status, new { error = e.Message });
            }
            catch (Exception e)
            {
                try
                {
                    Send(ctx, 500, new { error = e.Message });
                }
                catch (Exception)
                {
                    // client went away
                }
            }
        }

        private bool HostAllowed(string host) =>
            host != null && (host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase));

        private bool TokenMatches(string candidate)
        {
            if (candidate == null)
                return false;

            // Constant-time comparison.
            var a = Encoding.UTF8.GetBytes(candidate);
            var b = Encoding.UTF8.GetBytes(Token);
            var diff = a.Length ^ b.Length;

            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                diff |= a[i] ^ b[i];

            return diff == 0;
        }

        private bool Authorized(HttpListenerRequest req) =>
            TokenMatches(req.Cookies[CookieName]?.Value) || TokenMatches(req.Headers["X-Token"]);

        private bool SameOriginJson(HttpListenerRequest req)
        {
            var origin = req.Headers["Origin"];

            if (origin != null && !origin.Equals($"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) && !origin.Equals($"http://localhost:{Port}", StringComparison.OrdinalIgnoreCase))
                return false;

            return (req.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadBody(HttpListenerRequest req)
        {
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                return reader.ReadToEnd();
        }

        // ---- API -----------------------------------------------------------------------------------------------

        private object State() => new
        {
            runner = new { running = runner.IsRunning, paused = runner.IsPaused, currentId = runner.CurrentId },
            verification = Pending(),
            verificationSteps = MangaDownloader.Net.VerificationText.Steps,
            queue = library.ListQueue(),
            history = library.ListHistory(100),
            tracked = library.ListTracked().Select(t => new
            {
                t.Id,
                t.Url,
                t.Title,
                chapters = t.KnownChapters.Count,
                t.NewCount,
                t.LastChecked,
                t.LastError,
                t.AutoDownload,
                checking = checking.ContainsKey(t.Id),
            }),
        };

        private object Post(string path, string body)
        {
            var args = string.IsNullOrWhiteSpace(body) ? JsonDocument.Parse("{}").RootElement : JsonDocument.Parse(body).RootElement;

            string Str(string name) => args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            bool Bool(string name) => args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

            var parts = path.Trim('/').Split('/');

            if (parts.Length == 2 && parts[0] == "api" && parts[1] == "queue")
            {
                var urls = (Str("urls") ?? "").Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var bad = urls.Where(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")).ToList();

                if (urls.Length == 0)
                    throw new ApiException(400, "Paste at least one http(s) URL.");

                if (bad.Count > 0)
                    throw new ApiException(400, "Not an http(s) URL: " + bad[0]);

                foreach (var url in urls)
                    library.Enqueue(url, source: "web");

                runner.Wake();
                return new { added = urls.Length };
            }

            if (parts.Length == 3 && parts[1] == "queue" && parts[2] == "clear-completed")
                return new { removed = library.ClearCompleted() };

            if (parts.Length == 4 && parts[1] == "queue" && long.TryParse(parts[2], out var qid))
            {
                switch (parts[3])
                {
                    case "cancel":
                        return new { ok = runner.Cancel(qid) };
                    case "retry":
                        var item = library.Get(qid) ?? throw new ApiException(404, "No such queue item.");
                        library.Enqueue(item.Url);
                        runner.Wake();
                        return new { ok = true };
                    case "remove":
                        if (runner.CurrentId == qid)
                            throw new ApiException(409, "Stop the download before removing it.");
                        return new { ok = library.RemoveFromQueue(qid) };
                }
            }

            if (parts.Length == 3 && parts[1] == "runner")
            {
                if (parts[2] == "pause") runner.Pause();
                else if (parts[2] == "resume") runner.Resume();
                else throw new ApiException(404, "unknown runner action");

                return new { paused = runner.IsPaused };
            }

            if (parts.Length >= 2 && parts[1] == "verify")
            {
                var done = parts.Length == 3 && parts[2] == "cancel" ? Answer(null) : Answer(Str("text") ?? throw new ApiException(400, "Paste what the browser sent."));
                return new { ok = done };
            }

            if (parts.Length == 3 && parts[1] == "history" && parts[2] == "clear")
                return new { removed = library.ClearHistory() };

            if (parts.Length == 2 && parts[1] == "track")
            {
                var url = Str("url") ?? throw new ApiException(400, "Missing url.");

                try
                {
                    return tracker.Track(url, Bool("autoDownload"));
                }
                catch (InvalidOperationException e)
                {
                    throw new ApiException(422, e.Message);
                }
            }

            if (parts.Length == 3 && parts[1] == "track" && parts[2] == "check-all")
            {
                Task.Run(() => CheckAll());
                return new { started = true };
            }

            if (parts.Length == 4 && parts[1] == "track" && long.TryParse(parts[2], out var tid))
            {
                var series = library.GetTracked(tid) ?? throw new ApiException(404, "No such tracked series.");

                switch (parts[3])
                {
                    case "check":
                        Task.Run(() => CheckOne(series));
                        return new { started = true };
                    case "remove":
                        return new { ok = library.RemoveTracked(tid) };
                    case "auto":
                        library.SetAutoDownload(tid, Bool("enabled"));
                        return new { ok = true };
                }
            }

            if (parts.Length == 2 && parts[1] == "import")
            {
                var file = Str("path") ?? throw new ApiException(400, "Missing path.");

                if (!File.Exists(file))
                    throw new ApiException(404, "File not found on this computer: " + file);

                try
                {
                    var result = QueueMangaImporter.Import(library, file);
                    runner.Wake();
                    return new { result.Added, result.AlreadyPresent, skipped = result.Skipped, result.Version };
                }
                catch (FormatException e)
                {
                    throw new ApiException(422, e.Message);
                }
            }

            throw new ApiException(404, "not found");
        }

        // ---- manual Cloudflare check -------------------------------------------------------------------------------

        private sealed class PendingVerification
        {
            public string Url;
            public string Host;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Text;
            public bool Cancelled;
        }

        private object Pending()
        {
            lock (verificationGate)
                return pending == null ? null : new { host = pending.Host, url = pending.Url };
        }

        private bool Answer(string text)
        {
            lock (verificationGate)
            {
                if (pending == null)
                    return false;

                pending.Text = text;
                pending.Cancelled = text == null;
                pending.Done.Set();
                return true;
            }
        }

        /// <summary>Shows the Cloudflare question in the web page and waits for the person to paste the answer or cancel (15 minutes at most).</summary>
        public string Ask(string url, string host)
        {
            var request = new PendingVerification { Url = url, Host = host };

            lock (verificationGate)
                pending = request;

            try
            {
                if (!request.Done.Wait(TimeSpan.FromMinutes(15)))
                    return null;

                return request.Cancelled ? null : request.Text;
            }
            finally
            {
                lock (verificationGate)
                    pending = null;
            }
        }

        public void CheckAll()
        {
            foreach (var series in library.ListTracked())
                CheckOne(series);
        }

        private void CheckOne(TrackedSeries series)
        {
            if (!checking.TryAdd(series.Id, true))
                return;

            try
            {
                var result = tracker.Check(series);

                if (result.Queued)
                    runner.Wake();
            }
            finally
            {
                checking.TryRemove(series.Id, out _);
            }
        }

        // ---- responses -----------------------------------------------------------------------------------------

        private static void Send(HttpListenerContext ctx, int status, object payload)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.AddHeader("Cache-Control", "no-store");
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private static void SendHtml(HttpListenerContext ctx)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html"))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.AddHeader("Cache-Control", "no-store");
                ctx.Response.AddHeader("X-Content-Type-Options", "nosniff");
                ctx.Response.AddHeader("Content-Security-Policy", "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'");
                ctx.Response.ContentLength64 = ms.Length;
                ctx.Response.OutputStream.Write(ms.GetBuffer(), 0, (int)ms.Length);
                ctx.Response.Close();
            }
        }

        private sealed class ApiException : Exception
        {
            public ApiException(int status, string message) : base(message)
            {
                Status = status;
            }

            public int Status { get; }
        }
    }
}
