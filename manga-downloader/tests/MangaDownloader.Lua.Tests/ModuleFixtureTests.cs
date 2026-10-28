using System.IO;
using System.Linq;
using MangaDownloader.Core;
using MangaDownloader.Lua.Runtime;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    /// <summary>
    /// Runs the real upstream Lua modules against synthetic HTML fixtures shaped like the sites' markup.
    /// They need the upstream modules (see <see cref="TestSupport.ModulesDirectory"/>) and return early without them.
    /// </summary>
    public class ModuleFixtureTests
    {
        private const string HentaiFoxGallery =
            "<html><body><h1>Sample Doujin</h1>" +
            "<span class=\"pages\">Pages: 3</span>" +
            "<ul class=\"parodies\"><li><a href=\"/p/x\">original<span>12</span></a></li></ul>" +
            "<ul class=\"tags\"><li><a href=\"/t/a\">glasses<span>1</span></a></li><li><a href=\"/t/b\">sole female<span>2</span></a></li></ul>" +
            "<ul class=\"artists\"><li><a href=\"/a/x\">some artist<span>5</span></a></li></ul>" +
            "<ul class=\"languages\"><li><a href=\"/l/e\">english<span>9</span></a></li></ul>" +
            "<a class=\"g_button\" href=\"/g/12345/1/\">Read online</a></body></html>";

        private const string HentaiFoxReader =
            "<html><body>" +
            "<input name=\"image_dir\" value=\"012345abcdef\"><input name=\"gallery_id\" value=\"12345\"><input name=\"unique_id\" value=\"200000\">" +
            "<script>var g_th = $.parseJSON('{\"1\":\"j,1280,1810\",\"2\":\"p,1280,1810\",\"3\":\"w,1280,1810\"}');</script>" +
            "</body></html>";

        [Fact]
        public void HentaiFoxGalleryInfoAndPages()
        {
            var dir = TestSupport.ModulesDirectory();
            if (dir == null)
                return;

            var http = new FixtureHttp();
            http.Pages["https://hentaifox.com/gallery/12345/"] = HentaiFoxGallery;
            http.Pages["https://hentaifox.com/g/12345/1/"] = HentaiFoxReader;

            var module = LuaModule.Load(Path.Combine(dir, "HentaiFox.lua"), http);
            var session = new ModuleSession(module);

            var info = session.GetInfo("https://hentaifox.com/gallery/12345/");
            Assert.Equal("Sample Doujin", info.Title);
            Assert.Equal("3", info.Fields["PageCount"]);
            Assert.Equal("glasses, sole female", info.Fields["Tags"]);
            Assert.Equal("some artist", info.Fields["Artist"]);
            Assert.Equal("english", info.Fields["Language"]);
            Assert.False(info.Ignore);

            var pages = session.GetPages("https://hentaifox.com/gallery/12345/");
            Assert.Equal(
                new[]
                {
                    "https://i3.hentaifox.com/012345abcdef/12345/1.jpg",
                    "https://i3.hentaifox.com/012345abcdef/12345/2.png",
                    "https://i3.hentaifox.com/012345abcdef/12345/3.webp",
                },
                pages.Select(p => p.Url).ToArray());
        }

        [Fact]
        public void HentaiFoxListPageEnqueuesGalleries()
        {
            var dir = TestSupport.ModulesDirectory();
            if (dir == null)
                return;

            var http = new FixtureHttp();
            http.Pages["https://hentaifox.com/tag/glasses/"] =
                "<html><h1>Glasses - Page 1</h1><h2 class=\"g_title\"><a href=\"/gallery/1/\">A</a></h2><h2 class=\"g_title\"><a href=\"/gallery/2/\">B</a></h2></html>";

            var info = new ModuleSession(LuaModule.Load(Path.Combine(dir, "HentaiFox.lua"), http)).GetInfo("https://hentaifox.com/tag/glasses/");

            Assert.True(info.Ignore);
            Assert.Equal(new[] { "https://hentaifox.com/gallery/1/", "https://hentaifox.com/gallery/2/" }, info.Enqueued.ToArray());
        }

        [Fact]
        public void NHentaiGalleryInfoAndThumbnailToImageRewriting()
        {
            var dir = TestSupport.ModulesDirectory();
            if (dir == null)
                return;

            const string gallery =
                "<html><body><div id=\"info\"><h1>[Artist] Full Title</h1><h2>Original Title</h2>" +
                "<div class=\"tag-container field-name\">Tags:<span class=\"tags\"><a href=\"/tag/a\"><span class=\"name\">glasses</span></a><a href=\"/tag/b\"><span class=\"name\">stockings</span></a></span></div>" +
                "<div class=\"tag-container field-name\">Artists:<span class=\"tags\"><a href=\"/artist/x\"><span class=\"name\">some artist</span></a></span></div>" +
                "<div class=\"tag-container field-name\">Languages:<span class=\"tags\"><a href=\"/language/e\"><span class=\"name\">english</span></a></span></div>" +
                "</div><div id=\"thumbnail-container\">" +
                "<img class=\"lazyload\" data-src=\"https://t3.nhentai.net/galleries/999/1t.jpg\">" +
                "<img class=\"lazyload\" data-src=\"https://t3.nhentai.net/galleries/999/2t.png\">" +
                "<img class=\"lazyload\" data-src=\"https://t3.nhentai.net/galleries/999/3t.jpg.webp\">" +
                "</div></body></html>";

            var http = new FixtureHttp();
            http.Pages["https://nhentai.net/g/123456/"] = gallery;

            var session = new ModuleSession(LuaModule.Load(Path.Combine(dir, "NHentai.lua"), http));

            var info = session.GetInfo("https://nhentai.net/g/123456/");
            Assert.Equal("[Artist] Full Title", info.Title);
            Assert.Equal("Original Title", info.Fields["OriginalTitle"]);
            Assert.Equal("glasses, stockings", info.Fields["Tags"]);
            Assert.Equal("some artist", info.Fields["Artist"].ToLowerInvariant());

            var pages = session.GetPages("https://nhentai.net/g/123456/");
            Assert.Equal(
                new[]
                {
                    "https://i3.nhentai.net/galleries/999/1.jpg",
                    "https://i3.nhentai.net/galleries/999/2.png",
                    "https://i3.nhentai.net/galleries/999/3.jpg",
                },
                pages.Select(p => p.Url).ToArray());
        }

        [Fact]
        public void EveryUpstreamModuleLoadsExceptKnownGaps()
        {
            var dir = TestSupport.ModulesDirectory();
            if (dir == null)
                return;

            var repository = ModuleRepository.LoadDirectory(dir);

            // Known gaps: files wrapped in DoEncryptedString (key lives in the original exe) and voyce_me.lua,
            // which upstream flags "broken" in metadata.json (syntax error: `chapters..Reverse()`).
            var unexpected = repository.Failures.Where(f => !f.IsEncrypted && f.File != "voyce_me.lua").ToList();

            Assert.True(unexpected.Count == 0, string.Join("\n", unexpected.Select(f => f.File + ": " + f.Error)));
            Assert.True(repository.Files.Count >= 290, "loaded " + repository.Files.Count);
            Assert.NotNull(repository.Find("https://nhentai.net/g/1/"));
            Assert.NotNull(repository.Find("https://hentaifox.com/gallery/1/"));
        }
    }
}
