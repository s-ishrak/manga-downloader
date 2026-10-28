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
        public IList<CookieEntry> Cookies { get; } = new List<CookieEntry>();
    }

    public interface IChallengeSolver
    {
        /// <summary>Loads <paramref name="request"/> in a real browser and returns the page plus the cookies/user agent that cleared the challenge.</summary>
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
    /// Put this outside the <see cref="CookieBackend"/> so the retry picks up the new cookies.
    /// </summary>
    public sealed class CloudflareBackend : IHttpBackend
    {
        private readonly IHttpBackend inner;
        private readonly CookieJar jar;
        private readonly IChallengeSolver solver;
        private readonly Dictionary<string, string> userAgents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private readonly object solveGate = new object();
        private readonly Dictionary<string, DateTime> lastSolved = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

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
            var result = Run(request, destination);

            if (!IsChallenge(result) || solver == null)
            {
                if (IsChallenge(result))
                    throw new IOException("Blocked by a Cloudflare challenge at " + request.Url + ". Pass --browser to clear it in a browser window, or --flaresolverr URL, or import a cf_clearance cookie.");

                return result;
            }

            var host = new Uri(request.Url).Host;

            // One solve per site at a time. Requests that hit the same challenge while it is being solved (parallel page downloads) wait here and
            // then just retry with the cookies the first solve produced, instead of opening another browser window each.
            lock (solveGate)
            {
                if (!lastSolved.TryGetValue(host, out var solvedAt) || solvedAt < started)
                {
                    Log("Cloudflare challenge at " + host + "; asking the solver...");

                    var solution = solver.Solve(request);

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

                    lastSolved[host] = DateTime.UtcNow;
                    Solved?.Invoke();
                }
            }

            ApplyUserAgent(request);

            if (destination != null)
                destination.SetLength(0);

            var retry = Run(request, destination);

            if (IsChallenge(retry))
                throw new IOException("Cloudflare challenge at " + request.Url + " was not cleared by the solver (cookies were rejected).");

            return retry;
        }

        private HttpResult Run(HttpRequest request, Stream destination) => destination == null ? inner.Send(request) : inner.Download(request, destination);

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
