using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using MangaDownloader.Net;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    /// <summary>
    /// Runs the real popup-browser helper (WebView2) with its window hidden, against a local stand-in for a Cloudflare check: a 403 page whose own
    /// script sets the clearance cookie after a moment, as the real page does once a person has passed it. Skipped (returns early) when the helper
    /// has not been built or this is not Windows.
    /// </summary>
    public sealed class BrowserWindowTests : IDisposable
    {
        private readonly string profile = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"), "profile");
        private readonly HttpListener listener = new HttpListener();
        private readonly int port = CloudflareTests.FreePort();
        private readonly List<string> userAgentsWithClearance = new List<string>();

        public BrowserWindowTests()
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            Task.Run(Serve);
        }

        public void Dispose()
        {
            try { listener.Close(); } catch (ObjectDisposedException) { }

            // the browser process may need a moment to release its profile
            for (var i = 0; i < 10; i++)
            {
                try { Directory.Delete(Path.GetDirectoryName(profile), true); return; }
                catch (IOException) { System.Threading.Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(300); }
            }
        }

        private static string FindHelper()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var folder = Path.Combine(dir.FullName, "src", "MangaDownloader.BrowserWindow", "bin");

                if (Directory.Exists(folder))
                    return Directory.GetFiles(folder, BrowserWindowSolver.HelperFileName, SearchOption.AllDirectories).FirstOrDefault();
            }

            return null;
        }

        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;

                try { ctx = await listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException) { return; }

                var cleared = (ctx.Request.Headers["Cookie"] ?? "").Contains("cf_clearance=passed");
                var never = ctx.Request.Url.AbsolutePath.StartsWith("/never");
                string html;

                if (cleared && !never)
                {
                    lock (userAgentsWithClearance)
                        userAgentsWithClearance.Add(ctx.Request.UserAgent);

                    ctx.Response.StatusCode = 200;
                    html = "<html><head><title>Gallery</title></head><body><h1>Real page</h1></body></html>";
                }
                else
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.AddHeader("Server", "cloudflare");
                    ctx.Response.AddHeader("cf-mitigated", "challenge");
                    html = "<html><head><title>Just a moment...</title></head><body>Checking your browser...</body>" +
                           (never ? "" : "<script>setTimeout(function(){document.cookie='cf_clearance=passed; path=/; max-age=3600';location.reload();},1500);</script>") +
                           "</html>";
                }

                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
        }

        private BrowserWindowSolver Solver(string helper, int timeout = 60, List<string> log = null) =>
            new BrowserWindowSolver(profile, timeout, log == null ? (Action<string>)(_ => { }) : log.Add, helper, hidden: true);

        [Fact]
        public void TheWindowClosesItselfOnceThePageIsThroughAndHandsOverCookiesAndUserAgent()
        {
            var helper = FindHelper();
            if (helper == null || !BrowserWindowSolver.IsAvailable(helper))
                return;

            var log = new List<string>();
            var solution = Solver(helper, log: log).Solve(new HttpRequest { Url = $"http://127.0.0.1:{port}/gallery" });

            var clearance = solution.Cookies.Single(c => c.Name == "cf_clearance");
            Assert.Equal("passed", clearance.Value);
            Assert.NotNull(clearance.Expires);
            Assert.Contains("Chrome/", solution.UserAgent); // the embedded Chromium's own user agent, unmodified
            Assert.Contains(log, l => l.Contains("including cf_clearance"));
        }

        [Fact]
        public void ClearsAChallengeForTheWholeHttpStackAndReusesThePopupsUserAgent()
        {
            var helper = FindHelper();
            if (helper == null || !BrowserWindowSolver.IsAvailable(helper))
                return;

            var jar = new CookieJar();

            using (var client = new HttpClientBackend())
            {
                var backend = new CloudflareBackend(new CookieBackend(client, jar), jar, Solver(helper));

                var first = backend.Send(new HttpRequest { Url = $"http://127.0.0.1:{port}/gallery" });
                Assert.Equal(200, first.StatusCode);
                Assert.Contains("Real page", first.Body);

                var second = backend.Send(new HttpRequest { Url = $"http://127.0.0.1:{port}/another" });
                Assert.Equal(200, second.StatusCode);

                // The requests this tool made after the check carry the same user agent the popup browser had.
                lock (userAgentsWithClearance)
                {
                    var toolRequests = userAgentsWithClearance.Where(ua => ua != null).ToList();
                    Assert.Contains(toolRequests, ua => ua.Contains("Chrome/"));
                }
            }
        }

        [Fact]
        public void ARunThatNeverGetsThroughTimesOutAsACancellation()
        {
            var helper = FindHelper();
            if (helper == null || !BrowserWindowSolver.IsAvailable(helper))
                return;

            var e = Assert.Throws<SolverCancelledException>(() => Solver(helper, timeout: 4).Solve(new HttpRequest { Url = $"http://127.0.0.1:{port}/never" }));
            Assert.Contains("timed out", e.Message);
        }

        [Fact]
        public void AMissingHelperIsAPlainErrorSoThePasteFallbackCanTakeOver()
        {
            var missing = Path.Combine(Path.GetTempPath(), "no-such-helper.exe");

            Assert.False(BrowserWindowSolver.IsAvailable(missing));
            var e = Assert.Throws<IOException>(() => new BrowserWindowSolver(profile, 5, null, missing).Solve(new HttpRequest { Url = "https://site.test/" }));
            Assert.IsNotType<SolverCancelledException>(e);
        }

        private sealed class Throwing : IChallengeSolver
        {
            private readonly Exception error;
            public int Calls;

            public Throwing(Exception error)
            {
                this.error = error;
            }

            public SolverSolution Solve(HttpRequest request)
            {
                Calls++;
                throw error;
            }
        }

        [Fact]
        public void PastingIsOnlyAskedWhenThePopupCannotRunNeverAfterYouCancelIt()
        {
            var cannotRun = new Throwing(new IOException("WebView2 not installed"));
            var paste = new Throwing(new IOException("pasted"));
            Assert.Throws<IOException>(() => new FallbackSolver(cannotRun, paste).Solve(new HttpRequest { Url = "https://x.test/" }));
            Assert.Equal(1, paste.Calls); // popup failed to start -> fall back to paste

            var cancelled = new Throwing(new SolverCancelledException("window closed"));
            var paste2 = new Throwing(new IOException("pasted"));
            Assert.Throws<SolverCancelledException>(() => new FallbackSolver(cancelled, paste2).Solve(new HttpRequest { Url = "https://x.test/" }));
            Assert.Equal(0, paste2.Calls); // the person closed the window: do not nag with a second question
        }
    }
}
