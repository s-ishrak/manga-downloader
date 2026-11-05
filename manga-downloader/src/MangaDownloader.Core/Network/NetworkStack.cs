using System;
using System.IO;
using MangaDownloader.Net;

namespace MangaDownloader.Core.Network
{
    public sealed class NetworkOptions
    {
        /// <summary>http(s) proxy, optionally <c>http://user:pass@host:port</c>.</summary>
        public string ProxyUrl { get; set; }

        /// <summary>FlareSolverr base URL (e.g. http://localhost:8191); Cloudflare challenges are only solved when set.</summary>
        public string FlareSolverrUrl { get; set; }

        /// <summary>
        /// On a Cloudflare check, open a separate verification window and collect its cookies and user agent automatically.
        /// The check itself is always completed by a human.
        /// </summary>
        public bool OpenInBrowser { get; set; }

        /// <summary>Explicit opt-in to pasting cookies instead of automatically collecting them from a separate browser.</summary>
        public bool PasteInstead { get; set; }

        /// <summary>Folder for the popup browser's own profile; keeping it means a passed check is remembered.</summary>
        public string BrowserProfileDirectory { get; set; }

        public int BrowserTimeoutSeconds { get; set; } = 300;

        public string UserAgent { get; set; }

        /// <summary>Pick one of several common browser user agents for this run.</summary>
        public bool RandomUserAgent { get; set; }

        /// <summary>cookies.json to load at start and save at exit (HDoujin's format).</summary>
        public string CookieFile { get; set; }
    }

    /// <summary>
    /// The HTTP pipeline every request goes through: Cloudflare solving (outermost) -> cookie jar + redirects -> HttpClient (proxy, Brotli).
    /// Disposing saves the persistent cookies.
    /// </summary>
    public sealed class NetworkStack : IDisposable
    {
        private static readonly string[] UserAgents =
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:125.0) Gecko/20100101 Firefox/125.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        };

        private readonly HttpClientBackend client;
        private readonly IChallengeSolver solver;
        private readonly string cookieFile;

        private NetworkStack(HttpClientBackend client, IChallengeSolver solver, CloudflareBackend http, CookieJar jar, string cookieFile)
        {
            this.client = client;
            this.solver = solver;
            this.cookieFile = cookieFile;
            Http = http;
            Jar = jar;
        }

        public IHttpBackend Http { get; }

        public CookieJar Jar { get; }

        /// <summary>Where "please complete the check" questions go; the console by default, the web UI replaces it.</summary>
        public VerificationBroker Verification { get; private set; }

        public static NetworkStack Create(NetworkOptions options, Action<string> log = null)
        {
            options = options ?? new NetworkOptions();
            log = log ?? (_ => { });

            var jar = new CookieJar();

            if (options.CookieFile != null)
            {
                var imported = jar.Load(options.CookieFile);
                if (imported > 0)
                    log($"loaded {imported} cookie(s) from {options.CookieFile}");
            }

            var clientOptions = new HttpClientOptions
            {
                UserAgent = options.UserAgent
                    ?? (options.RandomUserAgent ? UserAgents[new Random().Next(UserAgents.Length)] : HttpClientBackend.DefaultUserAgent),
                Proxy = string.IsNullOrWhiteSpace(options.ProxyUrl) ? null : ProxySettings.Parse(options.ProxyUrl),
            };

            var client = new HttpClientBackend(clientOptions);
            IChallengeSolver flare = string.IsNullOrWhiteSpace(options.FlareSolverrUrl) ? null : new FlareSolverrClient(options.FlareSolverrUrl);
            var verification = new VerificationBroker
            {
                Target = new ConsoleVerificationPrompt(Console.In, Console.Error, interactive: !Console.IsInputRedirected),
            };
            IChallengeSolver manual = null;

            if (options.OpenInBrowser)
            {
                var profile = options.BrowserProfileDirectory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "manga-downloader", "verification-profile");
                // Pasting is available only when explicitly requested. The normal path never opens the person's main browser session.
                manual = options.PasteInstead
                    ? (IChallengeSolver)new ManualChallengeSolver(verification, null, log)
                    : new FallbackSolver(
                        new ChromiumWindowSolver(profile + "-chromium", options.BrowserTimeoutSeconds, log),
                        BrowserWindowSolver.IsAvailable() ? new BrowserWindowSolver(profile, options.BrowserTimeoutSeconds, log) : null) { Log = log };
            }

            // FlareSolverr (automatic) first when both are given; the manual check is the fallback.
            IChallengeSolver solver = flare != null && manual != null ? new FallbackSolver(flare, manual) { Log = log } : flare ?? manual;
            var http = new CloudflareBackend(new CookieBackend(client, jar), jar, solver) { Log = log };

            var stack = new NetworkStack(client, solver, http, jar, options.CookieFile) { Verification = verification };

            // A passed Cloudflare check is worth keeping even if the program is later closed abruptly (closing the console window skips Dispose).
            http.Solved += () =>
            {
                try
                {
                    stack.SaveCookies();
                }
                catch (IOException)
                {
                    // not being able to save must not fail the download
                }
            };

            return stack;
        }

        public void SaveCookies()
        {
            if (cookieFile != null)
                Jar.Save(cookieFile);
        }

        public void Dispose()
        {
            try
            {
                SaveCookies();
            }
            finally
            {
                try { (Http as IDisposable)?.Dispose(); }
                finally
                {
                    client.Dispose();
                    (solver as IDisposable)?.Dispose();
                }
            }
        }
    }
}
