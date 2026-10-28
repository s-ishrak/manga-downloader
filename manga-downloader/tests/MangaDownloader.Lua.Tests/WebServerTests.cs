using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MangaDownloader.Core;
using MangaDownloader.Core.Download;
using MangaDownloader.Core.Library;
using MangaDownloader.Web;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class WebServerTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));
        private readonly FixtureHttp fixture = new FixtureHttp();
        private readonly LibraryDb db = new LibraryDb(":memory:");
        private readonly QueueRunner runner;
        private readonly DownloadOptions download;
        private readonly LocalWebServer server;
        private readonly HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        private readonly int port = CloudflareTests.FreePort();

        public WebServerTests()
        {
            Directory.CreateDirectory(root);
            var modules = Path.Combine(root, "modules");
            Directory.CreateDirectory(modules);
            File.WriteAllText(Path.Combine(modules, "t.lua"),
                "function Register() module.Name='T' module.Domains.Add('t.test') end\n" +
                "function GetInfo() info.Title = 'Series' end\n" +
                "function GetChapters() for c in dom.SelectValues('//a/@href') do chapters.Add(c, c) end end\n" +
                "function GetPages() pages.Add('https://img.test/1.jpg') end\n");
            fixture.Pages["https://t.test/s"] = "<a href='/c/1'>1</a><a href='/c/2'>2</a>";
            fixture.Files["https://img.test/1.jpg"] = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1 };

            var repository = ModuleRepository.LoadDirectory(modules, fixture);
            download = new DownloadOptions { OutputDirectory = Path.Combine(root, "out"), SaveFormat = SaveFormat.Folder };
            runner = new QueueRunner(db, () => new DownloadEngine(repository, fixture, download));
            server = new LocalWebServer(db, runner, new SeriesTracker(db, repository), port, token: "test-token", download: download);
            server.Start();
        }

        public void Dispose()
        {
            server.Dispose();
            runner.Dispose();
            db.Dispose();
            client.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        private HttpRequestMessage Request(HttpMethod method, string path, string body = null, bool auth = true, string origin = null, string contentType = "application/json", string host = null)
        {
            var r = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");

            if (auth)
                r.Headers.Add("Cookie", "md_token=test-token");

            if (origin != null)
                r.Headers.Add("Origin", origin);

            if (host != null)
                r.Headers.Host = host;

            if (body != null)
                r.Content = new StringContent(body, Encoding.UTF8, contentType);

            return r;
        }

        private (HttpStatusCode Status, JsonElement Json) Call(HttpRequestMessage request)
        {
            using (var response = client.SendAsync(request).GetAwaiter().GetResult())
            {
                var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return (response.StatusCode, text.StartsWith("{") || text.StartsWith("[") ? JsonDocument.Parse(text).RootElement.Clone() : default(JsonElement));
            }
        }

        private JsonElement Ok(HttpRequestMessage request)
        {
            var (status, json) = Call(request);
            Assert.Equal(HttpStatusCode.OK, status);
            return json;
        }

        [Fact]
        public void RequiresTheTokenAndHandsItToABrowserAsACookie()
        {
            Assert.Equal(HttpStatusCode.Unauthorized, Call(Request(HttpMethod.Get, "/", auth: false)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, Call(Request(HttpMethod.Get, "/api/state", auth: false)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, Call(Request(HttpMethod.Get, "/?token=wrong", auth: false)).Status);

            using (var response = client.SendAsync(Request(HttpMethod.Get, "/?token=test-token", auth: false)).GetAwaiter().GetResult())
            {
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                var cookie = string.Join(";", response.Headers.GetValues("Set-Cookie"));
                Assert.Contains("md_token=test-token", cookie);
                Assert.Contains("HttpOnly", cookie);
                Assert.Contains("SameSite=Strict", cookie);
            }

            using (var page = client.SendAsync(Request(HttpMethod.Get, "/")).GetAwaiter().GetResult())
            {
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Contains("<title>manga-downloader</title>", page.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Contains("frame-ancestors 'none'", string.Join(";", page.Headers.GetValues("Content-Security-Policy")));
            }

            var viaHeader = Request(HttpMethod.Get, "/api/state", auth: false);
            viaHeader.Headers.Add("X-Token", "test-token");
            Assert.Equal(HttpStatusCode.OK, Call(viaHeader).Status);
        }

        [Fact]
        public void RejectsForeignHostsOriginsAndNonJsonPosts()
        {
            // DNS rebinding: the browser sends the attacker's hostname.
            Assert.Equal(HttpStatusCode.Forbidden, Call(Request(HttpMethod.Get, "/api/state", host: "evil.test")).Status);

            // Cross-site request from another page.
            Assert.Equal(HttpStatusCode.Forbidden, Call(Request(HttpMethod.Post, "/api/queue", "{\"urls\":\"https://t.test/s\"}", origin: "http://evil.test")).Status);

            // A plain HTML form cannot send JSON.
            Assert.Equal(HttpStatusCode.Forbidden, Call(Request(HttpMethod.Post, "/api/queue", "urls=https://t.test/s", contentType: "application/x-www-form-urlencoded")).Status);

            // No token.
            Assert.Equal(HttpStatusCode.Unauthorized, Call(Request(HttpMethod.Post, "/api/queue", "{\"urls\":\"https://t.test/s\"}", auth: false)).Status);

            Assert.Empty(db.ListQueue());

            // Same origin with the token works.
            Ok(Request(HttpMethod.Post, "/api/queue", "{\"urls\":\"https://t.test/s\"}", origin: $"http://127.0.0.1:{port}"));
            Assert.Single(db.ListQueue());
        }

        [Fact]
        public void QueuesUrlsValidatesThemAndReportsState()
        {
            var added = Ok(Request(HttpMethod.Post, "/api/queue", "{\"urls\":\"https://t.test/g/1\\nhttps://t.test/g/2\"}"));
            Assert.Equal(2, added.GetProperty("added").GetInt32());

            var (status, error) = Call(Request(HttpMethod.Post, "/api/queue", "{\"urls\":\"javascript:alert(1)\"}"));
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("http(s)", error.GetProperty("error").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, Call(Request(HttpMethod.Post, "/api/queue", "{\"urls\":\"   \"}")).Status);

            var state = Ok(Request(HttpMethod.Get, "/api/state"));
            Assert.Equal(2, state.GetProperty("queue").GetArrayLength());
            Assert.Equal("Queued", state.GetProperty("queue")[0].GetProperty("status").GetString());
            Assert.False(state.GetProperty("runner").GetProperty("paused").GetBoolean());

            var id = state.GetProperty("queue")[0].GetProperty("id").GetInt64();
            Ok(Request(HttpMethod.Post, $"/api/queue/{id}/cancel", "{}"));
            Assert.Equal(QueueStatus.Stopped, db.Get(id).Status);

            Ok(Request(HttpMethod.Post, $"/api/queue/{id}/retry", "{}"));
            Assert.Equal(QueueStatus.Queued, db.Get(id).Status);

            Ok(Request(HttpMethod.Post, $"/api/queue/{id}/remove", "{}"));
            Assert.Null(db.Get(id));
            Assert.Equal(HttpStatusCode.NotFound, Call(Request(HttpMethod.Post, "/api/queue/999/retry", "{}")).Status);
        }

        [Fact]
        public void OutputFolderCanBeChangedAndDownloadsUseIt()
        {
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "out")), Ok(Request(HttpMethod.Get, "/api/state")).GetProperty("outputDirectory").GetString());

            var other = Path.Combine(root, "elsewhere", "manga");
            var set = Ok(Request(HttpMethod.Post, "/api/settings/output", JsonSerializer.Serialize(new { path = other })));
            Assert.Equal(other, set.GetProperty("outputDirectory").GetString());
            Assert.True(Directory.Exists(other));
            Assert.Equal(other, download.OutputDirectory);

            Ok(Request(HttpMethod.Post, "/api/queue", "{\"urls\":\"https://t.test/s\"}"));
            runner.Start();
            Assert.True(System.Threading.SpinWait.SpinUntil(() => db.ListQueue().All(q => q.Status == QueueStatus.Completed), 8000));
            Assert.StartsWith(other, db.ListQueue().Single().Outputs.First());

            Assert.Equal(HttpStatusCode.BadRequest, Call(Request(HttpMethod.Post, "/api/settings/output", "{\"path\":\"  \"}")).Status);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, Call(Request(HttpMethod.Post, "/api/settings/output", "{\"path\":\"bad\\u0000name\"}")).Status);
            Assert.Equal(other, download.OutputDirectory);
        }

        [Fact]
        public void ConcurrencyCanBeChangedAndIsValidated()
        {
            Assert.Equal(1, Ok(Request(HttpMethod.Get, "/api/state")).GetProperty("runner").GetProperty("concurrency").GetInt32());
            Assert.Equal(3, Ok(Request(HttpMethod.Post, "/api/runner/concurrency", "{\"count\":3}")).GetProperty("concurrency").GetInt32());
            Assert.Equal(3, runner.MaxConcurrent);

            foreach (var bad in new[] { "{\"count\":0}", "{\"count\":99}", "{\"count\":\"2\"}", "{\"count\":1.5}", "{}" })
                Assert.Equal(HttpStatusCode.BadRequest, Call(Request(HttpMethod.Post, "/api/runner/concurrency", bad)).Status);

            Assert.Equal(3, runner.MaxConcurrent);
        }

        [Fact]
        public void PauseAndResumeReachTheRunner()
        {
            Assert.True(Ok(Request(HttpMethod.Post, "/api/runner/pause", "{}")).GetProperty("paused").GetBoolean());
            Assert.True(runner.IsPaused);
            Assert.False(Ok(Request(HttpMethod.Post, "/api/runner/resume", "{}")).GetProperty("paused").GetBoolean());
        }

        [Fact]
        public void TracksSeriesAndChecksForNewChapters()
        {
            Ok(Request(HttpMethod.Post, "/api/track", "{\"url\":\"https://t.test/s\"}"));
            var state = Ok(Request(HttpMethod.Get, "/api/state"));
            var tracked = state.GetProperty("tracked")[0];
            Assert.Equal("Series", tracked.GetProperty("title").GetString());
            Assert.Equal(2, tracked.GetProperty("chapters").GetInt32());

            fixture.Pages["https://t.test/s"] = "<a href='/c/1'>1</a><a href='/c/2'>2</a><a href='/c/3'>3</a>";
            var id = tracked.GetProperty("id").GetInt64();
            Ok(Request(HttpMethod.Post, $"/api/track/{id}/check", "{}"));

            Assert.True(System.Threading.SpinWait.SpinUntil(() => db.GetTracked(id).NewCount == 1, 5000), "check should find the new chapter");

            Ok(Request(HttpMethod.Post, $"/api/track/{id}/auto", "{\"enabled\":true}"));
            Assert.True(db.GetTracked(id).AutoDownload);

            var (status, error) = Call(Request(HttpMethod.Post, "/api/track", "{\"url\":\"https://t.test/none\"}"));
            Assert.NotEqual(HttpStatusCode.OK, status);
            Assert.NotNull(error.GetProperty("error").GetString());

            Ok(Request(HttpMethod.Post, $"/api/track/{id}/remove", "{}"));
            Assert.Empty(db.ListTracked());
        }

        [Fact]
        public void ImportsAQueueManFileFromDisk()
        {
            var file = Path.Combine(root, "queue.manga");
            File.WriteAllText(file, "[HDoujin Download List, V2.1]\r\nTitle\thttps://t.test/g/9\t0/5\tQueued\r\n");

            var result = Ok(Request(HttpMethod.Post, "/api/import", JsonSerializer.Serialize(new { path = file })));

            Assert.Equal(1, result.GetProperty("added").GetInt32());
            Assert.Equal("Title", db.ListQueue().Single().Title);

            Assert.Equal(HttpStatusCode.NotFound, Call(Request(HttpMethod.Post, "/api/import", "{\"path\":\"C:\\\\nope\\\\queue.manga\"}")).Status);

            var notAQueue = Path.Combine(root, "other.txt");
            File.WriteAllText(notAQueue, "hello");
            Assert.Equal((HttpStatusCode)422, Call(Request(HttpMethod.Post, "/api/import", JsonSerializer.Serialize(new { path = notAQueue }))).Status);
        }

        [Fact]
        public void ACloudflareQuestionAppearsInTheStateAndIsAnsweredThroughTheApi()
        {
            string answer = null;
            var asking = System.Threading.Tasks.Task.Run(() => answer = server.Ask("https://site.test/g/1", "site.test"));

            Assert.True(System.Threading.SpinWait.SpinUntil(() => !Ok(Request(HttpMethod.Get, "/api/state")).GetProperty("verification").ValueKind.Equals(JsonValueKind.Null), 5000));
            var state = Ok(Request(HttpMethod.Get, "/api/state"));
            Assert.Equal("site.test", state.GetProperty("verification").GetProperty("host").GetString());
            Assert.Equal("https://site.test/g/1", state.GetProperty("verification").GetProperty("url").GetString());
            Assert.True(state.GetProperty("verificationSteps").GetArrayLength() >= 3);

            Assert.True(Ok(Request(HttpMethod.Post, "/api/verify", "{\"text\":\"curl 'https://site.test/' -H 'cookie: cf_clearance=Z'\"}")).GetProperty("ok").GetBoolean());
            Assert.True(asking.Wait(5000));
            Assert.Contains("cf_clearance=Z", answer);
            Assert.Equal(JsonValueKind.Null, Ok(Request(HttpMethod.Get, "/api/state")).GetProperty("verification").ValueKind);
        }

        [Fact]
        public void CancellingTheCloudflareQuestionReturnsNull()
        {
            string answer = "not set";
            var asking = System.Threading.Tasks.Task.Run(() => answer = server.Ask("https://site.test/", "site.test"));
            Assert.True(System.Threading.SpinWait.SpinUntil(() => Ok(Request(HttpMethod.Get, "/api/state")).GetProperty("verification").ValueKind != JsonValueKind.Null, 5000));

            Ok(Request(HttpMethod.Post, "/api/verify/cancel", "{}"));

            Assert.True(asking.Wait(5000));
            Assert.Null(answer);
        }

        [Fact]
        public void AnswersWithoutAQuestionAreIgnoredAndNeedTheSameProtections()
        {
            Assert.False(Ok(Request(HttpMethod.Post, "/api/verify", "{\"text\":\"x\"}")).GetProperty("ok").GetBoolean());
            Assert.Equal(HttpStatusCode.Unauthorized, Call(Request(HttpMethod.Post, "/api/verify", "{\"text\":\"x\"}", auth: false)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, Call(Request(HttpMethod.Post, "/api/verify", "{\"text\":\"x\"}", origin: "http://evil.test")).Status);
        }

        [Fact]
        public void QueuedItemsAreDownloadedWhenTheRunnerIsStarted()
        {
            runner.Start();
            Ok(Request(HttpMethod.Post, "/api/queue", "{\"urls\":\"https://t.test/s\"}"));

            Assert.True(System.Threading.SpinWait.SpinUntil(() => db.ListQueue().Single().Status == QueueStatus.Completed, 8000));
            var state = Ok(Request(HttpMethod.Get, "/api/state"));
            Assert.Equal(1, state.GetProperty("history").GetArrayLength());
        }
    }
}
