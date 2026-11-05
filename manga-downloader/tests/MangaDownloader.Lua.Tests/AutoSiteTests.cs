using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MangaDownloader.Core.Download;
using MangaDownloader.Core.Library;
using MangaDownloader.Net;
using MangaDownloader.Web;
using MangaDownloader.Core;
using MangaDownloader.Lua.Runtime;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class AutoSiteTests
    {
        [Fact]
        public void ExistingComixLinkDoesNotStartDetectionOrRequestTheSite()
        {
            var dir = Path.Combine(Path.GetTempPath(), "auto-site-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "Comix.lua"), "function Register() module.Name='Comix' module.Domains.Add('comix.to') end");
                var http = new FixtureHttp();
                var repo = ModuleRepository.LoadDirectory(dir, http);
                var error = Assert.Throws<ArgumentException>(() => repo.AddFromLink("https://comix.to/title/55w9k-gibo-san-wa-boku-no-mono"));
                Assert.Contains("Comix is already supported", error.Message);
                Assert.Contains("Add to queue", error.Message);
                Assert.Empty(http.Requests);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task CancellingDetectionStopsItsNetworkRequestWithoutInstallingAnything()
        {
            var dir = Path.Combine(Path.GetTempPath(), "auto-site-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            using var http = new WaitingHttp();
            using var cancellation = new CancellationTokenSource();
            var repo = ModuleRepository.LoadDirectory(dir, http);
            var check = Task.Run(() => repo.AddFromLink("https://unknown.test/chapter-1", cancellation.Token));
            try
            {
                Assert.True(http.Entered.Wait(TimeSpan.FromSeconds(5)));
                cancellation.Cancel();
                Assert.Same(check, await Task.WhenAny(check, Task.Delay(2000)));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await check; });
                Assert.Empty(Directory.GetFiles(dir));
                Assert.Null(repo.Find("https://unknown.test/chapter-1"));
                http.Release.Set();
                Assert.Equal(1, repo.AddFromLink("https://unknown.test/chapter-1").Pages);
                // A successfully installed module must not retain the detection-only deadline or flags.
                Assert.Single(new ModuleSession(repo.Find("https://unknown.test/chapter-1").File).GetPages("https://unknown.test/chapter-1"));
                Assert.False(http.LastRequest.CancellationToken.CanBeCanceled);
                Assert.True(http.LastRequest.AllowInteractiveChallenge);
            }
            finally { http.Release.Set(); await Task.WhenAny(check, Task.Delay(2000)); Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task SlowSiteCheckLeavesStateAndOtherControlsAvailable()
        {
            var dir = Path.Combine(Path.GetTempPath(), "auto-site-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            using var http = new WaitingHttp();
            var repo = ModuleRepository.LoadDirectory(dir, http);
            using var db = new LibraryDb(":memory:");
            using var runner = new QueueRunner(db, () => new DownloadEngine(repo, http, new DownloadOptions()));
            var port = CloudflareTests.FreePort();
            using var server = new LocalWebServer(db, runner, new SeriesTracker(db, repo), port, "test-token", modules: repo);
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add("X-Token", "test-token");
            Task<HttpResponseMessage> Add() => client.PostAsync("/api/sites/add", new StringContent("{\"url\":\"https://unknown.test/chapter-1\"}", Encoding.UTF8, "application/json"));
            var checking = Add();
            try
            {
                Assert.True(http.Entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/state")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/runner/pause", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
                using var busy = await Add();
                Assert.Equal(HttpStatusCode.BadRequest, busy.StatusCode);
                Assert.Contains("already running", await busy.Content.ReadAsStringAsync());
                http.Release.Set();
                using var finished = await checking;
                Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
            }
            finally { http.Release.Set(); await Task.WhenAny(checking, Task.Delay(2000)); Directory.Delete(dir, true); }
        }

        private sealed class WaitingHttp : IHttpBackend, IDisposable
        {
            public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim();
            public ManualResetEventSlim Release { get; } = new ManualResetEventSlim();
            public HttpRequest LastRequest { get; private set; }

            public HttpResult Send(HttpRequest request)
            {
                LastRequest = request;
                Entered.Set();
                WaitHandle.WaitAny(new[] { Release.WaitHandle, request.CancellationToken.WaitHandle });
                request.CancellationToken.ThrowIfCancellationRequested();
                return new HttpResult { Url = request.Url, StatusCode = 200, Body = "<div id='readerarea'><img src='/1.jpg'></div>" };
            }

            public HttpResult Download(HttpRequest request, Stream destination) => throw new NotSupportedException();
            public void Dispose() { Entered.Dispose(); Release.Dispose(); }
        }

        [Fact]
        public void AddsAndReloadsAReaderFromSeriesLink()
        {
            const string series = "https://example.test/manga/test";
            const string chapter = "https://example.test/chapter-1";
            var http = new FixtureHttp();
            http.Pages[series] = "<h1>Test</h1><a href='/chapter-1'>Chapter 1</a>";
            http.Pages[chapter] = "<div id='readerarea'><img data-src='/1.jpg'><img src='/2.jpg'></div>";
            var dir = Path.Combine(Path.GetTempPath(), "auto-site-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var repo = ModuleRepository.LoadDirectory(dir, http);
                var result = repo.AddFromLink(series);
                Assert.Equal(1, result.Chapters);
                Assert.Equal(2, result.Pages);
                Assert.NotNull(repo.Find(series));
                var reloaded = ModuleRepository.LoadDirectory(dir, http);
                var session = new ModuleSession(reloaded.Find(series).File);
                Assert.Equal(chapter, Assert.Single(session.GetChapters(series)).Url);
                Assert.Empty(session.GetChapters(chapter));
                Assert.Equal(new[] { "https://example.test/1.jpg", "https://example.test/2.jpg" }, session.GetPages(chapter).Select(p => p.Url));
                Assert.Throws<ArgumentException>(() => repo.AddFromLink(series));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void DoesNotSaveWhenReaderCannotBeDetected()
        {
            var http = new FixtureHttp();
            http.Pages["https://unknown.test/page"] = "<h1>Test</h1><img src='/logo.png'>";
            var dir = Path.Combine(Path.GetTempPath(), "auto-site-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var repo = ModuleRepository.LoadDirectory(dir, http);
                Assert.Throws<ArgumentException>(() => repo.AddFromLink("https://unknown.test/page"));
                Assert.Empty(Directory.GetFiles(dir));
                Assert.Null(repo.Find("https://unknown.test/page"));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
