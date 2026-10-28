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
using MangaDownloader.Core;
using MangaDownloader.Core.Download;
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
        private readonly DownloadOptions download;
        private readonly HttpListener listener = new HttpListener();
        private readonly ConcurrentDictionary<long, bool> checking = new ConcurrentDictionary<long, bool>();
        private CancellationTokenSource stop;
        private readonly object verificationGate = new object();
        private PendingVerification pending;
        private Task acceptLoop;
        private readonly string settingsFile;
        private readonly object[] sites;
        private readonly object settingsGate = new object();
        private int checkEveryMinutes;

        /// <param name="settingsFile">Where changes made in the page are saved (only the fields changed there); null keeps them for this run only.</param>
        /// <param name="modules">The loaded scrapers; when given, the page lists the sites they support.</param>
        public LocalWebServer(LibraryDb library, QueueRunner runner, SeriesTracker tracker, int port, string token = null, DownloadOptions download = null, string settingsFile = null, ModuleRepository modules = null)
        {
            this.download = download;
            sites = modules == null ? new object[0] : modules.Modules
                .SelectMany(m => m.Module.Domains.All.Select(d => new { domain = d, name = m.Module.GetName(d), adult = m.Module.Adult }))
                .GroupBy(s => s.domain, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(s => s.domain, StringComparer.OrdinalIgnoreCase).ToArray<object>();
            this.settingsFile = settingsFile;
            this.library = library;
            this.runner = runner;
            this.tracker = tracker;
            Port = port;
            Token = token ?? NewToken();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public int Port { get; }

        public string Token { get; }

        /// <summary>Minutes between automatic checks of all tracked series (0 = off). The host's timer reads it; the page can change it.</summary>
        public int CheckEveryMinutes
        {
            get => Volatile.Read(ref checkEveryMinutes);
            set => Volatile.Write(ref checkEveryMinutes, Math.Max(0, value));
        }

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

                if (req.HttpMethod == "GET" && path == "/api/sites")
                {
                    Send(ctx, 200, sites);
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
            runner = new { running = runner.IsRunning, paused = runner.IsPaused, active = runner.ActiveCount, concurrency = runner.MaxConcurrent, maxConcurrency = QueueRunner.ConcurrencyLimit },
            outputDirectory = download == null ? null : Path.GetFullPath(download.OutputDirectory),
            settings = download == null ? null : new
            {
                format = download.SaveFormat.ToString().ToLowerInvariant(),
                parallel = download.MaxParallel,
                retry = download.RetryFailed,
                delayMilliseconds = download.PageDelayMilliseconds,
                keepImages = download.KeepImages,
                combineChapters = download.CombineChapters,
                overwrite = !download.SkipExisting,
                checkEveryMinutes = CheckEveryMinutes,
                saved = settingsFile != null,
                limits = new { parallel = AppSettings.MaxParallel, retry = AppSettings.MaxRetry, delayMilliseconds = AppSettings.MaxDelayMilliseconds },
            },
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
                        if (runner.IsActive(qid))
                            throw new ApiException(409, "Stop the download before removing it.");
                        return new { ok = library.RemoveFromQueue(qid) };
                }
            }

            if (parts.Length == 3 && parts[1] == "runner")
            {
                if (parts[2] == "pause") runner.Pause();
                else if (parts[2] == "resume") runner.Resume();
                else if (parts[2] == "concurrency")
                {
                    if (!args.TryGetProperty("count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var n) || n < 1 || n > QueueRunner.ConcurrencyLimit)
                        throw new ApiException(400, $"count must be a whole number from 1 to {QueueRunner.ConcurrencyLimit}.");

                    runner.MaxConcurrent = n;
                    Persist(s => s.Concurrent = n);
                }
                else throw new ApiException(404, "unknown runner action");

                return new { paused = runner.IsPaused, concurrency = runner.MaxConcurrent };
            }

            if (parts.Length == 3 && parts[1] == "settings" && parts[2] == "output")
            {
                if (download == null)
                    throw new ApiException(404, "The output folder cannot be changed here.");

                var folder = UsableFolder(Str("path"));
                download.OutputDirectory = folder; // downloads that start from now on use it
                Persist(s => s.OutputDirectory = folder);
                return new { outputDirectory = folder };
            }

            if (parts.Length == 2 && parts[1] == "settings")
            {
                if (download == null)
                    throw new ApiException(404, "Settings cannot be changed here.");

                // Every field is optional; all are checked before any is applied, so a bad value changes nothing.
                int? Int(string name, int min, int max)
                {
                    if (!args.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
                        return null;

                    if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var n) || n < min || n > max)
                        throw new ApiException(400, $"{name} must be a whole number from {min} to {max}.");

                    return n;
                }

                bool? Flag(string name)
                {
                    if (!args.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
                        return null;

                    if (p.ValueKind != JsonValueKind.True && p.ValueKind != JsonValueKind.False)
                        throw new ApiException(400, name + " must be true or false.");

                    return p.GetBoolean();
                }

                var folder = Str("outputDirectory") == null ? null : UsableFolder(Str("outputDirectory"));
                var formatText = Str("format");
                SaveFormat? format;

                try
                {
                    format = formatText == null ? (SaveFormat?)null : AppSettings.ParseFormat(formatText);
                }
                catch (FormatException e)
                {
                    throw new ApiException(400, e.Message);
                }

                var parallel = Int("parallel", 1, AppSettings.MaxParallel);
                var retry = Int("retry", 0, AppSettings.MaxRetry);
                var delay = Int("delayMilliseconds", 0, AppSettings.MaxDelayMilliseconds);
                var concurrency = Int("concurrency", 1, QueueRunner.ConcurrencyLimit);
                var checkEvery = Int("checkEveryMinutes", 0, 7 * 24 * 60);
                var keepImages = Flag("keepImages");
                var overwrite = Flag("overwrite");
                var combine = Flag("combineChapters");

                if (combine != null) download.CombineChapters = combine.Value;
                if (folder != null) download.OutputDirectory = folder;
                if (format != null) download.SaveFormat = format.Value;
                if (parallel != null) download.MaxParallel = parallel.Value;
                if (retry != null) download.RetryFailed = retry.Value;
                if (delay != null) download.PageDelayMilliseconds = delay.Value;
                if (keepImages != null) download.KeepImages = keepImages.Value;
                if (overwrite != null) download.SkipExisting = !overwrite.Value;
                if (concurrency != null) runner.MaxConcurrent = concurrency.Value;
                if (checkEvery != null) CheckEveryMinutes = checkEvery.Value;

                Persist(s =>
                {
                    s.OutputDirectory = folder ?? s.OutputDirectory;
                    s.Format = format == null ? s.Format : formatText.Trim().ToLowerInvariant();
                    s.Parallel = parallel ?? s.Parallel;
                    s.Retry = retry ?? s.Retry;
                    s.DelayMilliseconds = delay ?? s.DelayMilliseconds;
                    s.KeepImages = keepImages ?? s.KeepImages;
                    s.Overwrite = overwrite ?? s.Overwrite;
                    s.CombineChapters = combine ?? s.CombineChapters;
                    s.Concurrent = concurrency ?? s.Concurrent;
                    s.CheckEveryMinutes = checkEvery ?? s.CheckEveryMinutes;
                });

                return new { saved = settingsFile != null };
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
                // Either a path on this computer (typed in) or the file's contents (dropped or picked in the page).
                var file = Str("path");
                var text = Str("text");

                if (text == null && string.IsNullOrWhiteSpace(file))
                    throw new ApiException(400, "Missing path.");

                if (text == null && !File.Exists(file))
                    throw new ApiException(404, "File not found on this computer: " + file);

                try
                {
                    var result = text != null ? QueueMangaImporter.ImportText(library, text) : QueueMangaImporter.Import(library, file);
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

        /// <summary>The full path of a folder downloads can be written to, created if needed.</summary>
        private static string UsableFolder(string text)
        {
            var folder = (text ?? "").Trim();

            if (folder.Length == 0)
                throw new ApiException(400, "Enter a folder.");

            try
            {
                folder = Path.GetFullPath(folder);
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                throw new ApiException(422, "Cannot use that folder: " + e.Message);
            }
        }

        /// <summary>Saves the fields changed in the page to settings.json, keeping whatever else is in it.</summary>
        private void Persist(Action<AppSettings> change)
        {
            if (settingsFile == null)
                return;

            lock (settingsGate)
            {
                var settings = AppSettings.Load(settingsFile);
                change(settings);
                settings.Save(settingsFile);
            }
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
