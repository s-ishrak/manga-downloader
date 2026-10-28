using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MangaDownloader.Net
{
    /// <summary>Asks the person to hand over what their browser sent after they passed the check. Returns the pasted text, or null if they cancel.</summary>
    public interface IVerificationPrompt
    {
        string Ask(string url, string host);
    }

    /// <summary>Lets the application decide where the question is shown (the console, or the web UI) after the network stack is built.</summary>
    public sealed class VerificationBroker : IVerificationPrompt
    {
        public IVerificationPrompt Target { get; set; }

        public string Ask(string url, string host) =>
            (Target ?? throw new IOException("A Cloudflare check needs a person, but nothing is available to ask.")).Ask(url, host);
    }

    /// <summary>The steps shown to the person, in the console and the web UI.</summary>
    public static class VerificationText
    {
        public static readonly string[] Steps =
        {
            "The page just opened in your normal browser. Complete the Cloudflare check there yourself.",
            "When the real page has loaded: press F12, open the Network tab, reload the page (F5), and click the first entry (the page itself).",
            "Right-click it, choose Copy, then \"Copy as cURL\" (either the bash or the cmd flavour works). That text holds your cookies and browser id.",
            "Paste it here. Nothing in this tool controls or reads your browser; you hand the text over.",
        };
    }

    /// <summary>The text prompt used on the command line. Empty first line cancels; otherwise paste, then finish with an empty line.</summary>
    public sealed class ConsoleVerificationPrompt : IVerificationPrompt
    {
        private readonly TextReader input;
        private readonly TextWriter output;
        private readonly bool interactive;

        public ConsoleVerificationPrompt(TextReader input, TextWriter output, bool interactive = true)
        {
            this.input = input;
            this.output = output;
            this.interactive = interactive;
        }

        public string Ask(string url, string host)
        {
            if (!interactive)
                throw new IOException("Cloudflare wants a manual check for " + host + " but there is no terminal to ask in. Run it from a terminal, use the web UI, or import a cf_clearance cookie.");

            output.WriteLine();
            output.WriteLine("Cloudflare is checking the connection to " + host + ".");

            for (var i = 0; i < VerificationText.Steps.Length; i++)
                output.WriteLine("  " + (i + 1) + ". " + VerificationText.Steps[i]);

            output.WriteLine("If the page did not open, go to: " + url);
            output.WriteLine("Paste below, then press Enter on an empty line (an empty paste cancels):");

            var text = new StringBuilder();

            for (string line; (line = input.ReadLine()) != null;)
            {
                if (line.Trim().Length == 0)
                {
                    if (text.Length == 0)
                        return null;

                    break;
                }

                text.AppendLine(line);
            }

            return text.Length == 0 ? null : text.ToString();
        }
    }

    public sealed class ClearanceResult
    {
        public List<CookieEntry> Cookies { get; } = new List<CookieEntry>();
        public string UserAgent { get; set; }
    }

    /// <summary>
    /// Pulls the cookies and the user agent out of whatever the person pasted: browser DevTools "Copy as cURL" (bash, cmd or PowerShell
    /// flavour), a <c>Cookie:</c> header, a <c>name=value; name2=value2</c> string, or a bare cf_clearance value.
    /// </summary>
    public static class ClearanceParser
    {
        /// <summary>How long a pasted cf_clearance is kept (Cloudflare decides the real lifetime; if it ran out the check is simply asked again).</summary>
        public static readonly TimeSpan ClearanceLifetime = TimeSpan.FromHours(12);

        public static ClearanceResult Parse(string text, string host)
        {
            var result = new ClearanceResult();
            text = (text ?? "").Trim();

            if (text.Length == 0)
                return result;

            string cookieHeader = null;

            if (Regex.IsMatch(text, @"^\s*(curl|Invoke-WebRequest|Invoke-RestMethod)\b", RegexOptions.IgnoreCase))
            {
                ParseCommand(text, out cookieHeader, out var ua);
                result.UserAgent = ua;
            }
            else
            {
                var cookieLines = new List<string>();

                foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim();
                    var header = Regex.Match(line, @"^(?<n>cookie|user-agent)\s*:\s*(?<v>.+)$", RegexOptions.IgnoreCase);

                    if (header.Success && header.Groups["n"].Value.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
                        result.UserAgent = header.Groups["v"].Value.Trim();
                    else if (header.Success)
                        cookieLines.Add(header.Groups["v"].Value.Trim());
                    else if (line.StartsWith("Mozilla/", StringComparison.Ordinal))
                        result.UserAgent = line;
                    else if (line.Contains("="))
                        cookieLines.Add(line);
                    else if (Regex.IsMatch(line, @"^[A-Za-z0-9._\-]{20,}$"))
                        cookieLines.Add("cf_clearance=" + line); // a bare token: the clearance value
                }

                cookieHeader = string.Join("; ", cookieLines);
            }

            foreach (var pair in (cookieHeader ?? "").Split(';'))
            {
                var eq = pair.IndexOf('=');

                if (eq <= 0)
                    continue;

                var name = pair.Substring(0, eq).Trim();
                var value = pair.Substring(eq + 1).Trim();

                if (name.Length == 0 || result.Cookies.Any(c => c.Name == name))
                    continue;

                result.Cookies.Add(new CookieEntry
                {
                    Name = name,
                    Value = value,
                    Domain = host,
                    Path = "/",
                    // Only the clearance is worth keeping between runs; the rest behave like session cookies.
                    Expires = name == "cf_clearance" ? DateTime.UtcNow + ClearanceLifetime : (DateTime?)null,
                });
            }

            return result;
        }

        private static void ParseCommand(string text, out string cookies, out string userAgent)
        {
            // cmd flavour escapes with ^ and continues lines with ^<newline>; bash continues with \<newline>.
            text = Regex.Replace(text, @"\^\r?\n", " ");
            text = Regex.Replace(text, @"\\\r?\n", " ");

            if (text.Contains("^\""))
                text = Regex.Replace(text, @"\^(.)", "$1");

            cookies = null;
            userAgent = null;

            foreach (Match m in Regex.Matches(text, @"(?:^|\s)(?:-H|--header)\s+(?<q>['""])(?<n>[^:'""]+):\s*(?<v>.*?)\k<q>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var name = m.Groups["n"].Value.Trim();

                if (name.Equals("cookie", StringComparison.OrdinalIgnoreCase))
                    cookies = Join(cookies, m.Groups["v"].Value);
                else if (name.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
                    userAgent = m.Groups["v"].Value.Trim();
            }

            foreach (Match m in Regex.Matches(text, @"(?:^|\s)(?:-b|--cookie)\s+(?<q>['""])(?<v>.*?)\k<q>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                cookies = Join(cookies, m.Groups["v"].Value);

            // PowerShell flavour: -Headers @{ "cookie"="..."; "user-agent"="..." }
            foreach (Match m in Regex.Matches(text, @"""(?<n>cookie|user-agent)""\s*=\s*""(?<v>[^""]*)""", RegexOptions.IgnoreCase))
            {
                if (m.Groups["n"].Value.Equals("cookie", StringComparison.OrdinalIgnoreCase))
                    cookies = Join(cookies, m.Groups["v"].Value);
                else
                    userAgent = m.Groups["v"].Value.Trim();
            }

            var ps = Regex.Match(text, @"-UserAgent\s+""(?<v>[^""]*)""", RegexOptions.IgnoreCase);

            if (userAgent == null && ps.Success)
                userAgent = ps.Groups["v"].Value;
        }

        private static string Join(string existing, string more) => string.IsNullOrEmpty(existing) ? more : existing + "; " + more;
    }

    /// <summary>
    /// The manual way through a Cloudflare check. It opens the page in the person's own default browser (nothing is controlled or inspected
    /// there), then waits for them to paste what the browser sent once they passed the check, and uses those cookies and that user agent.
    /// </summary>
    public sealed class ManualChallengeSolver : IChallengeSolver
    {
        private readonly IVerificationPrompt prompt;
        private readonly Action<string> openInBrowser;
        private readonly Action<string> log;

        public ManualChallengeSolver(IVerificationPrompt prompt, Action<string> openInBrowser = null, Action<string> log = null)
        {
            this.prompt = prompt;
            this.openInBrowser = openInBrowser ?? DefaultOpen;
            this.log = log ?? (_ => { });
        }

        public SolverSolution Solve(HttpRequest request)
        {
            // A POST cannot be replayed by hand; the check is per site, so open the site itself.
            var target = request.Method == "POST" ? new Uri(request.Url).GetLeftPart(UriPartial.Authority) + "/" : request.Url;
            var host = new Uri(target).Host;

            try
            {
                openInBrowser(target);
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
            {
                log("could not open your browser (" + e.Message + "); open " + target + " yourself.");
            }

            var answer = prompt.Ask(target, host) ?? throw new SolverCancelledException("The Cloudflare check for " + host + " was cancelled.");
            var parsed = ClearanceParser.Parse(answer, host);

            if (parsed.Cookies.Count == 0)
                throw new IOException("I could not find any cookies in what you pasted. Use \"Copy as cURL\" on the page request in the browser's Network tab, or paste the cf_clearance value.");

            if (string.IsNullOrEmpty(parsed.UserAgent))
                log("no user agent in what you pasted; Cloudflare ties the clearance to the browser's user agent, so if it is rejected, paste the whole cURL text or pass --user-agent.");

            if (parsed.Cookies.All(c => c.Name != "cf_clearance"))
                log("what you pasted has no cf_clearance cookie; the check may not have been completed.");

            var solution = new SolverSolution { Status = 200, Url = target, UserAgent = parsed.UserAgent };
            foreach (var cookie in parsed.Cookies)
                solution.Cookies.Add(cookie);

            log("took over " + solution.Cookies.Count + " cookie(s) for " + host + ".");
            return solution;
        }

        private static void DefaultOpen(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidOperationException("not an http(s) address");

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    /// <summary>Tries each solver in turn (e.g. FlareSolverr first, the manual check if that is unavailable or fails).</summary>
    public sealed class FallbackSolver : IChallengeSolver
    {
        private readonly IChallengeSolver[] solvers;

        public FallbackSolver(params IChallengeSolver[] solvers)
        {
            this.solvers = solvers.Where(s => s != null).ToArray();
        }

        public Action<string> Log { get; set; } = _ => { };

        public SolverSolution Solve(HttpRequest request)
        {
            IOException last = null;

            foreach (var solver in solvers)
            {
                try
                {
                    return solver.Solve(request);
                }
                catch (SolverCancelledException)
                {
                    throw; // the person said no (or ran out of time); do not ask again another way
                }
                catch (IOException e)
                {
                    last = e;
                    Log(solver.GetType().Name + " could not solve it: " + e.Message);
                }
            }

            throw last ?? new IOException("No challenge solver is configured.");
        }
    }
}
