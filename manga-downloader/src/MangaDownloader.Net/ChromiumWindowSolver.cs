using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MangaDownloader.Net
{
    /// <summary>A separate Brave/Chrome/Edge window with an app-owned profile. The person completes verification; CDP reads the resulting session.</summary>
    public sealed class ChromiumWindowSolver : IChallengeSolver
    {
        private readonly string profileDirectory;
        private readonly int timeoutSeconds;
        private readonly Action<string> log;
        private readonly string browserPath;
        private readonly bool hidden;

        public ChromiumWindowSolver(string profileDirectory, int timeoutSeconds = 300, Action<string> log = null, string browserPath = null, bool hidden = false)
        {
            this.profileDirectory = profileDirectory;
            this.timeoutSeconds = timeoutSeconds;
            this.log = log ?? (_ => { });
            this.browserPath = browserPath;
            this.hidden = hidden;
        }

        public static string FindBrowser()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            return new[] { @"BraveSoftware\Brave-Browser\Application\brave.exe", @"Google\Chrome\Application\chrome.exe", @"Microsoft\Edge\Application\msedge.exe" }
                .SelectMany(relative => roots.Where(root => !string.IsNullOrEmpty(root)).Select(root => Path.Combine(root, relative)))
                .FirstOrDefault(File.Exists);
        }

        public SolverSolution Solve(HttpRequest request) => SolveAsync(request).GetAwaiter().GetResult();

        private async Task<SolverSolution> SolveAsync(HttpRequest request)
        {
            var executable = browserPath ?? FindBrowser();
            if (executable == null || !File.Exists(executable))
                throw new IOException("Brave, Chrome or Edge was not found for the separate verification window.");

            var target = new Uri(request.Method == "POST" ? new Uri(request.Url).GetLeftPart(UriPartial.Authority) + "/" : request.Url);
            if (target.Scheme != "https" && target.Scheme != "http")
                throw new IOException("Browser verification requires an http(s) URL.");

            // Each browser keeps its own profile format and session, separate from the person's normal browser profile.
            var browserProfile = Path.Combine(profileDirectory, Path.GetFileNameWithoutExtension(executable), Uri.EscapeDataString(target.GetLeftPart(UriPartial.Authority)));
            Directory.CreateDirectory(browserProfile);
            var portFile = Path.Combine(browserProfile, "DevToolsActivePort");
            File.Delete(portFile);
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--user-data-dir=" + Path.GetFullPath(browserProfile), "--remote-debugging-port=0",
                "--remote-debugging-address=127.0.0.1", "--no-first-run", "--no-default-browser-check", "--new-window", target.AbsoluteUri })
                info.ArgumentList.Add(argument);
            if (hidden)
                info.ArgumentList.Add("--headless=new"); // Local integration tests only.

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var token = timeout.Token;
            var process = StartBrowser(info);
            var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var transferred = false;
            string endpoint = null;
            try
            {
                log("A separate " + Path.GetFileNameWithoutExtension(executable) + " window opened for " + target.Host + ". Complete verification there; the session is collected automatically.");
                while (endpoint == null)
                {
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                        throw new IOException("The separate verification browser exited before it could start.");
                    if (File.Exists(portFile))
                    {
                        string[] lines;
                        try { lines = File.ReadAllLines(portFile); }
                        catch (IOException) { lines = Array.Empty<string>(); } // Chrome may still be writing the file.
                        if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0 && port <= 65535)
                            endpoint = "http://127.0.0.1:" + port;
                    }
                    if (endpoint == null)
                        await Task.Delay(200, token);
                }

                var throughPolls = 0;
                Uri pageSocket = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                        throw new SolverCancelledException("The verification window for " + target.Host + " was closed.");
                    using var pages = JsonDocument.Parse(await client.GetStringAsync(endpoint + "/json/list", token));
                    var foundPage = false;
                    SolverSolution solution = null;
                    foreach (var page in pages.RootElement.EnumerateArray())
                    {
                        if (page.GetProperty("type").GetString() != "page")
                            continue;
                        foundPage = true;
                        if (!Uri.TryCreate(page.GetProperty("url").GetString(), UriKind.Absolute, out var url) ||
                            !string.Equals(url.Host, target.Host, StringComparison.OrdinalIgnoreCase) ||
                            LooksLikeChallenge(page.GetProperty("title").GetString()))
                            continue;

                        using var session = new CdpSession();
                        pageSocket = new Uri(page.GetProperty("webSocketDebuggerUrl").GetString());
                        await session.Connect(pageSocket, token);
                        var cookies = await session.Call("Network.getCookies", new { urls = new[] { target.AbsoluteUri, url.AbsoluteUri } }, token);
                        var version = await session.Call("Browser.getVersion", new { }, token);
                        solution = ReadSolution(cookies, url.AbsoluteUri, version.GetProperty("userAgent").GetString());
                        break;
                    }
                    if (!foundPage)
                        throw new SolverCancelledException("The verification window for " + target.Host + " was closed.");
                    throughPolls = solution != null && solution.Cookies.Any(c => c.Name == "cf_clearance") ? throughPolls + 1 : 0;
                    if (throughPolls >= 2)
                    {
                        log("Automatically collected " + solution.Cookies.Count + " cookie(s) and the browser user agent for " + target.Host + ".");
                        solution.BrowserSession = new BrowserConnection(process, client, endpoint, pageSocket, target.GetLeftPart(UriPartial.Authority));
                        transferred = true;
                        return solution;
                    }
                    await Task.Delay(1000, token);
                }
            }
            catch (OperationCanceledException) when (!request.CancellationToken.IsCancellationRequested)
            {
                throw new SolverCancelledException("The browser verification for " + target.Host + " timed out after " + timeoutSeconds + " s.");
            }
            catch (Exception e) when (e is HttpRequestException || e is WebSocketException)
            {
                if (process.HasExited)
                    throw new SolverCancelledException("The verification window for " + target.Host + " was closed.");
                throw new IOException("Could not collect the separate browser session: " + e.Message, e);
            }
            finally
            {
                if (!transferred)
                    await CloseBrowser(process, client, endpoint);
            }
        }

        private static async Task CloseBrowser(Process process, HttpClient client, string endpoint)
        {
            // Only this app-owned browser process/profile is closed; the person's normal browser is never attached.
            try
            {
                if (!process.HasExited && endpoint != null)
                {
                    try
                    {
                        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        using var version = JsonDocument.Parse(await client.GetStringAsync(endpoint + "/json/version", closing.Token));
                        using var session = new CdpSession();
                        await session.Connect(new Uri(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()), closing.Token);
                        await session.Call("Browser.close", new { }, closing.Token);
                    }
                    catch (Exception e) when (e is IOException || e is HttpRequestException || e is WebSocketException || e is OperationCanceledException) { }
                    process.WaitForExit(2000);
                }
                if (!process.HasExited)
                {
                    try { process.Kill(true); }
                    catch (InvalidOperationException) { }
                    process.WaitForExit(5000);
                }
            }
            finally
            {
                process.Dispose();
                client.Dispose();
            }
        }

        private sealed class BrowserConnection : IHttpBackend, IDisposable
        {
            private readonly Process process;
            private readonly HttpClient client;
            private readonly string endpoint;
            private readonly Uri pageSocket;
            private readonly string origin;
            private readonly object gate = new object();
            private bool disposed;

            public BrowserConnection(Process process, HttpClient client, string endpoint, Uri pageSocket, string origin)
            {
                this.process = process;
                this.client = client;
                this.endpoint = endpoint;
                this.pageSocket = pageSocket;
                this.origin = origin;
            }

            public HttpResult Send(HttpRequest request) => Execute(request, null);
            public HttpResult Download(HttpRequest request, Stream destination) => Execute(request, destination);

            private HttpResult Execute(HttpRequest request, Stream destination)
            {
                lock (gate)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    if (disposed || process.HasExited)
                        throw new IOException("The verified browser window was closed. Retry to open verification again.");
                    if (!string.Equals(new Uri(request.Url).GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("A verified browser session can only request its own site.");
                    try { return ExecuteAsync(request, destination).GetAwaiter().GetResult(); }
                    catch (WebSocketException e) { throw new IOException("The verified browser connection was lost. Retry to open verification again.", e); }
                }
            }

            private async Task<HttpResult> ExecuteAsync(HttpRequest request, Stream destination)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var token = timeout.Token;
                using var session = new CdpSession();
                await session.Connect(pageSocket, token);
                if (request.Cookies.Count > 0)
                    await session.Call("Network.setCookies", new { cookies = request.Cookies.Select(c => new { name = c.Key, value = c.Value, url = request.Url }).ToArray() }, token);

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in request.Headers)
                    if (!new[] { "User-Agent", "Cookie", "Host", "Content-Length", "Accept-Encoding", "Connection", "Referer" }
                        .Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                        headers[header.Key] = header.Value;
                if (request.Body != null && !headers.ContainsKey("Content-Type"))
                    headers["Content-Type"] = request.ContentType ?? "application/x-www-form-urlencoded";

                // Serialize the request as data, never interpolate a URL or body as JavaScript source.
                var options = JsonSerializer.Serialize(new { url = request.Url, method = request.Method, headers,
                    body = request.Body, referrer = request.Referer, follow = request.AllowAutoRedirect });
                var expression = "(async () => { const r = " + options + "; const o = {method:r.method,headers:r.headers,credentials:'include',redirect:r.follow?'follow':'manual'};" +
                    "if(r.body!==null)o.body=r.body;if(r.referrer)o.referrer=r.referrer;const response=await fetch(r.url,o);" +
                    "if(response.type==='opaqueredirect')throw new Error('The browser cannot expose an unfollowed redirect.');" +
                    "const bytes=new Uint8Array(await response.arrayBuffer());let binary='';for(let i=0;i<bytes.length;i+=32768)binary+=String.fromCharCode(...bytes.subarray(i,i+32768));" +
                    "return {status:response.status,url:response.url,headers:Array.from(response.headers.entries()),data:btoa(binary)}; })()";
                var evaluated = await session.Call("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }, token);
                if (evaluated.TryGetProperty("exceptionDetails", out _))
                    throw new IOException("The verified browser could not read " + request.Url + ". The request may have been blocked by the site's browser security rules.");
                var value = evaluated.GetProperty("result").GetProperty("value");
                var result = new HttpResult { StatusCode = value.GetProperty("status").GetInt32(), Url = value.GetProperty("url").GetString() };
                foreach (var header in value.GetProperty("headers").EnumerateArray())
                    result.Headers[header[0].GetString()] = header[1].GetString();
                result.Headers.TryGetValue("content-type", out var contentType);
                result.ContentType = contentType?.Split(';')[0];
                var data = Convert.FromBase64String(value.GetProperty("data").GetString());
                if (destination == null)
                {
                    var encoding = Encoding.UTF8;
                    if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var media) && !string.IsNullOrEmpty(media.CharSet))
                        encoding = Encoding.GetEncoding(media.CharSet.Trim('"'));
                    result.Body = encoding.GetString(data);
                }
                else if (result.StatusCode >= 200 && result.StatusCode < 300)
                    await destination.WriteAsync(data, 0, data.Length, token);

                // A site may accept ordinary navigation while challenging fetch, even in the verified browser.
                if (destination == null && request.Method == "GET" && request.AllowAutoRedirect && CloudflareBackend.IsChallenge(result))
                    result = await Navigate(session, request.Url, token);

                var cookies = await session.Call("Network.getCookies", new { urls = new[] { request.Url, result.Url } }, token);
                foreach (var cookie in ReadSolution(cookies, result.Url, null).Cookies)
                {
                    result.SetCookies.Add(cookie);
                    result.Cookies[cookie.Name] = cookie.Value;
                }
                return result;
            }

            private static async Task<HttpResult> Navigate(CdpSession session, string url, CancellationToken token)
            {
                var documents = new Dictionary<string, (string RequestId, JsonElement Response)>();
                var finished = new HashSet<string>();
                session.OnEvent = message =>
                {
                    var method = message.GetProperty("method").GetString();
                    var parameters = message.GetProperty("params");
                    if (method == "Network.responseReceived" && parameters.GetProperty("type").GetString() == "Document")
                        documents[parameters.GetProperty("loaderId").GetString()] =
                            (parameters.GetProperty("requestId").GetString(), parameters.GetProperty("response").Clone());
                    else if (method == "Network.loadingFinished")
                        finished.Add(parameters.GetProperty("requestId").GetString());
                };
                await session.Call("Page.enable", new { }, token);
                await session.Call("Network.enable", new { }, token);
                var navigation = await session.Call("Page.navigate", new { url }, token);
                if (navigation.TryGetProperty("errorText", out var error))
                    throw new IOException("The verified browser could not navigate to " + url + ": " + error.GetString());
                if (!navigation.TryGetProperty("loaderId", out var loader))
                    throw new IOException("The verified browser did not start a page navigation to " + url + ".");
                var loaderId = loader.GetString();
                while (true)
                {
                    // A command also drains network events while the document is loading.
                    await session.Call("Runtime.evaluate", new { expression = "document.readyState" }, token);
                    if (documents.TryGetValue(loaderId, out var document) && finished.Contains(document.RequestId))
                    {
                        var body = await session.Call("Network.getResponseBody", new { requestId = document.RequestId }, token);
                        var response = document.Response;
                        var result = new HttpResult
                        {
                            StatusCode = (int)response.GetProperty("status").GetDouble(),
                            Url = response.GetProperty("url").GetString(),
                            ContentType = response.GetProperty("mimeType").GetString(),
                            Body = body.GetProperty("base64Encoded").GetBoolean()
                                ? Encoding.UTF8.GetString(Convert.FromBase64String(body.GetProperty("body").GetString()))
                                : body.GetProperty("body").GetString(),
                        };
                        foreach (var header in response.GetProperty("headers").EnumerateObject())
                            result.Headers[header.Name] = header.Value.ToString();
                        return result;
                    }
                    await Task.Delay(100, token);
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                    CloseBrowser(process, client, endpoint).GetAwaiter().GetResult();
                }
            }
        }

        private static Process StartBrowser(ProcessStartInfo info)
        {
            try { return Process.Start(info); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
            {
                throw new IOException("Could not open the separate verification browser: " + e.Message, e);
            }
        }

        private static bool LooksLikeChallenge(string title) => string.IsNullOrWhiteSpace(title) ||
            new[] { "just a moment", "attention required", "verify you are human", "security check" }
                .Any(text => title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);

        private static SolverSolution ReadSolution(JsonElement result, string url, string userAgent)
        {
            var solution = new SolverSolution { Status = 200, Url = url, UserAgent = userAgent };
            foreach (var cookie in result.GetProperty("cookies").EnumerateArray())
            {
                var entry = new CookieEntry
                {
                    Name = cookie.GetProperty("name").GetString(), Value = cookie.GetProperty("value").GetString(),
                    Domain = cookie.GetProperty("domain").GetString(), Path = cookie.GetProperty("path").GetString(),
                };
                if (cookie.TryGetProperty("expires", out var expires) && expires.GetDouble() > 0)
                    entry.Expires = DateTimeOffset.FromUnixTimeSeconds((long)expires.GetDouble()).UtcDateTime;
                solution.Cookies.Add(entry);
            }
            return solution;
        }

        private sealed class CdpSession : IDisposable
        {
            private readonly ClientWebSocket socket = new ClientWebSocket();
            private int sequence;
            public Action<JsonElement> OnEvent { get; set; }
            public Task Connect(Uri url, CancellationToken token) => socket.ConnectAsync(url, token);

            public async Task<JsonElement> Call(string method, object parameters, CancellationToken token)
            {
                var id = ++sequence;
                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method, @params = parameters }));
                await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, token);
                var buffer = new byte[8192];
                while (true)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult part;
                    do
                    {
                        part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (part.MessageType == WebSocketMessageType.Close)
                            throw new IOException("The verification browser connection closed.");
                        message.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);
                    using var json = JsonDocument.Parse(message.ToArray());
                    var root = json.RootElement;
                    if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                    {
                        if (root.TryGetProperty("method", out _))
                            OnEvent?.Invoke(root);
                        continue;
                    }
                    if (root.TryGetProperty("error", out var error))
                        throw new IOException("Browser session collection failed: " + error.GetProperty("message").GetString());
                    return root.GetProperty("result").Clone();
                }
            }

            public void Dispose() => socket.Dispose();
        }
    }
}
