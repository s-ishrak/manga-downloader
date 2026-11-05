using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MangaDownloader.Net
{
    public sealed class SolverSolution
    {
        public int Status { get; set; }
        public string Url { get; set; }
        public string Body { get; set; }
        public string UserAgent { get; set; }
        /// <summary>Optional live browser connection when clearance cannot be transferred to HttpClient. Ownership passes to CloudflareBackend.</summary>
        public IHttpBackend BrowserSession { get; set; }
        public IList<CookieEntry> Cookies { get; } = new List<CookieEntry>();
    }

    public interface IChallengeSolver
    {
        /// <summary>Loads <paramref name="request"/> in a browser and returns the cookies/user agent, optionally retaining its verified connection.</summary>
        SolverSolution Solve(HttpRequest request);
    }

    /// <summary>Client for the FlareSolverr HTTP API (<c>POST /v1</c>, cmd request.get / request.post).</summary>
    public sealed class FlareSolverrClient : IChallengeSolver, IDisposable
    {
        private readonly HttpClient client;
        private readonly string endpoint;
        private readonly int timeoutMilliseconds;

        public FlareSolverrClient(string baseUrl = "http://localhost:8191", int timeoutMilliseconds = 60000)
        {
            this.timeoutMilliseconds = timeoutMilliseconds;
            endpoint = baseUrl.TrimEnd('/') + (baseUrl.TrimEnd('/').EndsWith("/v1") ? "" : "/v1");
            client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds + 15000) };
        }

        public SolverSolution Solve(HttpRequest request)
        {
            var post = request.Method == "POST";
            string payload;

            using (var stream = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(stream))
                {
                    w.WriteStartObject();
                    w.WriteString("cmd", post ? "request.post" : "request.get");
                    w.WriteString("url", request.Url);
                    w.WriteNumber("maxTimeout", timeoutMilliseconds);

                    if (post)
                        w.WriteString("postData", request.Body ?? "");

                    w.WriteEndObject();
                }

                payload = Encoding.UTF8.GetString(stream.ToArray());
            }

            string responseText;

            try
            {
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                using (var response = client.PostAsync(endpoint, content).GetAwaiter().GetResult())
                    responseText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (HttpRequestException e)
            {
                throw new IOException("FlareSolverr is not reachable at " + endpoint + ": " + e.Message, e);
            }

            return ParseResponse(responseText);
        }

        public static SolverSolution ParseResponse(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

                if (Str(root, "status") != "ok")
                    throw new IOException("FlareSolverr could not solve the challenge: " + (Str(root, "message") ?? "unknown error"));

                var s = root.GetProperty("solution");
                var solution = new SolverSolution
                {
                    Status = s.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 200,
                    Url = Str(s, "url"),
                    Body = Str(s, "response") ?? "",
                    UserAgent = Str(s, "userAgent"),
                };

                if (s.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cookies.EnumerateArray())
                    {
                        var entry = new CookieEntry { Name = Str(c, "name"), Value = Str(c, "value") ?? "", Domain = Str(c, "domain"), Path = Str(c, "path") ?? "/" };

                        // FlareSolverr reports expiry as epoch seconds; -1 means a session cookie.
                        if (c.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number && ex.GetDouble() > 0)
                            entry.Expires = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ex.GetDouble());

                        solution.Cookies.Add(entry);
                    }
                }

                return solution;
            }
        }

        public void Dispose() => client.Dispose();
    }

    /// <summary>
    /// Detects Cloudflare challenge responses and, when a solver is configured, clears them once per site: the solved
    /// cookies go into the jar and the browser's user agent (cf_clearance is bound to it) is used for all later requests to that host.
    /// If cookie transfer is rejected, a solver's live browser connection can serve subsequent requests to the same origin.
    /// Put this outside the <see cref="CookieBackend"/> so the retry picks up the new cookies.
    /// </summary>
    public sealed class CloudflareBackend : IHttpBackend, IDisposable
    {
        private readonly IHttpBackend inner;
        private readonly CookieJar jar;
        private readonly IChallengeSolver solver;
        private readonly Dictionary<string, string> userAgents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private readonly object solveGate = new object();
        private readonly Dictionary<string, IHttpBackend> browserSessions = new Dictionary<string, IHttpBackend>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> lastSolved = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (DateTime At, string Message)> lastCancelled = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);

        public CloudflareBackend(IHttpBackend inner, CookieJar jar, IChallengeSolver solver)
        {
            this.inner = inner;
            this.jar = jar;
            this.solver = solver;
        }

        public Action<string> Log { get; set; } = _ => { };

        /// <summary>Raised after a challenge was cleared and its cookies are in the jar (so the application can persist them right away).</summary>
        public event Action Solved;

        public HttpResult Send(HttpRequest request) => Execute(request, null);

        public HttpResult Download(HttpRequest request, Stream destination) => Execute(request, destination);

        public static bool IsChallenge(HttpResult r)
        {
            if (r.StatusCode != 403 && r.StatusCode != 503 && r.StatusCode != 429)
                return false;

            if (r.Headers.TryGetValue("cf-mitigated", out var mitigated) && mitigated.IndexOf("challenge", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var cloudflare = r.Headers.TryGetValue("Server", out var server) && server.IndexOf("cloudflare", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!cloudflare)
                return false;

            // Streamed downloads have no body to inspect; a Cloudflare 403/503 on an asset is almost always the challenge.
            var body = r.Body ?? "";
            return body.Length == 0 || body.Contains("Just a moment") || body.Contains("cf-chl") || body.Contains("challenge-platform") || body.Contains("cf-browser-verification");
        }

        private HttpResult Execute(HttpRequest request, Stream destination)
        {
            var started = DateTime.UtcNow;
            ApplyUserAgent(request);
            var origin = new Uri(request.Url).GetLeftPart(UriPartial.Authority);
            var session = SessionFor(origin);
            HttpResult result;
            try { result = Run(request, destination, session); }
            catch (IOException) when (session != null)
            {
                RemoveSession(origin, session);
                throw;
            }
            if (session != null && IsChallenge(result))
                RemoveSession(origin, session);

            if (!IsChallenge(result) || solver == null)
            {
                if (IsChallenge(result))
                    throw new IOException("Blocked by a Cloudflare challenge at " + request.Url + ". Pass --browser to clear it in a browser window, or --flaresolverr URL, or import a cf_clearance cookie.");

                return result;
            }

            var host = new Uri(request.Url).Host;

            if (!request.AllowInteractiveChallenge)
                throw new IOException("The site requires browser verification and could not be inspected automatically. Use a supported site module or import its clearance cookies before trying again.");

            // One solve per site at a time. Requests that hit the same challenge while it is being solved (parallel page downloads) wait here and
            // then just retry with the cookies the first solve produced, instead of opening another browser window each.
            HttpResult retry = null;
            lock (solveGate)
            {
                if (lastCancelled.TryGetValue(host, out var cancelled) && cancelled.At >= started)
                    throw new SolverCancelledException(cancelled.Message);

                if (!lastSolved.TryGetValue(host, out var solvedAt) || solvedAt < started)
                {
                    Log("Cloudflare challenge at " + host + "; asking the solver...");

                    void Validate(SolverSolution solution)
                    {
                        foreach (var cookie in solution.Cookies)
                        {
                            if (string.IsNullOrEmpty(cookie.Domain))
                                cookie.Domain = host;

                            jar.Set(cookie);
                        }

                        if (!string.IsNullOrEmpty(solution.UserAgent))
                        {
                            lock (gate)
                                userAgents[host] = solution.UserAgent;
                        }

                        var retained = false;
                        try
                        {
                            var response = Retry();
                            if (IsChallenge(response) && solution.BrowserSession != null)
                            {
                                Log("Clearance cookies were rejected by the downloader; using the verified browser connection for " + host + ". Keep that window open while this site is in use.");
                                response = Retry(solution.BrowserSession);
                                RequireCleared(response);
                                lock (gate)
                                    browserSessions[origin] = solution.BrowserSession;
                                retained = true;
                            }
                            RequireCleared(response);
                            retry = response;
                        }
                        finally
                        {
                            if (!retained)
                                (solution.BrowserSession as IDisposable)?.Dispose();
                        }
                    }

                    try
                    {
                        if (solver is FallbackSolver fallback)
                            fallback.Solve(request, Validate);
                        else
                            Validate(solver.Solve(request));
                    }
                    catch (SolverCancelledException e)
                    {
                        lastCancelled[host] = (DateTime.UtcNow, e.Message);
                        throw;
                    }

                    lastSolved[host] = DateTime.UtcNow;
                    Solved?.Invoke();
                }
            }

            var final = retry ?? Retry(SessionFor(origin));
            RequireCleared(final);
            return final;

            HttpResult Retry(IHttpBackend backend = null)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                ApplyUserAgent(request);

                if (destination != null)
                    destination.SetLength(0);

                return Run(request, destination, backend);
            }

            void RequireCleared(HttpResult response)
            {
                if (IsChallenge(response))
                    throw new IOException("Cloudflare challenge at " + request.Url + " was not cleared by the solver (cookies were rejected). Complete verification in your browser using the same network connection as the downloader; if this persists, the site's protection may not accept this downloader's connection.");
            }
        }

        private HttpResult Run(HttpRequest request, Stream destination, IHttpBackend backend = null)
        {
            var response = destination == null ? (backend ?? inner).Send(request) : (backend ?? inner).Download(request, destination);
            if (backend != null)
                foreach (var cookie in response.SetCookies)
                    jar.Set(cookie);
            return response;
        }

        private IHttpBackend SessionFor(string origin)
        {
            lock (gate)
                return browserSessions.TryGetValue(origin, out var session) ? session : null;
        }

        private void RemoveSession(string origin, IHttpBackend session)
        {
            lock (gate)
                if (browserSessions.TryGetValue(origin, out var current) && ReferenceEquals(current, session))
                    browserSessions.Remove(origin);
            (session as IDisposable)?.Dispose();
        }

        public void Dispose()
        {
            IHttpBackend[] sessions;
            lock (gate)
            {
                sessions = browserSessions.Values.Distinct().ToArray();
                browserSessions.Clear();
            }
            foreach (var session in sessions)
                (session as IDisposable)?.Dispose();
        }

        private void ApplyUserAgent(HttpRequest request)
        {
            string ua;

            lock (gate)
                userAgents.TryGetValue(new Uri(request.Url).Host, out ua);

            if (ua != null)
                request.Headers["User-Agent"] = ua;
        }
    }
}
