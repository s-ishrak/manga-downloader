using System;
using System.IO;
using System.Linq;
using MangaDownloader.Core;
using MangaDownloader.Lua.Runtime;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class ILikeComixTests
    {
        [Fact]
        public void SeriesAndChapterReaderAreSupported()
        {
            const string root = "https://ilikecomix.com/manga/c/example/";
            const string chapter = root + "chapter-1/";
            var http = new FixtureHttp();
            http.Pages[root] = "<h1>Example</h1><div class='description-summary'><div class='summary__content'>Description</div></div><ul><li class='wp-manga-chapter'><a href='/manga/c/example/chapter-1/'>1 . Chapter One</a></li></ul>";
            http.Pages[chapter] = "<meta property='og:title' content='Chapter One'><img class='wp-manga-chapter-img' src='https://images.example/1.webp'><img class='wp-manga-chapter-img lazyload' data-src='https://images.example/2.webp' src='placeholder'>";
            var dir = Path.Combine(Path.GetTempPath(), "ilike-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var match = ModuleRepository.LoadDirectory(dir, http).Find(root);
                Assert.NotNull(match);
                var session = new ModuleSession(match.File);
                Assert.Equal("Example", session.GetInfo(root).Title);
                var item = Assert.Single(session.GetChapters(root));
                Assert.Equal(chapter, item.Url);
                Assert.Equal("1", item.Chapter);
                Assert.Empty(session.GetChapters(chapter));
                Assert.Equal(new[] { "https://images.example/1.webp", "https://images.example/2.webp" }, session.GetPages(chapter).Select(p => p.Url));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
