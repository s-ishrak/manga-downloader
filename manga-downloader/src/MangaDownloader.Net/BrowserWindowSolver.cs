using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MangaDownloader.Net
{
    /// <summary>The person gave up, closed the window, or the time ran out. Never retried with another solver: asking again would just nag them.</summary>
    public class SolverCancelledException : IOException
    {
        public SolverCancelledException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The popup browser: opens a real Chromium (WebView2) window on the site, lets the person pass the Cloudflare check by hand, and takes over
    /// the cookies and user agent the browser ended up with. The window belongs to a separate helper program
    /// (<c>manga-downloader-browser.exe</c>) that never touches the page; this class only starts it and reads its result file.
    /// </summary>
    public sealed class BrowserWindowSolver : IChallengeSolver
    {
        public const string HelperFileName = "manga-downloader-browser.exe";

        private readonly string helperPath;
        private readonly string profileDirectory;
        private readonly int timeoutSeconds;
        private readonly bool hidden;
        private readonly Action<string> log;

        public BrowserWindowSolver(string profileDirectory, int timeoutSeconds = 300, Action<string> log = null, string helperPath = null, bool hidden = false)
        {
            this.profileDirectory = profileDirectory;
            this.timeoutSeconds = timeoutSeconds;
            this.helperPath = helperPath ?? DefaultHelperPath;
            this.hidden = hidden;
            this.log = log ?? (_ => { });
        }

        public static string DefaultHelperPath => Path.Combine(AppContext.BaseDirectory, HelperFileName);

        /// <summary>The popup needs Windows (WebView2) and the helper next to this program.</summary>
        public static bool IsAvailable(string helperPath = null) =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(helperPath ?? DefaultHelperPath);

        public SolverSolution Solve(HttpRequest request)
        {
            if (!File.Exists(helperPath))
                throw new IOException("The popup browser helper was not found: " + helperPath);

            // A POST cannot be replayed in a browser; the check is per site, so open the site itself.
            var target = request.Method == "POST" ? new Uri(request.Url).GetLeftPart(UriPartial.Authority) + "/" : request.Url;
            var host = new Uri(target).Host;
            var output = Path.Combine(Path.GetTempPath(), "manga-downloader-" + Guid.NewGuid().ToString("N") + ".json");

            var info = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var a in new[] { "--url", target, "--profile", profileDirectory, "--out", output, "--timeout", timeoutSeconds.ToString() })
                info.ArgumentList.Add(a);

            if (hidden)
                info.ArgumentList.Add("--hidden");

            if (!hidden)
                log("A browser window opened for " + host + ". Complete the check in it yourself; it closes by itself when you are through.");

            try
            {
                using (var process = Process.Start(info))
                {
                    var stderr = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit((timeoutSeconds + 30) * 1000))
                    {
                        process.Kill(true);
                        throw new SolverCancelledException("The Cloudflare check for " + host + " timed out.");
                    }

                    switch (process.ExitCode)
                    {
                        case 0:
                            return Read(output, target, log);
                        case 2:
                            throw new SolverCancelledException("The check window for " + host + " was closed before the check was passed.");
                        case 3:
                            throw new SolverCancelledException("The Cloudflare check for " + host + " timed out after " + timeoutSeconds + " s.");
                        default:
                            throw new IOException("The popup browser could not start (exit " + process.ExitCode + "): " + stderr.GetAwaiter().GetResult().Trim());
                    }
                }
            }
            finally
            {
                try { File.Delete(output); } catch (IOException) { }
            }
        }

        private static SolverSolution Read(string path, string target, Action<string> log)
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var root = doc.RootElement;
                var solution = new SolverSolution
                {
                    Status = 200,
                    Url = root.TryGetProperty("url", out var u) ? u.GetString() : target,
                    UserAgent = root.TryGetProperty("userAgent", out var ua) ? ua.GetString() : null,
                };

                foreach (var c in root.GetProperty("cookies").EnumerateArray())
                {
                    var entry = new CookieEntry
                    {
                        Name = c.GetProperty("name").GetString(),
                        Value = c.GetProperty("value").GetString(),
                        Domain = c.GetProperty("domain").GetString(),
                        Path = c.TryGetProperty("path", out var p) ? p.GetString() : "/",
                    };

                    if (c.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number && ex.GetInt64() > 0)
                        entry.Expires = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ex.GetInt64());

                    solution.Cookies.Add(entry);
                }

                log("took over " + solution.Cookies.Count + " cookie(s)" + (solution.Cookies.Any(c => c.Name == "cf_clearance") ? " (including cf_clearance)." : "."));
                return solution;
            }
        }
    }
}
