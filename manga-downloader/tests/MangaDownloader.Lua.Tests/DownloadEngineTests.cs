using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using MangaDownloader.Core;
using MangaDownloader.Core.Download;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class DownloadEngineTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));
        private readonly FixtureHttp http = new FixtureHttp();

        public DownloadEngineTests()
        {
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        private static byte[] Jpeg(int seed) => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, (byte)seed, 1, 2, 3 };

        private static byte[] Png(int seed) => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, (byte)seed };

        /// <summary>Writes the module body into a fresh module directory and returns an engine over it.</summary>
        private DownloadEngine Engine(string luaBody, DownloadOptions options, Action<string> log = null)
        {
            var modules = Path.Combine(root, "modules");
            Directory.CreateDirectory(modules);
            File.WriteAllText(Path.Combine(modules, "t.lua"), "function Register() module.Name='T' module.Domains.Add('t.test') end\n" + luaBody);
            options.OutputDirectory = Path.Combine(root, "out");
            return new DownloadEngine(ModuleRepository.LoadDirectory(modules, http), http, options, log);
        }

        private void Page(string url, int seed) => http.Files[url] = Jpeg(seed);

        private const string ThreePageGallery =
            "function GetInfo() info.Title = 'My: Doujin' info.Artist = 'Alice' info.Tags = 'a, b' end\n" +
            "function GetPages() pages.Add('https://img.test/1.jpg') pages.Add('https://img.test/2.jpg') pages.Add('https://img.test/3.jpg') end\n";

        private void ThreePages()
        {
            http.Pages["https://t.test/g/1"] = "<html></html>";
            for (var i = 1; i <= 3; i++)
                Page("https://img.test/" + i + ".jpg", i);
        }

        [Fact]
        public void DownloadsAGalleryIntoACbzWithComicInfo()
        {
            ThreePages();
            var report = Engine(ThreePageGallery, new DownloadOptions()).Download("https://t.test/g/1").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            Assert.Equal(3, report.PagesDownloaded);

            var cbz = Path.Combine(root, "out", "My Doujin.cbz"); // ':' removed from the folder name
            Assert.Equal(new[] { cbz }, report.Outputs.ToArray());
            Assert.False(Directory.Exists(Path.Combine(root, "out", "My Doujin")), "image folder is removed after packing");

            using (var zip = ZipFile.OpenRead(cbz))
            {
                Assert.Equal(
                    new[] { "ComicInfo.xml", "My Doujin_001.jpg", "My Doujin_002.jpg", "My Doujin_003.jpg" },
                    zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

                using (var reader = new StreamReader(zip.GetEntry("ComicInfo.xml").Open()))
                {
                    var xml = reader.ReadToEnd();
                    Assert.Contains("<Title>My: Doujin</Title>", xml);
                    Assert.Contains("<Penciller>Alice</Penciller>", xml);
                    Assert.Contains("<Tags>a, b</Tags>", xml);
                    Assert.Contains("<PageCount>3</PageCount>", xml);
                }
            }
        }

        [Fact]
        public void FolderFormatKeepsImagesAndUsesCustomPageNames()
        {
            ThreePages();
            var options = new DownloadOptions { SaveFormat = SaveFormat.Folder, PageNameFormat = "%ARTIST% - %PAGE(000)%" };
            var report = Engine(ThreePageGallery, options).Download("https://t.test/g/1").Single();

            Assert.True(report.Success);
            var files = Directory.GetFiles(Path.Combine(root, "out", "My Doujin")).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Alice - 001.jpg", "Alice - 002.jpg", "Alice - 003.jpg" }, files);
        }

        [Fact]
        public void RealFileTypeWinsOverTheUrlExtension()
        {
            ThreePages();
            http.Files["https://img.test/2.jpg"] = Png(2);
            var report = Engine(ThreePageGallery, new DownloadOptions { SaveFormat = SaveFormat.Folder }).Download("https://t.test/g/1").Single();

            Assert.True(report.Success);
            Assert.True(File.Exists(Path.Combine(root, "out", "My Doujin", "My Doujin_002.png")));
        }

        [Fact]
        public void RetriesTransientFailures()
        {
            ThreePages();
            http.FailuresBeforeSuccess = url => url.EndsWith("2.jpg") ? 2 : 0;
            var options = new DownloadOptions { RetryDelayMilliseconds = 1, RetryFailed = 4 };

            var report = Engine(ThreePageGallery, options).Download("https://t.test/g/1").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            Assert.Equal(3, http.AttemptsFor("https://img.test/2.jpg"));
        }

        [Fact]
        public void FailedPagesBlockPackingAndTheNextRunResumes()
        {
            ThreePages();
            http.Files.Remove("https://img.test/2.jpg"); // 404
            var options = new DownloadOptions { RetryDelayMilliseconds = 1 };

            var first = Engine(ThreePageGallery, options).Download("https://t.test/g/1").Single();

            Assert.False(first.Success);
            Assert.Contains("page 2", first.Errors.Single());
            Assert.Equal(1, http.AttemptsFor("https://img.test/2.jpg")); // 404 is not retried
            Assert.False(File.Exists(Path.Combine(root, "out", "My Doujin.cbz")));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "out", "My Doujin")).Length);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "out", "My Doujin"), "*.part"));

            Page("https://img.test/2.jpg", 2);
            var second = Engine(ThreePageGallery, options).Download("https://t.test/g/1").Single();

            Assert.True(second.Success, string.Join("; ", second.Errors));
            Assert.Equal(1, second.PagesDownloaded);
            Assert.Equal(2, second.PagesSkipped);
            Assert.Equal(1, http.AttemptsFor("https://img.test/1.jpg")); // not fetched again
            Assert.True(File.Exists(Path.Combine(root, "out", "My Doujin.cbz")));
        }

        [Fact]
        public void ExistingArchiveIsSkippedUnlessOverwriting()
        {
            ThreePages();
            Engine(ThreePageGallery, new DownloadOptions()).Download("https://t.test/g/1");
            var before = http.Requests.Count;

            var again = Engine(ThreePageGallery, new DownloadOptions()).Download("https://t.test/g/1").Single();

            Assert.True(again.Success);
            Assert.Equal(0, again.PagesTotal);
            Assert.Equal(before + 1, http.Requests.Count); // only the info page was fetched
        }

        [Fact]
        public void BackupUrlsAreUsedWhenThePrimaryFails()
        {
            http.Pages["https://t.test/g/1"] = "<html></html>";
            Page("https://backup.test/1.jpg", 1);

            var lua = "function GetInfo() info.Title = 'B' end\n" +
                      "function GetPages() local p = PageInfo.New('https://dead.test/1.jpg') p.BackupUrls.Add('https://backup.test/1.jpg') pages.Add(p) end\n";
            var report = Engine(lua, new DownloadOptions { SaveFormat = SaveFormat.Folder, RetryDelayMilliseconds = 1 }).Download("https://t.test/g/1").Single();

            Assert.True(report.Success);
            Assert.True(File.Exists(Path.Combine(root, "out", "B", "B_001.jpg")));
        }

        [Fact]
        public void ReaderPagesAreResolvedToTheirImage()
        {
            http.Pages["https://t.test/g/1"] = "<html></html>";
            http.Files["https://t.test/read/1"] = Encoding.UTF8.GetBytes("<html><body><img id=\"image\" src=\"/media/1.jpg\"></body></html>");
            Page("https://t.test/media/1.jpg", 1);

            var lua = "function GetInfo() info.Title = 'R' end\nfunction GetPages() pages.Add('https://t.test/read/1') end\n";
            var report = Engine(lua, new DownloadOptions { SaveFormat = SaveFormat.Folder }).Download("https://t.test/g/1").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            Assert.Equal("https://t.test/read/1", http.Requests.Last().Referer);
            Assert.True(File.Exists(Path.Combine(root, "out", "R", "R_001.jpg")));
        }

        [Fact]
        public void BeforeDownloadPageCanRewriteTheUrlAndRefererIsSent()
        {
            http.Pages["https://t.test/g/1"] = "<html></html>";
            Page("https://real.test/1.jpg", 1);

            var lua = "function GetInfo() info.Title = 'H' end\n" +
                      "function GetPages() local p = PageInfo.New('https://placeholder.test/1') p.Referer = 'https://ref.test/' pages.Add(p) pages.Headers['X-Test'] = 'yes' end\n" +
                      "function BeforeDownloadPage() page.Url = 'https://real.test/1.jpg' end\n";
            var report = Engine(lua, new DownloadOptions { SaveFormat = SaveFormat.Folder }).Download("https://t.test/g/1").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            var request = http.Requests.Single(r => r.Url == "https://real.test/1.jpg");
            Assert.Equal("https://ref.test/", request.Referer);
            Assert.Equal("yes", request.Headers["X-Test"]);
        }

        [Fact]
        public void ChaptersGetTheirOwnArchivesNamedByTheChapterFormat()
        {
            http.Pages["https://t.test/s"] = "<html></html>";
            http.Pages["https://t.test/c/1"] = "<html></html>";
            http.Pages["https://t.test/c/2"] = "<html></html>";
            Page("https://img.test/a.jpg", 1);
            Page("https://img.test/b.jpg", 2);

            var lua = "function GetInfo() info.Title = 'Series' end\n" +
                      "function GetChapters() chapters.Add('/c/1', 'One') chapters.Add('/c/2', 'Two') end\n" +
                      "function GetPages() if url:contains('/c/1') then pages.Add('https://img.test/a.jpg') else pages.Add('https://img.test/b.jpg') end end\n";
            var report = Engine(lua, new DownloadOptions()).Download("https://t.test/s").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            Assert.Equal(
                new[] { "1 - One.cbz", "2 - Two.cbz" },
                Directory.GetFiles(Path.Combine(root, "out", "Series")).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void CombinedCbzIncludesChapterBookmarksAtTheirFirstPages()
        {
            http.Pages["https://t.test/s"] = "<html></html>";
            http.Pages["https://t.test/c/1"] = "<html></html>";
            http.Pages["https://t.test/c/2"] = "<html></html>";
            Page("https://img.test/a.jpg", 1);
            Page("https://img.test/b.jpg", 2);
            Page("https://img.test/c.jpg", 3);

            var lua = "function GetInfo() info.Title = 'Series' end\n" +
                      "function GetChapters() chapters.Add('/c/1', 'One') chapters.Add('/c/2', 'Two') end\n" +
                      "function GetPages() if url:contains('/c/1') then pages.Add('https://img.test/a.jpg') else pages.Add('https://img.test/b.jpg') pages.Add('https://img.test/c.jpg') end end\n";
            var report = Engine(lua, new DownloadOptions { CombineChapters = true }).Download("https://t.test/s").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            var cbz = Path.Combine(root, "out", "Series.cbz");

            using (var zip = ZipFile.OpenRead(cbz))
            {
                Assert.Equal(
                    // Page names use series metadata; chapter titles name the enclosing folders.
                    new[] { "1 - One/Series_001.jpg", "2 - Two/Series_001.jpg", "2 - Two/Series_002.jpg", "ComicInfo.xml" },
                    zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

                using (var stream = zip.GetEntry("ComicInfo.xml").Open())
                {
                    var pages = XDocument.Load(stream).Root.Element("Pages").Elements("Page").ToList();
                    Assert.Equal(new[] { "0", "1" }, pages.Select(p => (string)p.Attribute("Image")).ToArray());
                    Assert.Equal(new[] { "1 - One", "2 - Two" }, pages.Select(p => (string)p.Attribute("Bookmark")).ToArray());
                }
            }
        }

        [Fact]
        public void StaleCbzTemporaryFileDoesNotBlockARetry()
        {
            ThreePages();
            var output = Path.Combine(root, "out");
            Directory.CreateDirectory(output);
            var temp = Path.Combine(output, "My Doujin.cbz.tmp");
            File.WriteAllText(temp, "left by an interrupted run");

            var report = Engine(ThreePageGallery, new DownloadOptions()).Download("https://t.test/g/1").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            Assert.True(File.Exists(Path.Combine(output, "My Doujin.cbz")));
            Assert.False(File.Exists(temp));
        }

        [Fact]
        public void ListPagesFollowQueuedGalleries()
        {
            http.Pages["https://t.test/list"] = "<html></html>";
            http.Pages["https://t.test/g/1"] = "<html></html>";
            http.Pages["https://t.test/g/2"] = "<html></html>";
            Page("https://img.test/1.jpg", 1);

            var lua = "function GetInfo()\n if url:contains('/list') then Enqueue('/g/1') Enqueue('/g/2') info.Ignore = true else info.Title = 'G' .. url:after('/g/') end end\n" +
                      "function GetPages() pages.Add('https://img.test/1.jpg') end\n";
            var reports = Engine(lua, new DownloadOptions { SaveFormat = SaveFormat.Folder }).Download("https://t.test/list");

            Assert.Equal(new[] { "G1", "G2" }, reports.Select(r => r.Title).ToArray());
            Assert.All(reports, r => Assert.True(r.Success));
        }

        [Fact]
        public void DownloadsRunInParallelUpToTheLimit()
        {
            http.Pages["https://t.test/g/1"] = "<html></html>";
            var pagesLua = string.Join(" ", Enumerable.Range(1, 12).Select(i => "pages.Add('https://img.test/" + i + ".jpg')"));
            for (var i = 1; i <= 12; i++)
                Page("https://img.test/" + i + ".jpg", i);
            http.DownloadDelayMilliseconds = 40;

            var lua = "function GetInfo() info.Title = 'P' end\nfunction GetPages() " + pagesLua + " end\n";
            var report = Engine(lua, new DownloadOptions { SaveFormat = SaveFormat.Folder, MaxParallel = 4 }).Download("https://t.test/g/1").Single();

            Assert.True(report.Success);
            Assert.InRange(http.MaxConcurrentDownloads, 2, 4);
        }

        [Fact]
        public void RateLimitsFromTheModuleSpaceOutRequests()
        {
            http.Pages["https://t.test/g/1"] = "<html></html>";
            for (var i = 1; i <= 3; i++)
                Page("https://img.test/" + i + ".jpg", i);

            // Limits are declared in Register(); write a module that does so.
            var modules = Path.Combine(root, "modules");
            Directory.CreateDirectory(modules);
            File.WriteAllText(Path.Combine(modules, "t.lua"),
                "function Register() module.Name='T' module.Domains.Add('t.test') module.RateLimits.Add('img.test', 1, 150) end\n" +
                "function GetInfo() info.Title = 'RL' end\n" +
                "function GetPages() pages.Add('https://img.test/1.jpg') pages.Add('https://img.test/2.jpg') pages.Add('https://img.test/3.jpg') end\n");
            var options = new DownloadOptions { SaveFormat = SaveFormat.Folder, OutputDirectory = Path.Combine(root, "out") };
            var engine = new DownloadEngine(ModuleRepository.LoadDirectory(modules, http), http, options);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var report = engine.Download("https://t.test/g/1").Single();
            watch.Stop();

            Assert.True(report.Success);
            Assert.True(watch.ElapsedMilliseconds >= 280, "3 requests at 1 per 150ms need >= 300ms, took " + watch.ElapsedMilliseconds);
        }

        [Fact]
        public void PageNamesStayUniqueWhenTheFormatHasNoPageToken()
        {
            ThreePages();
            var options = new DownloadOptions { SaveFormat = SaveFormat.Folder, PageNameFormat = "%TITLE%" };
            Engine(ThreePageGallery, options).Download("https://t.test/g/1");

            Assert.Equal(3, Directory.GetFiles(Path.Combine(root, "out", "My Doujin")).Length);
        }

        [Fact]
        public void ModulesThatFailSurfaceAsReportErrors()
        {
            http.Pages["https://t.test/g/1"] = "<html></html>";
            var report = Engine("function GetInfo() Fail(Error.LoginRequired) end\n", new DownloadOptions()).Download("https://t.test/g/1").Single();

            Assert.False(report.Success);
            Assert.StartsWith("LoginRequired:", report.Errors.Single());
            Assert.Contains("credentials set t.test", report.Errors.Single());
        }
    }
}
