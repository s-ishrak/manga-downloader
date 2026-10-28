using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MangaDownloader.Core;
using MangaDownloader.Core.Download;
using MangaDownloader.Core.Library;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class LibraryDbTests
    {
        [Fact]
        public void UrlKeysIgnoreSchemeWwwFragmentAndTrailingSlash()
        {
            var key = LibraryDb.UrlKey("https://www.Example.com/g/1/");
            Assert.Equal(key, LibraryDb.UrlKey("http://example.com/g/1#top"));
            Assert.NotEqual(key, LibraryDb.UrlKey("https://example.com/g/2"));
            Assert.NotEqual(LibraryDb.UrlKey("https://example.com/g?id=1"), LibraryDb.UrlKey("https://example.com/g?id=2"));
        }

        [Fact]
        public void QueueKeepsOrderDedupesAndRequeuesFinishedItems()
        {
            using (var db = new LibraryDb(":memory:"))
            {
                var a = db.Enqueue("https://x.test/a", "A");
                var b = db.Enqueue("https://x.test/b", "B");
                var again = db.Enqueue("http://www.x.test/a/", "A again");

                Assert.Equal(a.Id, again.Id); // same gallery
                Assert.Equal(new[] { "A", "B" }, db.ListQueue().Select(q => q.Title).ToArray());
                Assert.Equal(a.Id, db.NextQueued().Id);

                db.SetProgress(a.Id, 9, 9, null, 1000);
                db.SetStatus(a.Id, QueueStatus.Completed, null, new[] { "C:\\out\\A.cbz" });
                Assert.Equal(b.Id, db.NextQueued().Id);
                Assert.Equal(new[] { "C:\\out\\A.cbz" }, db.Get(a.Id).Outputs.ToArray());

                var requeued = db.Enqueue("https://x.test/a");
                Assert.Equal(QueueStatus.Queued, requeued.Status);
                Assert.Equal((0, 0, 0), (requeued.PagesDone, requeued.PagesTotal, requeued.Progress)); // a retry starts from zero
                Assert.Equal(b.Id, db.NextQueued().Id); // goes to the back
                Assert.Equal(2, db.ListQueue().Count);
            }
        }

        [Fact]
        public void InterruptedItemsGoBackToQueuedAndCompletedCanBeCleared()
        {
            using (var db = new LibraryDb(":memory:"))
            {
                var a = db.Enqueue("https://x.test/a");
                var b = db.Enqueue("https://x.test/b");
                db.SetStatus(a.Id, QueueStatus.Downloading);
                db.SetStatus(b.Id, QueueStatus.Completed);

                Assert.Equal(1, db.ResetInterrupted());
                Assert.Equal(QueueStatus.Queued, db.Get(a.Id).Status);
                Assert.Equal(1, db.ClearCompleted());
                Assert.Single(db.ListQueue());
            }
        }

        [Fact]
        public void PersistsAcrossReopeningAndKeepsHistoryAndTracking()
        {
            var file = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"), "library.db");

            using (var db = new LibraryDb(file))
            {
                db.Enqueue("https://x.test/a", "A");
                db.AddHistory("https://x.test/h", "Hist", QueueStatus.Completed, 12, new[] { "o.cbz" }, null);
                db.AddTracked("https://x.test/s", "Series", new[] { "https://x.test/s/1", "https://x.test/s/2" }, autoDownload: true);
            }

            using (var db = new LibraryDb(file))
            {
                Assert.Equal("A", db.ListQueue().Single().Title);
                var h = db.ListHistory().Single();
                Assert.Equal(("Hist", 12), (h.Title, h.Pages));
                var t = db.ListTracked().Single();
                Assert.Equal(2, t.KnownChapters.Count);
                Assert.True(t.AutoDownload);
                Assert.NotNull(t.LastChecked);
            }
        }
    }

    public class MigrationAndProgressTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));

        public MigrationAndProgressTests()
        {
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        [Fact]
        public void UpgradesAVersion1DatabaseKeepingItsRows()
        {
            var file = Path.Combine(root, "v1.db");

            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + file))
            {
                raw.Open();
                var cmd = raw.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);" +
                    "CREATE TABLE queue (id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, url_key TEXT NOT NULL UNIQUE, title TEXT, status TEXT NOT NULL," +
                    " pages_done INTEGER NOT NULL DEFAULT 0, pages_total INTEGER NOT NULL DEFAULT 0, error TEXT, outputs TEXT, added_at TEXT NOT NULL, updated_at TEXT NOT NULL, position INTEGER NOT NULL, source TEXT);" +
                    "CREATE TABLE history (id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, title TEXT, status TEXT NOT NULL, pages INTEGER NOT NULL DEFAULT 0, outputs TEXT, error TEXT, finished_at TEXT NOT NULL);" +
                    "CREATE TABLE tracked (id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, url_key TEXT NOT NULL UNIQUE, title TEXT, chapters TEXT, last_checked TEXT, new_count INTEGER NOT NULL DEFAULT 0, auto_download INTEGER NOT NULL DEFAULT 0, last_error TEXT);" +
                    "INSERT INTO meta VALUES ('schema_version', '1');" +
                    "INSERT INTO queue(url, url_key, title, status, added_at, updated_at, position) VALUES ('https://x.test/old', 'x.test/old', 'Old item', 'Queued', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', 1);";
                cmd.ExecuteNonQuery();
            }

            using (var db = new LibraryDb(file))
            {
                var old = db.ListQueue().Single();
                Assert.Equal("Old item", old.Title);
                Assert.Equal(0, old.Progress);

                db.SetProgress(old.Id, 3, 10, null, 420);
                Assert.Equal(420, db.Get(old.Id).Progress);
            }

            using (var again = new LibraryDb(file)) // opening an upgraded database must not fail or re-run the upgrade
                Assert.Equal("Old item", again.ListQueue().Single().Title);
        }

        [Fact]
        public void ProgressIsChapterAwareAndEndsAtOneThousand()
        {
            var http = new FixtureHttp();
            http.Pages["https://t.test/s"] = "<a class='c' href='/c/1'>1</a><a class='c' href='/c/2'>2</a>";
            http.Pages["https://t.test/c/1"] = "<html/>";
            http.Pages["https://t.test/c/2"] = "<html/>";
            for (var i = 1; i <= 4; i++)
                http.Files["https://img.test/" + i + ".jpg"] = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, (byte)i };

            var modules = Path.Combine(root, "modules");
            Directory.CreateDirectory(modules);
            File.WriteAllText(Path.Combine(modules, "t.lua"),
                "function Register() module.Name='T' module.Domains.Add('t.test') end\n" +
                "function GetInfo() info.Title = 'Two chapters' end\n" +
                "function GetChapters() for c in dom.SelectValues('//a/@href') do chapters.Add(c, c) end end\n" +
                "function GetPages() local o = url:contains('/c/1') and 0 or 2 pages.Add('https://img.test/' .. (o + 1) .. '.jpg') pages.Add('https://img.test/' .. (o + 2) .. '.jpg') end\n");
            var repository = ModuleRepository.LoadDirectory(modules, http);
            var fractions = new List<double>();

            var engine = new DownloadEngine(repository, http, new DownloadOptions { OutputDirectory = Path.Combine(root, "out"), SaveFormat = SaveFormat.Folder, MaxParallel = 1 });
            engine.Progress = p => fractions.Add(p.Fraction);
            var report = engine.Download("https://t.test/s").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            Assert.Equal((2, 2), (report.ChaptersDone, report.ChaptersTotal));
            Assert.Equal(fractions.OrderBy(f => f).ToList(), fractions); // never goes backwards
            Assert.InRange(fractions.First(), 0.0, 0.3);   // first page of the first chapter: nowhere near done
            Assert.Equal(1.0, fractions.Last(), 3);
            Assert.Contains(fractions, f => f > 0.45 && f < 0.55); // halfway: first chapter done

            using (var db = new LibraryDb(":memory:"))
            {
                var item = db.Enqueue("https://t.test/s");
                new QueueRunner(db, () => new DownloadEngine(repository, http, new DownloadOptions { OutputDirectory = Path.Combine(root, "out2"), SaveFormat = SaveFormat.Folder })).RunUntilEmpty(CancellationToken.None);
                Assert.Equal(1000, db.Get(item.Id).Progress);
            }
        }
    }

    public class QueueMangaImporterTests
    {
        private static string Row(params string[] cells) => string.Join("\t", cells);

        /// <summary>A 43-column row with the fields in the order the plan recovered (title ... progress, status); the rest padded.</summary>
        private static string Row43(string title, string url, string progress, string status)
        {
            var cells = Enumerable.Repeat("", 43).ToArray();
            cells[0] = title;
            cells[1] = "Original Title";
            cells[2] = "tag1, tag2";
            cells[3] = "artist";
            cells[6] = url;
            cells[7] = "https://site.test/favicon.ico";
            cells[8] = "site.test";
            cells[10] = "D:\\Manga";
            cells[11] = progress;
            cells[12] = status;
            cells[20] = "True";
            cells[30] = "12345";
            return Row(cells);
        }

        [Fact]
        public void ParsesRowsByContentWhateverTheColumnOrder()
        {
            var text = "[HDoujin Download List, V2.1]\r\n" +
                       Row43("Alpha", "https://site.test/g/1", "0/132", "Queued") + "\r\n" +
                       Row43("Beta", "https://site.test/g/2", "20/20", "Completed") + "\r\n" +
                       Row43("Gamma", "https://site.test/g/3", "5/9", "Paused") + "\r\n" +
                       Row43("Delta", "https://site.test/g/4", "", "Failed") + "\r\n" +
                       "\r\n" +
                       Row("no url here", "", "0/1", "Queued") + "\r\n";
            var skipped = new List<string>();

            var entries = QueueMangaImporter.Parse(text, null, out var version, skipped);

            Assert.Equal("2.1", version);
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma", "Delta" }, entries.Select(e => e.Title).ToArray());
            Assert.Equal("https://site.test/g/1", entries[0].Url); // not the favicon URL
            Assert.Equal((0, 132), (entries[0].PagesDone, entries[0].PagesTotal));
            Assert.Equal(QueueStatus.Completed, entries[1].Status);
            Assert.Equal(20, entries[1].PagesDone);
            Assert.Equal(QueueStatus.Queued, entries[2].Status); // paused -> resumes
            Assert.Equal(QueueStatus.Failed, entries[3].Status);
            Assert.Single(skipped);
        }

        [Fact]
        public void ExplicitColumnMapOverridesDetection()
        {
            var text = "[HDoujin Download List, V2.1]\n" + Row("x", "https://a.test/one", "https://b.test/two", "My Title");
            var entries = QueueMangaImporter.Parse(text, new QueueColumnMap { Url = 2, Title = 3 }, out _);

            Assert.Equal("https://b.test/two", entries.Single().Url);
            Assert.Equal("My Title", entries.Single().Title);
        }

        [Fact]
        public void RejectsFilesWithoutTheHeader()
        {
            Assert.Throws<FormatException>(() => QueueMangaImporter.Parse("just,some,csv\n1,2,3", null, out _));
        }

        [Fact]
        public void AnEmptyQueueFileImportsNothing()
        {
            var entries = QueueMangaImporter.Parse("[HDoujin Download List, V2.1]\r\n", null, out var version);
            Assert.Empty(entries);
            Assert.Equal("2.1", version);
        }

        [Fact]
        public void ImportsIntoTheDatabaseOnceAndSkipsDuplicates()
        {
            var file = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"), "queue.manga");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "[HDoujin Download List, V2.1]\r\n" + Row43("Alpha", "https://site.test/g/1", "3/10", "Downloading") + "\r\n" + Row43("Beta", "https://site.test/g/2", "10/10", "Completed") + "\r\n");

            using (var db = new LibraryDb(":memory:"))
            {
                var first = QueueMangaImporter.Import(db, file);
                var second = QueueMangaImporter.Import(db, file);

                Assert.Equal((2, 0), (first.Added, first.AlreadyPresent));
                Assert.Equal((0, 2), (second.Added, second.AlreadyPresent));

                var queue = db.ListQueue();
                Assert.Equal(QueueStatus.Queued, queue[0].Status);
                Assert.Equal(3, queue[0].PagesDone);
                Assert.Equal(QueueStatus.Completed, queue[1].Status);
                Assert.Equal(QueueStatus.Queued, db.NextQueued().Status);
                Assert.Equal("Alpha", db.NextQueued().Title);
            }
        }
    }

    public class QueueRunnerAndTrackerTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));
        private readonly FixtureHttp http = new FixtureHttp();

        public QueueRunnerAndTrackerTests()
        {
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        private static byte[] Jpeg(int seed) => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, (byte)seed, 1, 2, 3 };

        private ModuleRepository Repository(string lua)
        {
            var modules = Path.Combine(root, "modules");
            Directory.CreateDirectory(modules);
            File.WriteAllText(Path.Combine(modules, "t.lua"), "function Register() module.Name='T' module.Domains.Add('t.test') end\n" + lua);
            return ModuleRepository.LoadDirectory(modules, http);
        }

        private const string GalleryLua =
            "function GetInfo() info.Title = 'Gal' .. url:after('/g/') end\n" +
            "function GetPages() pages.Add('https://img.test/' .. url:after('/g/') .. '.jpg') end\n";

        private QueueRunner Runner(LibraryDb db, ModuleRepository repo, List<string> log = null) =>
            new QueueRunner(db, () => new DownloadEngine(repo, http, new DownloadOptions { OutputDirectory = Path.Combine(root, "out"), SaveFormat = SaveFormat.Folder, RetryDelayMilliseconds = 1 }), log == null ? (Action<string>)null : log.Add);

        [Fact]
        public void RunsQueuedItemsAndRecordsResultsAndHistory()
        {
            http.Pages["https://t.test/g/1"] = "<html/>";
            http.Pages["https://t.test/g/2"] = "<html/>";
            http.Files["https://img.test/1.jpg"] = Jpeg(1);
            http.Files["https://img.test/2.jpg"] = Jpeg(2);

            using (var db = new LibraryDb(":memory:"))
            {
                db.Enqueue("https://t.test/g/1");
                db.Enqueue("https://t.test/g/2");

                var handled = Runner(db, Repository(GalleryLua)).RunUntilEmpty(CancellationToken.None);

                Assert.Equal(2, handled);
                var items = db.ListQueue();
                Assert.All(items, i => Assert.Equal(QueueStatus.Completed, i.Status));
                Assert.Equal(new[] { "Gal1", "Gal2" }, items.Select(i => i.Title).ToArray());
                Assert.All(items, i => Assert.Equal((1, 1), (i.PagesDone, i.PagesTotal)));
                Assert.Equal(2, db.ListHistory().Count);
                Assert.True(Directory.Exists(db.ListQueue()[0].Outputs.Single()));
            }
        }

        [Fact]
        public void FailedItemsKeepTheirErrorAndCanBeRetried()
        {
            http.Pages["https://t.test/g/1"] = "<html/>";
            // image is missing -> 404

            using (var db = new LibraryDb(":memory:"))
            {
                var item = db.Enqueue("https://t.test/g/1");
                var runner = Runner(db, Repository(GalleryLua));
                runner.RunUntilEmpty(CancellationToken.None);

                var failed = db.Get(item.Id);
                Assert.Equal(QueueStatus.Failed, failed.Status);
                Assert.Contains("HTTP 404", failed.Error);
                Assert.Equal(QueueStatus.Failed, db.ListHistory().Single().Status);

                http.Files["https://img.test/1.jpg"] = Jpeg(1);
                db.Enqueue("https://t.test/g/1"); // retry = enqueue again
                runner.RunUntilEmpty(CancellationToken.None);

                Assert.Equal(QueueStatus.Completed, db.Get(item.Id).Status);
                Assert.Null(db.Get(item.Id).Error);
            }
        }

        [Fact]
        public void ListPagesAreRecordedAsOneItem()
        {
            http.Pages["https://t.test/list"] = "<html/>";
            http.Pages["https://t.test/g/1"] = "<html/>";
            http.Pages["https://t.test/g/2"] = "<html/>";
            http.Files["https://img.test/1.jpg"] = Jpeg(1);
            http.Files["https://img.test/2.jpg"] = Jpeg(2);
            var lua = "function GetInfo() if url:contains('/list') then Enqueue('/g/1') Enqueue('/g/2') info.Ignore = true else info.Title = 'Gal' .. url:after('/g/') end end\n" +
                      "function GetPages() pages.Add('https://img.test/' .. url:after('/g/') .. '.jpg') end\n";

            using (var db = new LibraryDb(":memory:"))
            {
                var item = db.Enqueue("https://t.test/list");
                Runner(db, Repository(lua)).RunUntilEmpty(CancellationToken.None);

                var done = db.Get(item.Id);
                Assert.Equal(QueueStatus.Completed, done.Status);
                Assert.Equal("Gal1 (+1 more)", done.Title);
                Assert.Equal((2, 2), (done.PagesDone, done.PagesTotal));
                Assert.Equal(2, done.Outputs.Count);
            }
        }

        [Fact]
        public void CancellingStopsTheItemAndKeepsItResumable()
        {
            http.Pages["https://t.test/g/1"] = "<html/>";
            http.Files["https://img.test/1.jpg"] = Jpeg(1);
            http.DownloadDelayMilliseconds = 1500;

            using (var db = new LibraryDb(":memory:"))
            using (var runner = Runner(db, Repository(GalleryLua)))
            {
                var item = db.Enqueue("https://t.test/g/1");
                runner.Start();

                SpinWait.SpinUntil(() => db.Get(item.Id).Status == QueueStatus.Downloading, 5000);
                Assert.True(runner.Cancel(item.Id));
                Assert.True(SpinWait.SpinUntil(() => db.Get(item.Id).Status == QueueStatus.Stopped, 8000), "item should end up Stopped");

                http.DownloadDelayMilliseconds = 0;
                db.Enqueue("https://t.test/g/1"); // run it again
                runner.Wake();
                Assert.True(SpinWait.SpinUntil(() => db.Get(item.Id).Status == QueueStatus.Completed, 8000), "item should complete on the second run");
            }
        }

        [Fact]
        public void PausedRunnerLeavesItemsQueuedUntilResumed()
        {
            http.Pages["https://t.test/g/1"] = "<html/>";
            http.Files["https://img.test/1.jpg"] = Jpeg(1);

            using (var db = new LibraryDb(":memory:"))
            using (var runner = Runner(db, Repository(GalleryLua)))
            {
                runner.Pause();
                runner.Start();
                var item = db.Enqueue("https://t.test/g/1");
                runner.Wake();

                Thread.Sleep(400);
                Assert.Equal(QueueStatus.Queued, db.Get(item.Id).Status);

                runner.Resume();
                Assert.True(SpinWait.SpinUntil(() => db.Get(item.Id).Status == QueueStatus.Completed, 8000));
            }
        }

        [Fact]
        public void StoppingTheQueueInterruptsRunningItemsAndStartingResumesThem()
        {
            http.Pages["https://t.test/g/1"] = "<html/>";
            http.Files["https://img.test/1.jpg"] = Jpeg(1);
            http.DownloadDelayMilliseconds = 1500;

            using (var db = new LibraryDb(":memory:"))
            using (var runner = Runner(db, Repository(GalleryLua)))
            {
                var item = db.Enqueue("https://t.test/g/1");
                runner.Start();

                Assert.True(SpinWait.SpinUntil(() => db.Get(item.Id).Status == QueueStatus.Downloading, 5000));
                runner.Pause();

                Assert.True(SpinWait.SpinUntil(() => runner.ActiveCount == 0, 8000), "the running item should be interrupted");
                Assert.Equal(QueueStatus.Queued, db.Get(item.Id).Status); // not Stopped: starting the queue picks it up again

                http.DownloadDelayMilliseconds = 0;
                Thread.Sleep(300);
                Assert.Equal(QueueStatus.Queued, db.Get(item.Id).Status);

                runner.Resume();
                Assert.True(SpinWait.SpinUntil(() => db.Get(item.Id).Status == QueueStatus.Completed, 8000), "item should complete once the queue is started");
            }
        }

        [Fact]
        public void ItemsDownloadOneAtATimeByDefault()
        {
            for (var i = 1; i <= 3; i++)
            {
                http.Pages["https://t.test/g/" + i] = "<html/>";
                http.Files["https://img.test/" + i + ".jpg"] = Jpeg((byte)i);
            }

            http.DownloadDelayMilliseconds = 100;

            using (var db = new LibraryDb(":memory:"))
            {
                for (var i = 1; i <= 3; i++)
                    db.Enqueue("https://t.test/g/" + i);

                Assert.Equal(3, Runner(db, Repository(GalleryLua)).RunUntilEmpty(CancellationToken.None));
                Assert.Equal(1, http.MaxConcurrentDownloads);
                Assert.All(db.ListQueue(), q => Assert.Equal(QueueStatus.Completed, q.Status));
            }
        }

        [Fact]
        public void ConcurrencyLimitLetsSeveralItemsDownloadTogether()
        {
            for (var i = 1; i <= 4; i++)
            {
                http.Pages["https://t.test/g/" + i] = "<html/>";
                http.Files["https://img.test/" + i + ".jpg"] = Jpeg((byte)i);
            }

            http.DownloadDelayMilliseconds = 300;

            using (var db = new LibraryDb(":memory:"))
            {
                for (var i = 1; i <= 4; i++)
                    db.Enqueue("https://t.test/g/" + i);

                var runner = Runner(db, Repository(GalleryLua));
                runner.MaxConcurrent = 3;

                Assert.Equal(4, runner.RunUntilEmpty(CancellationToken.None));
                Assert.Equal(3, http.MaxConcurrentDownloads);
                Assert.All(db.ListQueue(), q => Assert.Equal(QueueStatus.Completed, q.Status));
                Assert.Equal(4, db.ListHistory().Count);
            }
        }

        [Fact]
        public void ConcurrencyLimitIsKeptWithinBounds()
        {
            using (var db = new LibraryDb(":memory:"))
            {
                var runner = Runner(db, Repository(GalleryLua));
                Assert.Equal(1, runner.MaxConcurrent);

                runner.MaxConcurrent = 0;
                Assert.Equal(1, runner.MaxConcurrent);

                runner.MaxConcurrent = 99;
                Assert.Equal(QueueRunner.ConcurrencyLimit, runner.MaxConcurrent);
            }
        }

        [Fact]
        public void StartingResumesItemsAKilledRunLeftDownloading()
        {
            http.Pages["https://t.test/g/1"] = "<html/>";
            http.Files["https://img.test/1.jpg"] = Jpeg(1);

            using (var db = new LibraryDb(":memory:"))
            {
                var item = db.Enqueue("https://t.test/g/1");
                db.SetStatus(item.Id, QueueStatus.Downloading); // as if the process died mid-download

                using (var runner = Runner(db, Repository(GalleryLua)))
                {
                    runner.Start();
                    Assert.True(SpinWait.SpinUntil(() => db.Get(item.Id).Status == QueueStatus.Completed, 8000));
                }
            }
        }

        private const string SeriesLua =
            "function GetInfo() info.Title = 'My Series' end\n" +
            "function GetChapters() for c in dom.SelectValues('//a[@class=\"c\"]/@href') do chapters.Add(c, 'Ch ' .. c:after('/c/')) end end\n" +
            "function GetPages() pages.Add('https://img.test/s.jpg') end\n";

        private static string SeriesPage(params int[] chapters) =>
            "<html>" + string.Concat(chapters.Select(n => "<a class=\"c\" href=\"/c/" + n + "\">" + n + "</a>")) + "</html>";

        [Fact]
        public void TrackerReportsOnlyChaptersPublishedAfterTracking()
        {
            http.Pages["https://t.test/s"] = SeriesPage(1, 2, 3);

            using (var db = new LibraryDb(":memory:"))
            {
                var tracker = new SeriesTracker(db, Repository(SeriesLua));
                var series = tracker.Track("https://t.test/s");

                Assert.Equal("My Series", series.Title);
                Assert.Equal(3, series.KnownChapters.Count);

                var quiet = tracker.Check(series);
                Assert.Empty(quiet.NewChapters);
                Assert.Null(quiet.Error);

                http.Pages["https://t.test/s"] = SeriesPage(1, 2, 3, 4, 5);
                var found = tracker.Check(db.GetTracked(series.Id));
                Assert.Equal(new[] { "Ch 4", "Ch 5" }, found.NewChapters.Select(c => c.Title).ToArray());
                Assert.Equal(2, found.Series.NewCount);
                Assert.False(found.Queued);
                Assert.Empty(db.ListQueue());

                var again = tracker.Check(db.GetTracked(series.Id));
                Assert.Empty(again.NewChapters); // already reported once
                Assert.Equal(0, again.Series.NewCount);
            }
        }

        [Fact]
        public void TrackerQueuesTheSeriesWhenAutoDownloadIsOn()
        {
            http.Pages["https://t.test/s"] = SeriesPage(1);

            using (var db = new LibraryDb(":memory:"))
            {
                var tracker = new SeriesTracker(db, Repository(SeriesLua));
                var series = tracker.Track("https://t.test/s", autoDownload: true);

                http.Pages["https://t.test/s"] = SeriesPage(1, 2);
                var result = tracker.Check(series);

                Assert.True(result.Queued);
                Assert.Equal("https://t.test/s", db.ListQueue().Single().Url);
                Assert.Equal("tracker", db.ListQueue().Single().Source);
            }
        }

        [Fact]
        public void TrackerRecordsErrorsInsteadOfThrowingOnCheck()
        {
            http.Pages["https://t.test/s"] = SeriesPage(1);

            using (var db = new LibraryDb(":memory:"))
            {
                var tracker = new SeriesTracker(db, Repository(SeriesLua));
                var series = tracker.Track("https://t.test/s");
                db.Enqueue("x"); // unrelated row

                var broken = new SeriesTracker(db, Repository("function GetInfo() Fail(Error.LoginRequired) end\n"));
                var result = broken.Check(series);

                Assert.Equal("LoginRequired", result.Error);
                Assert.Equal("LoginRequired", db.GetTracked(series.Id).LastError);
            }
        }

        [Fact]
        public void TrackingRejectsPagesWithoutChaptersAndListPages()
        {
            http.Pages["https://t.test/g/1"] = "<html/>";

            using (var db = new LibraryDb(":memory:"))
            {
                var e = Assert.Throws<InvalidOperationException>(() => new SeriesTracker(db, Repository(GalleryLua)).Track("https://t.test/g/1"));
                Assert.Contains("no chapter list", e.Message);
            }
        }
    }
}
