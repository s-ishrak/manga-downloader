using System;
using System.IO;
using System.Linq;
using MangaDownloader.Core;
using MangaDownloader.Lua.Runtime;
using MangaDownloader.Net;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class KaganeTests
    {
        [Fact]
        public void SeriesChaptersAndReaderUseKaganeApi()
        {
            const string series = "01a0e2ac-6475-7ab5-bcd7-6dd2c6aaa124";
            const string book = "01a1556c-60cb-7b5d-a6ef-c00761a5042a";
            string root = "https://kagane.moe/series/name-" + series;
            string reader = root + "/reader/chapter-" + book;
            var http = new ScriptedHttp(request =>
            {
                string body;
                if (request.Url.EndsWith("/series/" + series)) body = "{\"title\":\"Example\",\"translated_language\":\"en\"}";
                else if (request.Url.EndsWith("/books")) body = "{\"items\":[{\"book_id\":\"" + book + "\",\"title\":\"Chapter 1\",\"chapter_no\":\"1\"}],\"next_cursor\":\"next+page\"}";
                else if (request.Url.EndsWith("?before=next%2Bpage")) body = "{\"items\":[],\"next_cursor\":null}";
                else if (request.Url.EndsWith("/api/integrity")) { Assert.Equal("POST", request.Method); body = "{\"token\":\"integrity\"}"; }
                else if (request.Url.EndsWith("?intent=Read")) { Assert.Equal("integrity", request.Headers["x-integrity-token"]); body = "{\"cache_url\":\"https://cache.example\",\"access_token\":\"access\",\"manifest\":{\"pages\":[{\"page_id\":\"p1\",\"ext\":\"webp\"},{\"page_id\":\"p2\",\"ext\":\"jxl\"}]}}"; }
                else return new HttpResult { StatusCode = 404, Body = "" };
                return new HttpResult { StatusCode = 200, Body = body };
            });
            var dir = Path.Combine(Path.GetTempPath(), "kagane-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var match = ModuleRepository.LoadDirectory(dir, http).Find(root);
                Assert.NotNull(match);
                var session = new ModuleSession(match.File);
                Assert.Equal("Example", session.GetInfo(root).Title);
                var chapter = Assert.Single(session.GetChapters(root));
                Assert.Equal("1", chapter.Chapter);
                Assert.EndsWith("/reader/" + book, chapter.Url);
                Assert.Empty(session.GetChapters(reader));
                var pages = session.GetPages(reader);
                Assert.Equal(2, pages.Count);
                Assert.Equal("https://cache.example/api/v2/books/page/" + book + "/p1.webp?token=access", pages[0].Url);
                Assert.Contains("/page/original/", pages[1].Url);
                Assert.Equal("https://kagane.moe/", pages[0].Referer);
                Assert.Equal("https://kagane.moe", pages[0].Headers["Origin"]);
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
