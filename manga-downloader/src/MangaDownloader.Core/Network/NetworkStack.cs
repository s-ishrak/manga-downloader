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
        /// On a Cloudflare check, open the site in the person's own browser and ask them to paste what that browser sent after they passed it.
        /// The check itself is always done by a human; nothing here drives or inspects the browser.
        /// </summary>
        public bool OpenInBrowser { get; set; }

        /// <summary>With <see cref="OpenInBrowser"/>: skip the popup window and ask for pasted cookies instead (also the fallback when the popup cannot run).</summary>
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
                var paste = new ManualChallengeSolver(verification, null, log);
                var popupReady = !options.PasteInstead && BrowserWindowSolver.IsAvailable() && !string.IsNullOrEmpty(options.BrowserProfileDirectory);

                if (!options.PasteInstead && !popupReady)
                    log("the popup browser is not available here (it needs Windows and " + BrowserWindowSolver.HelperFileName + " next to the program); you will be asked to paste instead.");

                // Popup window first. Pasting is only the fallback when the popup cannot run, never after you close or cancel the window.
                manual = popupReady
                    ? new FallbackSolver(new BrowserWindowSolver(options.BrowserProfileDirectory, options.BrowserTimeoutSeconds, log), paste) { Log = log }
                    : (IChallengeSolver)paste;
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
                client.Dispose();
                (solver as IDisposable)?.Dispose();
            }
        }
    }
}
