using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MangaDownloader.Core;
using MangaDownloader.Core.Download;
using MangaDownloader.Core.Library;
using MangaDownloader.Core.Network;
using MangaDownloader.Core.Sync;
using MangaDownloader.Lua.Runtime;
using MangaDownloader.Net;
using MangaDownloader.Web;

namespace MangaDownloader.Cli
{
    internal static class Program
    {
        private const string OfficialRepository = "https://github.com/HDoujinDownloader/HDoujin-Downloader/tree/master/modules/lua/";

        private const string Usage =
            "manga-downloader - run HDoujin-style Lua modules\n" +
            "\n" +
            "Usage:\n" +
            "  manga-downloader get      <url>... [--output DIR] [--format cbz|zip|pdf|folder] [--parallel N] [--retry N]\n" +
            "                            [--delay MS] [--page-format F] [--chapter-format F] [--folder-format F]\n" +
            "                            [--convert webp,avif=jpg:80]... [--keep-images] [--overwrite]\n" +
            "  manga-downloader info|chapters|pages <url>\n" +
            "  manga-downloader queue add <url>... | list | run [--concurrent N] | remove <id> | clear | import <queue.manga> [--map url=6,title=0,progress=11,status=12]\n" +
            "  manga-downloader history [--limit N] | history clear\n" +
            "  manga-downloader track add <url> [--auto] | list | check [id] | remove <id>\n" +
            "  manga-downloader serve [--port 8780] [--open] [--check-every MINUTES] [--concurrent N]   (local web UI; takes the get options too)\n" +
            "  manga-downloader modules list\n" +
            "  manga-downloader modules sync [--source GITHUB_FOLDER_URL] [--repositories repositories.json]\n" +
            "  manga-downloader cookies import <cookies.json> | list | clear\n" +
            "  manga-downloader credentials set <domain> <username> | list | remove <domain>\n" +
            "\n" +
            "Common options:\n" +
            "  --modules DIR        Lua modules (else $MANGA_DOWNLOADER_MODULES, else ./modules/lua)\n" +
            "  --config DIR         cookies.json / credentials.json / library.db live here (default: %APPDATA%/manga-downloader)\n" +
            "  --proxy URL          http(s) proxy, e.g. http://user:pass@host:8080\n" +
            "  --browser            on a Cloudflare check: a popup browser window opens; you pass the check there yourself and the\n" +
            "                       tool takes over the resulting cookies (nothing automates the page). [--browser-timeout SECONDS]\n" +
            "                       [--paste: paste \"Copy as cURL\" from your own browser instead of using the popup]\n" +
            "  --flaresolverr URL   solve Cloudflare challenges through FlareSolverr, e.g. http://localhost:8191\n" +
            "  --user-agent UA | --random-ua";

        private static int Main(string[] args)
        {
            try
            {
                var options = Options.Parse(args);
                var sub = options.Positional.FirstOrDefault();

                switch (options.Command)
                {
                    case "modules" when sub == "list": return ListModules(options);
                    case "modules" when sub == "sync": return SyncModules(options);
                    case "cookies": return Cookies(options);
                    case "credentials": return Credentials(options);
                    case "queue": return QueueCommand(options);
                    case "history": return HistoryCommand(options);
                    case "track": return TrackCommand(options);
                    case "serve": return Serve(options);
                    case "info":
                    case "chapters":
                    case "pages":
                        return RunModule(options);
                    case "get":
                        return Get(options);
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (Exception e) when (e is IOException || e is ModuleFailedException || e is InvalidOperationException || e is FormatException || e is NotSupportedException)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
            catch (System.Net.HttpListenerException e)
            {
                Console.Error.WriteLine("error: could not start the web UI (" + e.Message + "). Is the port already in use? Try --port with another number.");
                return 1;
            }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException || e is System.Threading.Tasks.TaskCanceledException || e is System.Net.Sockets.SocketException)
            {
                Console.Error.WriteLine("error: " + NetworkErrors.Describe(e));
                return 1;
            }
        }

        // ---- modules ---------------------------------------------------------------------------------------------

        private static int ListModules(Options options)
        {
            var repository = ModuleRepository.LoadDirectory(options.ModulesDirectory);
            var rows = repository.Modules.ToList();

            foreach (var (file, module) in rows)
            {
                var domains = string.Join(", ", module.Domains.All);
                Console.WriteLine($"{module.GetName(),-28} {(module.Adult ? "adult" : "     ")} {module.Language ?? "-",-10} {file.FileName,-28} {domains}");
            }

            var encrypted = repository.Failures.Where(f => f.IsEncrypted).ToList();
            var errors = repository.Failures.Where(f => !f.IsEncrypted).ToList();

            foreach (var failure in errors)
                Console.Error.WriteLine($"FAILED {failure.File}: {failure.Error}");

            Console.WriteLine();
            Console.WriteLine($"{repository.Files.Count} files loaded ({repository.Files.Count(f => f.IsLibrary)} libraries), {rows.Count} modules, " +
                              $"{rows.Sum(r => r.Module.Domains.Count())} domains, {errors.Count} load errors, {encrypted.Count} encrypted (skipped)");
            return errors.Count == 0 ? 0 : 1;
        }

        private static int SyncModules(Options options)
        {
            var sourceUrl = options.Flag("source");
            var repositoriesFile = options.Flag("repositories") ?? Path.Combine(options.ConfigDirectory, "repositories.json");

            if (sourceUrl == null && File.Exists(repositoriesFile))
            {
                var source = RepositorySource.Load(repositoriesFile).FirstOrDefault();
                sourceUrl = source?.RemotePath;
                Console.Error.WriteLine($"using repository \"{source?.Name}\" from {repositoriesFile}");
            }

            var location = GitHubLocation.Parse(sourceUrl ?? OfficialRepository);

            using (var net = NetworkStack.Create(options.Network(withCookies: false), Console.Error.WriteLine))
            {
                Console.Error.WriteLine($"syncing {location} -> {options.ModulesDirectory}");
                var result = new ModuleSync(net.Http, Console.Error.WriteLine).Sync(location, options.ModulesDirectory);

                Console.WriteLine($"{result.Added.Count} added, {result.Updated.Count} updated, {result.Removed.Count} removed, {result.Unchanged} unchanged, {result.Failed.Count} failed");

                foreach (var failure in result.Failed)
                    Console.Error.WriteLine("  ! " + failure);

                return result.Failed.Count == 0 ? 0 : 1;
            }
        }

        // ---- cookies / credentials -------------------------------------------------------------------------------

        private static int Cookies(Options options)
        {
            var file = options.CookieFile;
            var jar = new CookieJar();
            jar.Load(file);

            switch (options.Positional.FirstOrDefault())
            {
                case "import":
                    var source = options.Positional.Skip(1).FirstOrDefault() ?? throw new InvalidOperationException("Missing <cookies.json> to import.");
                    var imported = jar.Load(source);
                    jar.Save(file);
                    Console.WriteLine($"imported {imported} cookie(s); {jar.Count} total in {file}");
                    return 0;

                case "list":
                    // Names only: values are credentials.
                    foreach (var c in jar.Snapshot().OrderBy(c => c.Domain).ThenBy(c => c.Name))
                        Console.WriteLine($"{c.Domain,-30} {c.Name,-28} {(c.Expires.HasValue ? "expires " + c.Expires.Value.ToString("yyyy-MM-dd") : "session")}");
                    return 0;

                case "clear":
                    jar.Clear();
                    jar.Save(file);
                    Console.WriteLine("cleared " + file);
                    return 0;

                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        private static int Credentials(Options options)
        {
            var store = new CredentialStore(options.CredentialsFile);
            var args = options.Positional.Skip(1).ToList();

            switch (options.Positional.FirstOrDefault())
            {
                case "set":
                    if (args.Count < 2)
                        throw new InvalidOperationException("Usage: credentials set <domain> <username>");

                    var password = Environment.GetEnvironmentVariable("MANGA_DOWNLOADER_PASSWORD") ?? ReadPassword("Password for " + args[1] + "@" + args[0] + ": ");
                    store.Set(args[0], args[1], password);
                    Console.WriteLine("saved login for " + args[0] + " in " + options.CredentialsFile);
                    return 0;

                case "list":
                    foreach (var domain in store.Domains)
                        Console.WriteLine($"{domain,-30} {store.UserFor(domain)}");
                    return 0;

                case "remove":
                    return args.Count == 1 && store.Remove(args[0]) ? 0 : 1;

                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        private static string ReadPassword(string prompt)
        {
            Console.Error.Write(prompt);

            if (Console.IsInputRedirected)
                return Console.ReadLine() ?? "";

            var sb = new StringBuilder();

            for (ConsoleKeyInfo key; (key = Console.ReadKey(true)).Key != ConsoleKey.Enter;)
            {
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                        sb.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                }
            }

            Console.Error.WriteLine();
            return sb.ToString();
        }

        // ---- queue / history / tracking / web UI -----------------------------------------------------------------

        private static int QueueCommand(Options options)
        {
            var args = options.Positional.Skip(1).ToList();

            using (var library = new LibraryDb(options.LibraryFile))
            {
                switch (options.Positional.FirstOrDefault())
                {
                    case "add":
                        if (args.Count == 0)
                            throw new InvalidOperationException("Usage: queue add <url>...");
                        foreach (var url in args)
                            Console.WriteLine($"#{library.Enqueue(url, source: "cli").Id} {url}");
                        return 0;

                    case "list":
                        foreach (var q in library.ListQueue())
                            Console.WriteLine($"{q.Id,4}  {q.Status,-11} {(q.PagesTotal > 0 ? q.PagesDone + "/" + q.PagesTotal : "-"),9}  {q.Title ?? q.Url}" + (q.Error != null ? "  [" + q.Error + "]" : ""));
                        return 0;

                    case "remove":
                        return args.Count == 1 && long.TryParse(args[0], out var id) && library.RemoveFromQueue(id) ? 0 : 1;

                    case "clear":
                        Console.WriteLine($"removed {library.ClearCompleted()} completed item(s)");
                        return 0;

                    case "import":
                        var file = args.FirstOrDefault() ?? throw new InvalidOperationException("Usage: queue import <queue.manga>");
                        var result = QueueMangaImporter.Import(library, file, ParseColumnMap(options.Flag("map")));
                        Console.WriteLine($"queue file V{result.Version}: {result.Added} added, {result.AlreadyPresent} already present, {result.Skipped.Count} skipped");
                        foreach (var skipped in result.Skipped.Take(10))
                            Console.Error.WriteLine("  ! " + skipped);
                        return 0;

                    case "run":
                        return RunQueue(options, library);

                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
        }

        private static QueueColumnMap ParseColumnMap(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var map = new QueueColumnMap();

            foreach (var part in text.Split(','))
            {
                var kv = part.Split('=');

                if (kv.Length != 2 || !int.TryParse(kv[1], out var column))
                    throw new FormatException("--map must look like url=6,title=0,progress=11,status=12");

                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "url": map.Url = column; break;
                    case "title": map.Title = column; break;
                    case "progress": map.Progress = column; break;
                    case "status": map.Status = column; break;
                    default: throw new FormatException("Unknown column name in --map: " + kv[0]);
                }
            }

            return map;
        }

        private static int RunQueue(Options options, LibraryDb library)
        {
            var download = BuildDownloadOptions(options);

            using (var net = NetworkStack.Create(options.Network(), Console.Error.WriteLine))
            using (var cancel = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cancel.Cancel(); };

                var repository = ModuleRepository.LoadDirectory(options.ModulesDirectory, net.Http, net.Jar);
                var credentials = new CredentialStore(options.CredentialsFile);
                var runner = new QueueRunner(library, () => new DownloadEngine(repository, net.Http, download, Console.WriteLine, credentials), Console.WriteLine)
                {
                    MaxConcurrent = options.Int("concurrent", 1),
                };

                try
                {
                    var handled = runner.RunUntilEmpty(cancel.Token);
                    var failed = library.ListQueue().Count(q => q.Status == QueueStatus.Failed);
                    Console.WriteLine($"{handled} item(s) processed" + (failed > 0 ? $", {failed} failed (see: queue list)" : ""));
                    return failed == 0 ? 0 : 1;
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("stopped; run `queue run` again to resume.");
                    return 130;
                }
            }
        }

        private static int HistoryCommand(Options options)
        {
            using (var library = new LibraryDb(options.LibraryFile))
            {
                if (options.Positional.FirstOrDefault() == "clear")
                {
                    Console.WriteLine($"removed {library.ClearHistory()} entr(ies)");
                    return 0;
                }

                foreach (var h in library.ListHistory(options.Int("limit", 50)))
                    Console.WriteLine($"{h.FinishedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {h.Status,-9} {h.Pages,4}p  {h.Title}" + (h.Error != null ? "  [" + h.Error + "]" : ""));

                return 0;
            }
        }

        private static int TrackCommand(Options options)
        {
            var args = options.Positional.Skip(1).ToList();
            var sub = options.Positional.FirstOrDefault();

            using (var library = new LibraryDb(options.LibraryFile))
            {
                if (sub == "list")
                {
                    foreach (var t in library.ListTracked())
                        Console.WriteLine($"{t.Id,4}  {t.KnownChapters.Count,4} chapters  {(t.NewCount > 0 ? t.NewCount + " NEW" : "up to date"),-10} {(t.AutoDownload ? "auto" : "    ")}  {t.Title ?? t.Url}" + (t.LastError != null ? "  [" + t.LastError + "]" : ""));
                    return 0;
                }

                if (sub == "remove")
                    return args.Count == 1 && long.TryParse(args[0], out var rid) && library.RemoveTracked(rid) ? 0 : 1;

                if (sub != "add" && sub != "check")
                {
                    Console.Error.WriteLine(Usage);
                    return 2;
                }

                using (var net = NetworkStack.Create(options.Network(), Console.Error.WriteLine))
                {
                    var repository = ModuleRepository.LoadDirectory(options.ModulesDirectory, net.Http, net.Jar);
                    var tracker = new SeriesTracker(library, repository, new CredentialStore(options.CredentialsFile));

                    if (sub == "add")
                    {
                        var url = args.FirstOrDefault() ?? throw new InvalidOperationException("Usage: track add <url> [--auto]");
                        var series = tracker.Track(url, options.Has("auto"));
                        Console.WriteLine($"tracking \"{series.Title}\" ({series.KnownChapters.Count} chapters known)");
                        return 0;
                    }

                    var targets = args.Count > 0 ? args.Select(a => library.GetTracked(long.Parse(a)) ?? throw new InvalidOperationException("No tracked series #" + a)).ToList() : library.ListTracked().ToList();
                    var anyNew = false;

                    foreach (var result in targets.Select(tracker.Check))
                    {
                        if (result.Error != null)
                            Console.Error.WriteLine($"! {result.Series.Title ?? result.Series.Url}: {result.Error}");
                        else if (result.NewChapters.Count > 0)
                        {
                            anyNew = true;
                            Console.WriteLine($"{result.Series.Title}: {result.NewChapters.Count} new" + (result.Queued ? " (queued)" : ""));
                            foreach (var c in result.NewChapters)
                                Console.WriteLine("    " + (c.Title ?? c.Url));
                        }
                        else
                            Console.WriteLine($"{result.Series.Title}: up to date");
                    }

                    if (anyNew)
                        Console.WriteLine("Download them with: queue run   (series marked auto are already queued)");

                    return 0;
                }
            }
        }

        private static int Serve(Options options)
        {
            var port = options.Int("port", 8780);
            var download = BuildDownloadOptions(options);

            using (var library = new LibraryDb(options.LibraryFile))
            using (var net = NetworkStack.Create(options.Network(), Console.Error.WriteLine))
            using (var quit = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Cancel(); };

                var repository = ModuleRepository.LoadDirectory(options.ModulesDirectory, net.Http, net.Jar);
                var credentials = new CredentialStore(options.CredentialsFile);
                var tracker = new SeriesTracker(library, repository, credentials);

                using (var runner = new QueueRunner(library, () => new DownloadEngine(repository, net.Http, download, null, credentials), Console.WriteLine) { MaxConcurrent = options.Int("concurrent", 1) })
                using (var server = new LocalWebServer(library, runner, tracker, port, download: download))
                {
                    // Cloudflare questions are answered in the web page instead of the console.
                    net.Verification.Target = server;

                    server.Start();
                    runner.Start();

                    Console.WriteLine($"{repository.Files.Count} modules loaded. Web UI (keep this address private; it contains the access token):");
                    Console.WriteLine("  " + server.Url);
                    Console.WriteLine("Press Ctrl+C to stop; downloads in progress resume next time.");

                    if (options.Has("open"))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(server.Url) { UseShellExecute = true });

                    var every = options.Int("check-every", 0);

                    while (!quit.Token.WaitHandle.WaitOne(every > 0 ? TimeSpan.FromMinutes(every) : Timeout.InfiniteTimeSpan))
                    {
                        if (every > 0)
                            server.CheckAll();
                    }

                    Console.WriteLine("stopping...");
                    runner.Stop();
                    return 0;
                }
            }
        }

        // ---- scraping / downloading ------------------------------------------------------------------------------

        private static int Get(Options options)
        {
            if (options.Positional.Count == 0)
                throw new InvalidOperationException("Missing <url>.");

            var download = BuildDownloadOptions(options);

            using (var net = NetworkStack.Create(options.Network(), Console.Error.WriteLine))
            using (var cancel = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cancel.Cancel(); };

                var repository = ModuleRepository.LoadDirectory(options.ModulesDirectory, net.Http, net.Jar);
                var engine = new DownloadEngine(repository, net.Http, download, Console.WriteLine, new CredentialStore(options.CredentialsFile));
                var failed = 0;

                try
                {
                    foreach (var url in options.Positional)
                    {
                        foreach (var report in engine.Download(url, cancel.Token))
                        {
                            Console.WriteLine($"{(report.Success ? "OK  " : "FAIL")} {report.Title ?? report.Url}: {report.PagesDownloaded} downloaded, {report.PagesSkipped} already had, {report.PagesTotal} total");

                            foreach (var output in report.Outputs)
                                Console.WriteLine("     -> " + output);

                            foreach (var error in report.Errors)
                                Console.Error.WriteLine("     ! " + error);

                            if (!report.Success)
                                failed++;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("cancelled; rerun the same command to resume.");
                    return 130;
                }

                return failed == 0 ? 0 : 1;
            }
        }

        private static DownloadOptions BuildDownloadOptions(Options options)
        {
            var download = new DownloadOptions
            {
                OutputDirectory = options.Flag("output") ?? "downloads",
                MaxParallel = options.Int("parallel", 6),
                RetryFailed = options.Int("retry", 4),
                PageDelayMilliseconds = options.Int("delay", 0),
                KeepImages = options.Has("keep-images"),
                SkipExisting = !options.Has("overwrite"),
            };

            switch ((options.Flag("format") ?? "cbz").ToLowerInvariant())
            {
                case "cbz": download.SaveFormat = SaveFormat.Cbz; break;
                case "zip": download.SaveFormat = SaveFormat.Zip; break;
                case "pdf": download.SaveFormat = SaveFormat.Pdf; break;
                case "folder": download.SaveFormat = SaveFormat.Folder; break;
                default: throw new InvalidOperationException("--format must be cbz, zip, pdf or folder.");
            }

            foreach (var rule in options.Many("convert"))
                download.ConversionRules.Add(MangaDownloader.Imaging.ImageConversionRule.Parse(rule));

            download.PageNameFormat = options.Flag("page-format") ?? download.PageNameFormat;
            download.ChapterFormat = options.Flag("chapter-format") ?? download.ChapterFormat;
            download.FolderFormat = options.Flag("folder-format") ?? download.FolderFormat;

            return download;
        }

        private static int RunModule(Options options)
        {
            var url = options.Positional.FirstOrDefault() ?? throw new InvalidOperationException("Missing <url>.");

            using (var net = NetworkStack.Create(options.Network(), Console.Error.WriteLine))
            {
                var repository = ModuleRepository.LoadDirectory(options.ModulesDirectory, net.Http, net.Jar);
                var match = repository.Find(url) ?? throw new InvalidOperationException("No module supports " + url);
                var session = new ModuleSession(match.File, new CredentialStore(options.CredentialsFile));

                Console.Error.WriteLine($"module: {match.Module.GetName()} ({match.File.FileName})");

                switch (options.Command)
                {
                    case "info":
                        var info = session.GetInfo(url);
                        foreach (var field in info.Fields)
                            Console.WriteLine($"{field.Key}: {field.Value}");
                        if (info.Ignore)
                            Console.WriteLine("(list page) queued: " + info.Enqueued.Count);
                        foreach (var queued in info.Enqueued)
                            Console.WriteLine("  " + queued);
                        break;

                    case "chapters":
                        foreach (var chapter in session.GetChapters(url))
                            Console.WriteLine($"{chapter.Title}\t{chapter.Url}");
                        break;

                    case "pages":
                        foreach (var page in session.GetPages(url))
                            Console.WriteLine(page.Url);
                        break;
                }
            }

            return 0;
        }

        private sealed class Options
        {
            private static readonly HashSet<string> Switches =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "keep-images", "overwrite", "random-ua", "open", "auto", "browser", "paste" };

            private readonly Dictionary<string, string> flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<string>> repeated = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            public string Command { get; private set; }
            public List<string> Positional { get; } = new List<string>();

            public string ModulesDirectory =>
                Flag("modules") ?? Environment.GetEnvironmentVariable("MANGA_DOWNLOADER_MODULES") ?? Path.Combine("modules", "lua");

            public string ConfigDirectory =>
                Flag("config") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "manga-downloader");

            public string CookieFile => Flag("cookies") ?? Path.Combine(ConfigDirectory, "cookies.json");

            public string LibraryFile => Flag("library") ?? Path.Combine(ConfigDirectory, "library.db");

            public string CredentialsFile => Flag("credentials-file") ?? Path.Combine(ConfigDirectory, "credentials.json");

            public string Flag(string name) => flags.TryGetValue(name, out var v) ? v : null;

            /// <summary>All values of a flag that may be given more than once (e.g. --convert).</summary>
            public IEnumerable<string> Many(string name) => repeated.TryGetValue(name, out var v) ? v : Enumerable.Empty<string>();

            public bool Has(string name) => flags.ContainsKey(name);

            public int Int(string name, int fallback) => Flag(name) != null ? int.Parse(Flag(name)) : fallback;

            public NetworkOptions Network(bool withCookies = true) => new NetworkOptions
            {
                ProxyUrl = Flag("proxy") ?? Environment.GetEnvironmentVariable("MANGA_DOWNLOADER_PROXY"),
                FlareSolverrUrl = Flag("flaresolverr"),
                OpenInBrowser = Has("browser") || Has("paste"),
                PasteInstead = Has("paste"),
                BrowserProfileDirectory = Path.Combine(ConfigDirectory, "webview-profile"),
                BrowserTimeoutSeconds = Int("browser-timeout", 300),
                UserAgent = Flag("user-agent"),
                RandomUserAgent = Has("random-ua"),
                CookieFile = withCookies ? CookieFile : null,
            };

            public static Options Parse(string[] args)
            {
                var o = new Options();

                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i].StartsWith("--") && Switches.Contains(args[i].Substring(2)))
                    {
                        o.flags[args[i].Substring(2)] = "true";
                    }
                    else if (args[i].StartsWith("--") && i + 1 < args.Length)
                    {
                        var name = args[i].Substring(2);
                        var value = args[++i];
                        o.flags[name] = value;

                        if (!o.repeated.TryGetValue(name, out var list))
                            o.repeated[name] = list = new List<string>();

                        list.Add(value);
                    }
                    else if (o.Command == null)
                    {
                        o.Command = args[i];
                    }
                    else
                    {
                        o.Positional.Add(args[i]);
                    }
                }

                return o;
            }
        }
    }
}
