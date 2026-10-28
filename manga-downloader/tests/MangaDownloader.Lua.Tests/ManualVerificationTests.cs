using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MangaDownloader.Net;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class ClearanceParserTests
    {
        private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

        private static readonly string BashCurl =
            "curl 'https://site.test/gallery/' \\\n" +
            "  -H 'accept: text/html,application/xhtml+xml' \\\n" +
            "  -H 'accept-language: en-US,en;q=0.9' \\\n" +
            "  -H 'cookie: cf_clearance=TOKEN.abc-123; PHPSESSID=sess1; theme=dark' \\\n" +
            "  -H 'user-agent: " + Ua + "' \\\n" +
            "  --compressed";

        private static readonly string CmdCurl =
            "curl ^\"https://site.test/gallery/^\" ^\n" +
            "  -H ^\"accept: text/html^\" ^\n" +
            "  -H ^\"cookie: cf_clearance=TOKEN.abc-123; PHPSESSID=sess1; theme=dark^\" ^\n" +
            "  -H ^\"user-agent: " + Ua + "^\" ^\n" +
            "  --compressed";

        [Theory]
        [InlineData("bash")]
        [InlineData("cmd")]
        public void ReadsCookiesAndUserAgentFromCopyAsCurl(string flavour)
        {
            var parsed = ClearanceParser.Parse(flavour == "bash" ? BashCurl : CmdCurl, "site.test");

            Assert.Equal(Ua, parsed.UserAgent);
            Assert.Equal(new[] { "cf_clearance", "PHPSESSID", "theme" }, parsed.Cookies.Select(c => c.Name).ToArray());
            Assert.Equal("TOKEN.abc-123", parsed.Cookies[0].Value);
            Assert.All(parsed.Cookies, c => Assert.Equal("site.test", c.Domain));
        }

        [Fact]
        public void OnlyTheClearanceIsKeptBetweenRuns()
        {
            var parsed = ClearanceParser.Parse(BashCurl, "site.test");

            var clearance = parsed.Cookies.Single(c => c.Name == "cf_clearance");
            Assert.InRange((clearance.Expires.Value - DateTime.UtcNow).TotalHours, 11.9, 12.1);
            Assert.All(parsed.Cookies.Where(c => c.Name != "cf_clearance"), c => Assert.Null(c.Expires));
        }

        [Fact]
        public void ReadsTheCookieFlagAndPowerShellFlavours()
        {
            var b = ClearanceParser.Parse("curl 'https://site.test/' -b 'cf_clearance=ABC; x=1' -H 'user-agent: UA/1'", "site.test");
            Assert.Equal(new[] { "cf_clearance", "x" }, b.Cookies.Select(c => c.Name).ToArray());
            Assert.Equal("UA/1", b.UserAgent);

            var ps = ClearanceParser.Parse("Invoke-WebRequest -UseBasicParsing -Uri \"https://site.test/\" -WebSession $session -Headers @{\n\"cookie\"=\"cf_clearance=PSTOKEN; a=b\"\n\"user-agent\"=\"UA/2\"\n}", "site.test");
            Assert.Equal("PSTOKEN", ps.Cookies.First().Value);
            Assert.Equal("UA/2", ps.UserAgent);
        }

        [Fact]
        public void ReadsPlainHeadersAndBareTokens()
        {
            var headers = ClearanceParser.Parse("Cookie: cf_clearance=ZZZ; a=b\nUser-Agent: UA/3", "site.test");
            Assert.Equal(new[] { "cf_clearance", "a" }, headers.Cookies.Select(c => c.Name).ToArray());
            Assert.Equal("UA/3", headers.UserAgent);

            var pair = ClearanceParser.Parse("cf_clearance=QQQ", "site.test");
            Assert.Equal("QQQ", pair.Cookies.Single().Value);
            Assert.Null(pair.UserAgent);

            var token = ClearanceParser.Parse("abcDEF123456.7890-ghijkLMNOP_qrs\n" + Ua, "site.test");
            Assert.Equal("cf_clearance", token.Cookies.Single().Name);
            Assert.Equal(Ua, token.UserAgent);
        }

        [Fact]
        public void FindsNothingInUnrelatedText()
        {
            Assert.Empty(ClearanceParser.Parse("", "site.test").Cookies);
            Assert.Empty(ClearanceParser.Parse("hello world, nothing here", "site.test").Cookies);
            Assert.Empty(ClearanceParser.Parse("curl 'https://site.test/' -H 'accept: */*'", "site.test").Cookies);
        }
    }

    public class ManualChallengeSolverTests
    {
        private sealed class FakePrompt : IVerificationPrompt
        {
            public string Answer;
            public string AskedUrl;
            public string AskedHost;

            public string Ask(string url, string host)
            {
                AskedUrl = url;
                AskedHost = host;
                return Answer;
            }
        }

        private const string Pasted = "curl 'https://site.test/g/1' -H 'cookie: cf_clearance=GOOD' -H 'user-agent: BrowserUA/9'";

        [Fact]
        public void OpensThePageInTheBrowserThenUsesWhatThePersonPastes()
        {
            var prompt = new FakePrompt { Answer = Pasted };
            var opened = new List<string>();
            var solver = new ManualChallengeSolver(prompt, opened.Add);

            var solution = solver.Solve(new HttpRequest { Url = "https://site.test/g/1" });

            Assert.Equal(new[] { "https://site.test/g/1" }, opened.ToArray());
            Assert.Equal(("https://site.test/g/1", "site.test"), (prompt.AskedUrl, prompt.AskedHost));
            Assert.Equal("BrowserUA/9", solution.UserAgent);
            Assert.Equal("GOOD", solution.Cookies.Single().Value);
        }

        [Fact]
        public void APostIsHandledByOpeningTheSiteItself()
        {
            var opened = new List<string>();
            new ManualChallengeSolver(new FakePrompt { Answer = Pasted }, opened.Add).Solve(new HttpRequest { Url = "https://site.test/login", Method = "POST", Body = "x=1" });

            Assert.Equal(new[] { "https://site.test/" }, opened.ToArray());
        }

        [Fact]
        public void CancellingAndUselessPastesAreReportedClearly()
        {
            Assert.Contains("cancelled", Assert.ThrowsAny<IOException>(() =>
                new ManualChallengeSolver(new FakePrompt { Answer = null }, _ => { }).Solve(new HttpRequest { Url = "https://site.test/" })).Message);

            var e = Assert.Throws<IOException>(() =>
                new ManualChallengeSolver(new FakePrompt { Answer = "no cookies here" }, _ => { }).Solve(new HttpRequest { Url = "https://site.test/" }));
            Assert.Contains("Copy as cURL", e.Message);
        }

        [Fact]
        public void ABrowserThatWillNotOpenOnlyCostsAHintNotTheCheck()
        {
            var log = new List<string>();
            var solver = new ManualChallengeSolver(new FakePrompt { Answer = Pasted }, _ => throw new InvalidOperationException("no default browser"), log.Add);

            var solution = solver.Solve(new HttpRequest { Url = "https://site.test/g/1" });

            Assert.Single(solution.Cookies);
            Assert.Contains(log, l => l.Contains("open https://site.test/g/1 yourself"));
        }

        [Fact]
        public void WarnsWhenTheClearanceOrUserAgentIsMissing()
        {
            var log = new List<string>();
            new ManualChallengeSolver(new FakePrompt { Answer = "other=1" }, _ => { }, log.Add).Solve(new HttpRequest { Url = "https://site.test/" });

            Assert.Contains(log, l => l.Contains("no user agent"));
            Assert.Contains(log, l => l.Contains("no cf_clearance"));
        }

        [Fact]
        public void ClearsTheChallengeForTheHttpStackAndSendsTheBrowsersUserAgent()
        {
            var jar = new CookieJar();
            var inner = new ScriptedHttp(req =>
            {
                if (req.Cookies.TryGetValue("cf_clearance", out var v) && v == "GOOD")
                    return new HttpResult { StatusCode = 200, Body = "real page" };

                var r = new HttpResult { StatusCode = 403, Body = "<title>Just a moment...</title>" };
                r.Headers["Server"] = "cloudflare";
                r.Headers["cf-mitigated"] = "challenge";
                return r;
            });
            var backend = new CloudflareBackend(new CookieBackend(inner, jar), jar, new ManualChallengeSolver(new FakePrompt { Answer = Pasted }, _ => { }));

            var result = backend.Send(new HttpRequest { Url = "https://site.test/g/1" });

            Assert.Equal("real page", result.Body);
            Assert.Equal("BrowserUA/9", inner.Requests.Last().Headers["User-Agent"]);
            Assert.Equal("GOOD", jar.CookiesFor(new Uri("https://site.test/"))["cf_clearance"]);
        }
    }

    public class ConsoleVerificationPromptTests
    {
        [Fact]
        public void ShowsTheStepsAndReadsAPastedBlockUntilAnEmptyLine()
        {
            var output = new StringWriter();
            var prompt = new ConsoleVerificationPrompt(new StringReader("curl 'https://x.test/' \\\n  -H 'cookie: a=b'\n\nignored"), output);

            var text = prompt.Ask("https://x.test/", "x.test");

            Assert.Contains("-H 'cookie: a=b'", text);
            Assert.DoesNotContain("ignored", text);
            Assert.Contains("x.test", output.ToString());
            Assert.Contains("Copy as cURL", output.ToString());
            Assert.Contains("https://x.test/", output.ToString());
        }

        [Fact]
        public void AnEmptyPasteCancelsAndANonInteractiveRunExplainsWhy()
        {
            Assert.Null(new ConsoleVerificationPrompt(new StringReader("\n"), new StringWriter()).Ask("https://x.test/", "x.test"));
            Assert.Null(new ConsoleVerificationPrompt(new StringReader(""), new StringWriter()).Ask("https://x.test/", "x.test"));

            var e = Assert.Throws<IOException>(() => new ConsoleVerificationPrompt(new StringReader(""), new StringWriter(), interactive: false).Ask("https://x.test/", "x.test"));
            Assert.Contains("no terminal", e.Message);
        }

        [Fact]
        public void TheBrokerForwardsToWhateverIsPluggedIn()
        {
            var broker = new VerificationBroker();
            Assert.Throws<IOException>(() => broker.Ask("https://x.test/", "x.test"));

            broker.Target = new ConsoleVerificationPrompt(new StringReader("a=b\n\n"), new StringWriter());
            Assert.Equal("a=b", broker.Ask("https://x.test/", "x.test").Trim());
        }
    }
}
