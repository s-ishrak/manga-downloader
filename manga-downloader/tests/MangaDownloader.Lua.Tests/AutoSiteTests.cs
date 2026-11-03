using System;
using System.IO;
using System.Linq;
using MangaDownloader.Core;
using MangaDownloader.Lua.Runtime;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class AutoSiteTests
    {
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
