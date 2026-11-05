using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MangaDownloader.Net;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class CookieJarTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));

        public CookieJarTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        [Fact]
        public void MatchesDomainsAndPaths()
        {
            var jar = new CookieJar();
            jar.Set(new CookieEntry { Domain = ".example.com", Name = "a", Value = "1" });
            jar.Set(new CookieEntry { Domain = "sub.example.com", Name = "b", Value = "2", Path = "/private" });
            jar.Set(new CookieEntry { Domain = "other.org", Name = "c", Value = "3" });

            Assert.Equal(new[] { "a" }, jar.CookiesFor(new Uri("https://www.example.com/x")).Keys.ToArray());
            Assert.Equal(new[] { "a", "b" }, jar.CookiesFor(new Uri("https://sub.example.com/private/x")).Keys.OrderBy(k => k).ToArray());
            Assert.Equal(new[] { "a" }, jar.CookiesFor(new Uri("https://sub.example.com/public")).Keys.ToArray());
            Assert.Empty(jar.CookiesFor(new Uri("https://notexample.com/")));
        }

        [Fact]
        public void ExpiredCookiesAreIgnoredAndExpiringDeletes()
        {
            var jar = new CookieJar();
            jar.Set(new CookieEntry { Domain = "a.test", Name = "live", Value = "1", Expires = DateTime.UtcNow.AddDays(1) });
            jar.Set(new CookieEntry { Domain = "a.test", Name = "gone", Value = "1", Expires = DateTime.UtcNow.AddDays(1) });
            jar.Set(new CookieEntry { Domain = "a.test", Name = "gone", Value = "", Expires = DateTime.UtcNow.AddDays(-1) }); // server deletes it

            Assert.Equal(new[] { "live" }, jar.CookiesFor(new Uri("https://a.test/")).Keys.ToArray());
        }

        [Fact]
        public void ImportsHDoujinCookiesJsonAndRoundTrips()
        {
            var future = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ss");
            var file = Path.Combine(dir, "cookies.json");
            File.WriteAllText(file,
                "[{\"Domain\":\".example.com\",\"Expires\":\"" + future + "\",\"Name\":\"cf_clearance\",\"Value\":\"abc\",\"Path\":\"/\"}," +
                " {\"Domain\":\"old.test\",\"Expires\":\"2001-01-01T00:00:00\",\"Name\":\"stale\",\"Value\":\"x\",\"Path\":\"/\"}," +
                " {\"Domain\":\"sess.test\",\"Expires\":\"0001-01-01T00:00:00\",\"Name\":\"session\",\"Value\":\"y\",\"Path\":\"/\"}]");

            var jar = new CookieJar();
            Assert.Equal(2, jar.Load(file)); // the expired one is skipped

            Assert.Equal("abc", jar.CookiesFor(new Uri("https://www.example.com/"))["cf_clearance"]);
            Assert.Equal("y", jar.CookiesFor(new Uri("https://sess.test/"))["session"]);

            var saved = Path.Combine(dir, "saved", "cookies.json");
            jar.Save(saved); // session cookies are not persisted

            var reloaded = new CookieJar();
            Assert.Equal(1, reloaded.Load(saved));
            Assert.Equal("abc", reloaded.CookiesFor(new Uri("https://example.com/"))["cf_clearance"]);
            Assert.False(File.Exists(saved + ".tmp"));
        }

        [Fact]
        public void ParsesSetCookieAttributes()
        {
            var c = SetCookieParser.Parse("sid=abc123; Domain=.example.com; Path=/app; Max-Age=3600; HttpOnly; Secure", new Uri("https://www.example.com/app/login"));

            Assert.Equal("sid", c.Name);
            Assert.Equal("abc123", c.Value);
            Assert.Equal("example.com", c.Domain);
            Assert.Equal("/app", c.Path);
            Assert.InRange((c.Expires.Value - DateTime.UtcNow).TotalSeconds, 3500, 3700);

            var plain = SetCookieParser.Parse("a=b", new Uri("https://x.test/dir/page"));
            Assert.Equal("x.test", plain.Domain);
            Assert.Equal("/dir", plain.Path);
            Assert.Null(plain.Expires);
            Assert.Null(SetCookieParser.Parse("garbage", new Uri("https://x.test/")));
        }
    }

    public class CookieBackendTests
    {
        private static HttpResult Redirect(int status, string location, params string[] setCookies)
        {
            var r = new HttpResult { StatusCode = status };
            r.Headers["Location"] = location;

            foreach (var sc in setCookies)
                r.SetCookies.Add(SetCookieParser.Parse(sc, new Uri("https://site.test/")));

            return r;
        }

        [Fact]
        public void StoresSetCookiesAndSendsThemLater()
        {
            var inner = new ScriptedHttp(req =>
            {
                var r = new HttpResult { StatusCode = 200, Body = "ok" };

                if (req.Url.EndsWith("/login"))
                    r.SetCookies.Add(SetCookieParser.Parse("sid=s1; Max-Age=600", new Uri(req.Url)));

                return r;
            });
            var backend = new CookieBackend(inner, new CookieJar());

            backend.Send(new HttpRequest { Url = "https://site.test/login" });
            backend.Send(new HttpRequest { Url = "https://site.test/page" });
            backend.Send(new HttpRequest { Url = "https://other.test/page" });

            Assert.Empty(inner.Requests[0].Cookies);
            Assert.Equal("s1", inner.Requests[1].Cookies["sid"]);
            Assert.Empty(inner.Requests[2].Cookies);
        }

        [Fact]
        public void ExplicitRequestCookiesWinOverTheJar()
        {
            var inner = new ScriptedHttp(_ => new HttpResult { StatusCode = 200 });
            var jar = new CookieJar();
            jar.Set("site.test", "k", "jar");
            var backend = new CookieBackend(inner, jar);

            var request = new HttpRequest { Url = "https://site.test/" };
            request.Cookies["k"] = "explicit";
            backend.Send(request);

            Assert.Equal("explicit", inner.Requests.Single().Cookies["k"]);
        }

        [Fact]
        public void FollowsRedirectsKeepingCookiesFromEveryHop()
        {
            using var cancellation = new CancellationTokenSource();
            var inner = new ScriptedHttp(req =>
            {
                switch (req.Url)
                {
                    case "https://site.test/start": return Redirect(302, "/middle", "hop1=a");
                    case "https://site.test/middle": return Redirect(303, "https://site.test/end", "hop2=b");
                    default: return new HttpResult { StatusCode = 200, Body = "done" };
                }
            });

            var result = new CookieBackend(inner, new CookieJar()).Send(new HttpRequest { Url = "https://site.test/start", Method = "POST", Body = "x=1", CancellationToken = cancellation.Token, AllowInteractiveChallenge = false });

            Assert.Equal("done", result.Body);
            Assert.Equal("https://site.test/end", result.Url);
            Assert.Equal(3, inner.Requests.Count);
            Assert.All(inner.Requests, r => { Assert.Equal(cancellation.Token, r.CancellationToken); Assert.False(r.AllowInteractiveChallenge); });
            Assert.Equal("GET", inner.Requests[1].Method); // 302 after POST -> GET
            Assert.Null(inner.Requests[1].Body);
            Assert.Equal("a", inner.Requests[1].Cookies["hop1"]);
            Assert.Equal("a", inner.Requests[2].Cookies["hop1"]);
            Assert.Equal("b", inner.Requests[2].Cookies["hop2"]);
        }

        [Fact]
        public void FinalResponseIncludesCookiesSetOnEarlierRedirectHops()
        {
            var inner = new ScriptedHttp(req => req.Url.EndsWith("/login")
                ? Redirect(302, "/home", "sessionid=abc")
                : new HttpResult { StatusCode = 200, Body = "home" });

            var result = new CookieBackend(inner, new CookieJar()).Send(new HttpRequest { Url = "https://site.test/login", Method = "POST", Body = "u=1" });

            Assert.Equal("home", result.Body);
            Assert.Equal("abc", result.Cookies["sessionid"]);
            Assert.Contains(result.SetCookies, c => c.Name == "sessionid");
        }

        [Fact]
        public void HonoursAllowAutoRedirectFalse()
        {
            var inner = new ScriptedHttp(_ => Redirect(302, "/next"));
            var result = new CookieBackend(inner, new CookieJar()).Send(new HttpRequest { Url = "https://site.test/a", AllowAutoRedirect = false });

            Assert.Equal(302, result.StatusCode);
            Assert.Single(inner.Requests);
        }

        [Fact]
        public void StopsAtRedirectLoops()
        {
            var inner = new ScriptedHttp(_ => Redirect(302, "/loop"));
            var result = new CookieBackend(inner, new CookieJar()).Send(new HttpRequest { Url = "https://site.test/loop" });

            Assert.Equal(302, result.StatusCode);
            Assert.Equal(11, inner.Requests.Count);
        }
    }

    public class CloudflareTests
    {
        private static HttpResult Challenge(bool streamed = false)
        {
            var r = new HttpResult { StatusCode = 403, Body = streamed ? "" : "<html><title>Just a moment...</title></html>" };
            r.Headers["Server"] = "cloudflare";
            r.Headers["cf-mitigated"] = "challenge";
            return r;
        }

        private sealed class FakeSolver : IChallengeSolver
        {
            public int Calls;
            public string Token = "token";
            public Exception Error;
            public IHttpBackend BrowserSession;

            public SolverSolution Solve(HttpRequest request)
            {
                Calls++;
                if (Error != null)
                    throw Error;

                var s = new SolverSolution { Status = 200, UserAgent = "SolverBrowser/1.0", Body = "<html>real</html>", BrowserSession = BrowserSession };
                s.Cookies.Add(new CookieEntry { Name = "cf_clearance", Value = Token, Domain = ".site.test", Expires = DateTime.UtcNow.AddHours(1) });
                return s;
            }
        }

        private sealed class FakeBrowserSession : IHttpBackend, IDisposable
        {
            public bool Accepts = true;
            public int Calls;
            public int DisposeCalls;
            public HttpResult Send(HttpRequest request)
            {
                Calls++;
                return Accepts ? new HttpResult { StatusCode = 200, Body = "browser page" } : Challenge();
            }
            public HttpResult Download(HttpRequest request, Stream destination) => throw new NotSupportedException();
            public void Dispose() => DisposeCalls++;
        }

        [Fact]
        public void AcceptedCookieTransferClosesTheUnusedBrowserConnection()
        {
            var session = new FakeBrowserSession();
            var solver = new FakeSolver { BrowserSession = session };
            var jar = new CookieJar();
            var inner = new ScriptedHttp(req => req.Cookies.ContainsKey("cf_clearance")
                ? new HttpResult { StatusCode = 200, Body = "page" } : Challenge());
            using (var backend = new CloudflareBackend(new CookieBackend(inner, jar), jar, solver))
            {
                Assert.Equal("page", backend.Send(new HttpRequest { Url = "https://site.test/" }).Body);
                Assert.Equal(0, session.Calls);
                Assert.Equal(1, session.DisposeCalls);
            }
            Assert.Equal(1, session.DisposeCalls);
        }

        [Fact]
        public void BrowserConnectionIsReusedOnlyForItsOriginAndDisposedAtShutdown()
        {
            var session = new FakeBrowserSession();
            var solver = new FakeSolver { BrowserSession = session };
            var backend = new CloudflareBackend(new ScriptedHttp(_ => Challenge()), new CookieJar(), solver);
            try
            {
                Assert.Equal("browser page", backend.Send(new HttpRequest { Url = "https://site.test/a" }).Body);
                Assert.Equal("browser page", backend.Send(new HttpRequest { Url = "https://site.test/b" }).Body);
                Assert.Throws<IOException>(() => backend.Send(new HttpRequest { Url = "http://site.test/", AllowInteractiveChallenge = false }));
                Assert.Equal(2, session.Calls);
                Assert.Equal(1, solver.Calls);
                Assert.Equal(0, session.DisposeCalls);
            }
            finally { backend.Dispose(); }
            backend.Dispose();
            Assert.Equal(1, session.DisposeCalls);
        }

        [Fact]
        public void RejectedBrowserConnectionIsClosedBeforeTryingTheNextSolver()
        {
            var session = new FakeBrowserSession { Accepts = false };
            var first = new FakeSolver { BrowserSession = session };
            var second = new FakeSolver { Token = "accepted" };
            var jar = new CookieJar();
            var inner = new ScriptedHttp(req => req.Cookies.TryGetValue("cf_clearance", out var token) && token == "accepted"
                ? new HttpResult { StatusCode = 200, Body = "page" } : Challenge());
            using var backend = new CloudflareBackend(new CookieBackend(inner, jar), jar, new FallbackSolver(first, second));

            Assert.Equal("page", backend.Send(new HttpRequest { Url = "https://site.test/" }).Body);
            Assert.Equal(1, session.Calls);
            Assert.Equal(1, session.DisposeCalls);
            Assert.Equal(1, first.Calls);
            Assert.Equal(1, second.Calls);
        }

        [Fact]
        public void SiteDetectionDoesNotOpenAChallengeOrWaitForASolver()
        {
            var solver = new FakeSolver();
            var backend = new CloudflareBackend(new ScriptedHttp(_ => Challenge()), new CookieJar(), solver);
            var error = Assert.Throws<IOException>(() => backend.Send(new HttpRequest { Url = "https://site.test/", AllowInteractiveChallenge = false }));
            Assert.Contains("could not be inspected automatically", error.Message);
            Assert.Equal(0, solver.Calls);
        }

        [Fact]
        public void RecognisesChallengeResponses()
        {
            Assert.True(CloudflareBackend.IsChallenge(Challenge()));
            Assert.True(CloudflareBackend.IsChallenge(Challenge(streamed: true)));

            var plain403 = new HttpResult { StatusCode = 403, Body = "Forbidden" };
            Assert.False(CloudflareBackend.IsChallenge(plain403));

            var cf403 = new HttpResult { StatusCode = 403, Body = "Access denied by site rules" };
            cf403.Headers["Server"] = "cloudflare";
            Assert.False(CloudflareBackend.IsChallenge(cf403)); // a Cloudflare block page, not a solvable challenge

            Assert.False(CloudflareBackend.IsChallenge(new HttpResult { StatusCode = 200, Body = "Just a moment" }));
        }

        [Fact]
        public void SolvesOnceThenReusesCookiesAndUserAgent()
        {
            var jar = new CookieJar();
            var solver = new FakeSolver();
            var inner = new ScriptedHttp(req =>
                req.Cookies.ContainsKey("cf_clearance") || jar.CookiesFor(new Uri(req.Url)).ContainsKey("cf_clearance")
                    ? new HttpResult { StatusCode = 200, Body = "page" }
                    : Challenge());

            // Cloudflare outermost over the cookie layer, as NetworkStack builds it.
            var backend = new CloudflareBackend(new CookieBackend(inner, jar), jar, solver);

            Assert.Equal("page", backend.Send(new HttpRequest { Url = "https://site.test/a" }).Body);
            Assert.Equal("page", backend.Send(new HttpRequest { Url = "https://site.test/b" }).Body);

            Assert.Equal(1, solver.Calls);
            Assert.Equal("token", jar.CookiesFor(new Uri("https://site.test/"))["cf_clearance"]);
            Assert.Equal("SolverBrowser/1.0", inner.Requests.Last().Headers["User-Agent"]); // cf_clearance is bound to the solver's UA
        }

        [Fact]
        public void StreamedDownloadsAreRetriedAfterSolving()
        {
            var jar = new CookieJar();
            var solver = new FakeSolver();
            var inner = new ScriptedHttp(req =>
                jar.CookiesFor(new Uri(req.Url)).ContainsKey("cf_clearance") ? new HttpResult { StatusCode = 200, Body = "IMG" } : Challenge(streamed: true));
            var backend = new CloudflareBackend(new CookieBackend(inner, jar), jar, solver);

            using (var ms = new MemoryStream())
            {
                var result = backend.Download(new HttpRequest { Url = "https://site.test/img.jpg" }, ms);
                Assert.Equal(200, result.StatusCode);
                Assert.Equal("IMG", Encoding.UTF8.GetString(ms.ToArray()));
            }
        }

        [Fact]
        public void WithoutASolverTheErrorSaysWhatToDo()
        {
            var jar = new CookieJar();
            var backend = new CloudflareBackend(new CookieBackend(new ScriptedHttp(_ => Challenge()), jar), jar, null);

            var e = Assert.Throws<IOException>(() => backend.Send(new HttpRequest { Url = "https://site.test/" }));
            Assert.Contains("--flaresolverr", e.Message);
        }

        [Fact]
        public void RaisesSolvedAfterTheCookiesAreInTheJar()
        {
            var jar = new CookieJar();
            var solver = new FakeSolver();
            var inner = new ScriptedHttp(req =>
                jar.CookiesFor(new Uri(req.Url)).ContainsKey("cf_clearance") ? new HttpResult { StatusCode = 200, Body = "page" } : Challenge());
            var backend = new CloudflareBackend(new CookieBackend(inner, jar), jar, solver);
            var jarHadClearanceWhenRaised = false;
            backend.Solved += () => jarHadClearanceWhenRaised = jar.CookiesFor(new Uri("https://site.test/")).ContainsKey("cf_clearance");

            backend.Send(new HttpRequest { Url = "https://site.test/a" });
            backend.Send(new HttpRequest { Url = "https://site.test/b" });

            Assert.True(jarHadClearanceWhenRaised);
        }

        [Fact]
        public void GivesUpWhenTheSolvedCookiesAreStillRejected()
        {
            var jar = new CookieJar();
            var solver = new FakeSolver();
            var backend = new CloudflareBackend(new CookieBackend(new ScriptedHttp(_ => Challenge()), jar), jar, solver);

            var e = Assert.Throws<IOException>(() => backend.Send(new HttpRequest { Url = "https://site.test/" }));
            Assert.Contains("not cleared", e.Message);
            Assert.Equal(1, solver.Calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RejectedCookiesTryNestedFallbacksBeforeReportingSuccess(bool streamed)
        {
            var jar = new CookieJar();
            var rejected = new FakeSolver { Token = "rejected" };
            var popup = new FakeSolver { Token = "also-rejected" };
            var paste = new FakeSolver { Token = "accepted" };
            var unused = new FakeSolver();
            var inner = new ScriptedHttp(req =>
                req.Cookies.TryGetValue("cf_clearance", out var token) && token == "accepted"
                    ? new HttpResult { StatusCode = 200, Body = "page" } : Challenge(streamed));
            var backend = new CloudflareBackend(new CookieBackend(inner, jar), jar,
                new FallbackSolver(rejected, new FallbackSolver(popup, paste), unused));
            var solved = 0;
            backend.Solved += () =>
            {
                Assert.Equal("accepted", jar.CookiesFor(new Uri("https://site.test/"))["cf_clearance"]);
                solved++;
            };

            using var output = new MemoryStream();
            var request = new HttpRequest { Url = "https://site.test/page" };
            var result = streamed ? backend.Download(request, output) : backend.Send(request);

            Assert.Equal(200, result.StatusCode);
            Assert.Equal("page", streamed ? Encoding.UTF8.GetString(output.ToArray()) : result.Body);
            Assert.Equal(1, rejected.Calls);
            Assert.Equal(1, popup.Calls);
            Assert.Equal(1, paste.Calls);
            Assert.Equal(0, unused.Calls);
            Assert.Equal(4, inner.Requests.Count);
            Assert.Equal(1, solved);
            Assert.Equal("SolverBrowser/1.0", inner.Requests.Last().Headers["User-Agent"]);
        }

        [Fact]
        public void ExhaustedFallbacksDoNotReportRejectedCookiesAsSolved()
        {
            var first = new FakeSolver();
            var second = new FakeSolver();
            var jar = new CookieJar();
            var inner = new ScriptedHttp(_ => Challenge());
            var backend = new CloudflareBackend(new CookieBackend(inner, jar), jar, new FallbackSolver(first, second));
            var solved = 0;
            backend.Solved += () => solved++;

            var error = Assert.Throws<IOException>(() => backend.Send(new HttpRequest { Url = "https://site.test/" }));

            Assert.Contains("cookies were rejected", error.Message);
            Assert.Equal(1, first.Calls);
            Assert.Equal(1, second.Calls);
            Assert.Equal(3, inner.Requests.Count);
            Assert.Equal(0, solved);
        }

        [Fact]
        public void CancellingAfterRejectedCookiesDoesNotAskAnotherSolver()
        {
            var rejected = new FakeSolver();
            var cancelled = new FakeSolver { Error = new SolverCancelledException("window closed") };
            var unused = new FakeSolver();
            var jar = new CookieJar();
            var backend = new CloudflareBackend(new CookieBackend(new ScriptedHttp(_ => Challenge()), jar), jar,
                new FallbackSolver(rejected, new FallbackSolver(cancelled, unused)));

            Assert.Throws<SolverCancelledException>(() => backend.Send(new HttpRequest { Url = "https://site.test/" }));

            Assert.Equal(1, rejected.Calls);
            Assert.Equal(1, cancelled.Calls);
            Assert.Equal(0, unused.Calls);
        }

        [Fact]
        public void ParsesFlareSolverrResponses()
        {
            var solution = FlareSolverrClient.ParseResponse(
                "{\"status\":\"ok\",\"message\":\"\",\"solution\":{\"url\":\"https://site.test/\",\"status\":200,\"response\":\"<html>x</html>\"," +
                "\"userAgent\":\"UA/1\",\"cookies\":[{\"name\":\"cf_clearance\",\"value\":\"v\",\"domain\":\".site.test\",\"path\":\"/\",\"expires\":1893456000}," +
                "{\"name\":\"sess\",\"value\":\"s\",\"domain\":\"site.test\",\"path\":\"/\",\"expires\":-1}]}}");

            Assert.Equal("UA/1", solution.UserAgent);
            Assert.Equal("<html>x</html>", solution.Body);
            Assert.Equal(2030, solution.Cookies[0].Expires.Value.Year);
            Assert.Null(solution.Cookies[1].Expires);

            var e = Assert.Throws<IOException>(() => FlareSolverrClient.ParseResponse("{\"status\":\"error\",\"message\":\"Timeout\"}"));
            Assert.Contains("Timeout", e.Message);
        }

        [Fact]
        public void TalksToAFlareSolverrEndpoint()
        {
            var port = FreePort();
            string received = null;

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();

                var server = Task.Run(() =>
                {
                    var ctx = listener.GetContext();
                    using (var reader = new StreamReader(ctx.Request.InputStream))
                        received = ctx.Request.HttpMethod + " " + ctx.Request.Url.AbsolutePath + " " + reader.ReadToEnd();

                    var body = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"solution\":{\"url\":\"u\",\"status\":200,\"response\":\"R\",\"userAgent\":\"UA\",\"cookies\":[]}}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                    ctx.Response.Close();
                });

                using (var client = new FlareSolverrClient($"http://localhost:{port}"))
                {
                    var solution = client.Solve(new HttpRequest { Url = "https://site.test/x" });
                    Assert.Equal("R", solution.Body);
                }

                server.Wait(5000);
            }

            Assert.StartsWith("POST /v1 ", received);
            Assert.Contains("\"cmd\":\"request.get\"", received);
            Assert.Contains("\"url\":\"https://site.test/x\"", received);
        }

        [Fact]
        public void ReportsAnUnreachableSolverClearly()
        {
            using (var client = new FlareSolverrClient($"http://localhost:{FreePort()}"))
            {
                var e = Assert.Throws<IOException>(() => client.Solve(new HttpRequest { Url = "https://site.test/" }));
                Assert.Contains("FlareSolverr is not reachable", e.Message);
            }
        }

        internal static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }

    public class NetworkErrorTests
    {
        [Fact]
        public void ExplainsUnresolvableHostsAndTimeouts()
        {
            var dns = new HttpRequestException("No such host is known. (x.invalid:443)", new SocketException((int)SocketError.HostNotFound));
            var text = NetworkErrors.Describe(dns);
            Assert.Contains("could not resolve the host name", text);
            Assert.Contains("--proxy", text);

            Assert.Contains("timed out", NetworkErrors.Describe(new TaskCanceledException()));
            Assert.Contains("ConnectionRefused", NetworkErrors.Describe(new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused))));
        }

        [Fact]
        public void UnresolvableHostSurfacesAsAReportErrorNotACrash()
        {
            var http = new MangaDownloader.Net.HttpClientBackend();
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "t.lua"), "function Register() module.Name='T' module.Domains.Add('nonexistent-host.invalid') end function GetInfo() end");
            var engine = new MangaDownloader.Core.Download.DownloadEngine(MangaDownloader.Core.ModuleRepository.LoadDirectory(dir, http), http, new MangaDownloader.Core.Download.DownloadOptions { OutputDirectory = dir });

            var report = engine.Download("https://nonexistent-host.invalid/g/1").Single();

            Assert.False(report.Success);
            Assert.Contains("resolve", report.Errors.Single());
        }
    }

    public class ProxyTests
    {
        [Fact]
        public async Task CancellationStopsAnUnresponsiveHttpRequestThroughTheCookieLayer()
        {
            var port = CloudflareTests.FreePort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            using var transport = new HttpClientBackend();
            var backend = new CookieBackend(transport, new CookieJar());
            using var cancellation = new CancellationTokenSource();
            var accepted = listener.GetContextAsync();
            var request = Task.Run(() => backend.Send(new HttpRequest { Url = $"http://127.0.0.1:{port}/", CancellationToken = cancellation.Token }));
            try
            {
                Assert.Same(accepted, await Task.WhenAny(accepted, Task.Delay(5000)));
                await accepted;
                cancellation.Cancel();
                Assert.Same(request, await Task.WhenAny(request, Task.Delay(2000)));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await request; });
            }
            finally { cancellation.Cancel(); listener.Close(); }
        }

        [Fact]
        public void ParsesProxyUrls()
        {
            var plain = ProxySettings.Parse("http://proxy.test:8080");
            Assert.Equal("http://proxy.test:8080", plain.Url);
            Assert.Null(plain.Username);

            var auth = ProxySettings.Parse("http://bob:p%40ss@proxy.test:3128");
            Assert.Equal("http://proxy.test:3128", auth.Url);
            Assert.Equal("bob", auth.Username);
            Assert.Equal("p@ss", auth.Password);

            Assert.Equal("http://proxy.test:8000", ProxySettings.Parse("proxy.test:8000").Url); // scheme defaults to http
            Assert.Throws<NotSupportedException>(() => ProxySettings.Parse("socks5://proxy.test:1080"));
        }

        [Fact]
        public void RequestsGoThroughTheConfiguredProxyWithCredentials()
        {
            // A minimal raw-socket proxy: demands Basic auth (407), then answers the absolute-URI request.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var seen = new List<string>();
            string authorization = null;

            var server = Task.Run(() =>
            {
                for (var i = 0; i < 2; i++)
                {
                    using (var client = listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    {
                        var reader = new StreamReader(stream, Encoding.ASCII);
                        var requestLine = reader.ReadLine();
                        string header, auth = null;

                        while (!string.IsNullOrEmpty(header = reader.ReadLine()))
                            if (header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                                auth = header.Substring(header.IndexOf(':') + 1).Trim();

                        seen.Add(requestLine);
                        authorization = auth ?? authorization;

                        var response = auth == null
                            ? string.Join("\r\n", "HTTP/1.1 407 Proxy Authentication Required", "Proxy-Authenticate: Basic realm=\"t\"", "Content-Length: 0", "Connection: close", "", "")
                            : string.Join("\r\n", "HTTP/1.1 200 OK", "Content-Length: 9", "Connection: close", "", "via proxy");
                        var bytes = Encoding.ASCII.GetBytes(response);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
            });

            try
            {
                using (var client = new HttpClientBackend(new HttpClientOptions { Proxy = ProxySettings.Parse($"http://bob:pw@127.0.0.1:{port}") }))
                {
                    // origin.invalid does not resolve: only the proxy can answer.
                    var result = client.Send(new HttpRequest { Url = "http://origin.invalid/some/path?q=1" });
                    Assert.Equal("via proxy", result.Body);
                }

                server.Wait(5000);
            }
            finally
            {
                listener.Stop();
            }

            Assert.All(seen, line => Assert.Equal("GET http://origin.invalid/some/path?q=1 HTTP/1.1", line));
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("bob:pw")), authorization);
        }

        [Fact]
        public void CustomUserAgentIsSent()
        {
            var port = CloudflareTests.FreePort();
            string ua = null;

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();

                var server = Task.Run(() =>
                {
                    var ctx = listener.GetContext();
                    ua = ctx.Request.UserAgent;
                    ctx.Response.Close();
                });

                using (var client = new HttpClientBackend(new HttpClientOptions { UserAgent = "Configured/1.0" }))
                    client.Send(new HttpRequest { Url = $"http://localhost:{port}/" });

                server.Wait(5000);
            }

            Assert.Equal("Configured/1.0", ua);
        }
    }
}
