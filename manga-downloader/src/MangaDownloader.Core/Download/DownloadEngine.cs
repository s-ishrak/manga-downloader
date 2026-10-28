using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MangaDownloader.Core.Naming;
using MangaDownloader.Core.Output;
using MangaDownloader.Imaging;
using MangaDownloader.Lua.Api;
using MangaDownloader.Lua.Runtime;
using MangaDownloader.Net;

namespace MangaDownloader.Core.Download
{
    public enum SaveFormat
    {
        Folder,
        Cbz,
        Pdf,
        Zip,
    }

    /// <summary>Defaults mirror HDoujin's config.ini ([Formatting]).</summary>
    public sealed class DownloadOptions
    {
        public string OutputDirectory { get; set; } = "downloads";
        public string PageNameFormat { get; set; } = "%TITLE%_%PAGE%";
        public string ChapterFormat { get; set; } = "%CHAPTER#% - %TITLE%";
        public string FolderFormat { get; set; } = "%TITLE%";
        public bool IgnoreSingleChapterFormatting { get; set; } = true;
        public int MaxParallel { get; set; } = 6;
        public int RetryFailed { get; set; } = 4;
        public int RetryDelayMilliseconds { get; set; } = 500;
        public int PageDelayMilliseconds { get; set; }
        public SaveFormat SaveFormat { get; set; } = SaveFormat.Cbz;
        /// <summary>
        /// Keep the image folder after packing, and keep pre-conversion originals in its <c>_original</c> sub-folder.
        /// Off (the default) deletes both once the archive is written / the conversion succeeded.
        /// </summary>
        public bool KeepImages { get; set; }

        /// <summary>
        /// For a series with several chapters saved as cbz/zip/pdf: one archive for the whole series (chapters in order, each in its own
        /// sub-folder inside it) instead of one archive per chapter. Ignored for single chapters and the folder format.
        /// </summary>
        public bool CombineChapters { get; set; }

        /// <summary>Per-image format conversions applied after download (first matching rule wins), e.g. webp,avif=jpg:80.</summary>
        public List<ImageConversionRule> ConversionRules { get; private set; } = new List<ImageConversionRule>();
        public bool SkipExisting { get; set; } = true;
        public bool FixLongPaths { get; set; } = true;

        /// <summary>Replace invalid filename characters with this instead of dropping them.</summary>
        public char? ReplaceSpecialWith { get; set; }

        /// <summary>How many levels of list pages (tags, searches) to follow.</summary>
        public int MaxQueueDepth { get; set; } = 2;

        /// <summary>A snapshot for one download, so settings changed while it runs only apply to the next one.</summary>
        public DownloadOptions Clone()
        {
            var copy = (DownloadOptions)MemberwiseClone();
            copy.ConversionRules = new List<ImageConversionRule>(ConversionRules);
            return copy;
        }
    }

    /// <summary>Raised after every page of a gallery finishes (downloaded, resumed or failed).</summary>
    public sealed class DownloadProgress
    {
        public DownloadReport Report { get; set; }
        public int PagesDone { get; set; }
        public int PagesTotal { get; set; }

        /// <summary>Overall completion 0..1 of this gallery: finished chapters plus the share of the current chapter's pages.</summary>
        public double Fraction { get; set; }
    }

    public sealed class DownloadReport
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public List<string> Outputs { get; } = new List<string>();
        public int PagesTotal { get; set; }
        public int PagesDownloaded { get; set; }
        public int PagesSkipped { get; set; }
        public int PagesFailed { get; set; }
        public int ChaptersTotal { get; set; }
        public int ChaptersDone { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public bool Success => Errors.Count == 0;
    }

    /// <summary>
    /// url -> module -> info -> chapters -> pages -> parallel image download -> archive.
    /// Pages already on disk are kept (resume); a chapter is only archived once every page is present.
    /// </summary>
    public sealed class DownloadEngine
    {
        private readonly ModuleRepository repository;
        private readonly IHttpBackend http;
        private readonly DownloadOptions options;
        private readonly Action<string> log;
        private readonly ICredentialProvider credentials;

        /// <summary>Called (from worker threads, one at a time) as pages finish.</summary>
        public Action<DownloadProgress> Progress { get; set; }

        public DownloadEngine(ModuleRepository repository, IHttpBackend http, DownloadOptions options, Action<string> log = null, ICredentialProvider credentials = null)
        {
            this.credentials = credentials;
            this.repository = repository;
            this.http = http;
            this.options = options;
            this.log = log ?? (_ => { });
        }

        public IList<DownloadReport> Download(string url, CancellationToken cancel = default) => Download(url, 0, cancel);

        private IList<DownloadReport> Download(string url, int depth, CancellationToken cancel)
        {
            var reports = new List<DownloadReport>();
            var report = new DownloadReport { Url = url };

            try
            {
                var match = repository.Find(url) ?? throw new InvalidOperationException("No module supports " + url);
                var session = new ModuleSession(match.File, credentials);

                log($"{match.Module.GetName()}: {url}");
                var info = session.GetInfo(url);
                report.Title = info.Title;

                if (info.Ignore)
                {
                    log($"list page: {info.Enqueued.Count} item(s) queued");

                    if (depth >= options.MaxQueueDepth)
                        return reports;

                    foreach (var queued in info.Enqueued.Distinct())
                    {
                        cancel.ThrowIfCancellationRequested();
                        reports.AddRange(Download(queued, depth + 1, cancel));
                    }

                    return reports;
                }

                reports.Add(report);
                DownloadSeries(url, match, session, info, report, cancel);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                report.Errors.Add(e is ModuleFailedException failed ? Describe(failed, url) : IsNetworkFailure(e) ? Net.NetworkErrors.Describe(e) : e.Message);

                if (!reports.Contains(report))
                    reports.Add(report);
            }

            return reports;
        }

        private void RaiseProgress(DownloadReport report, int chapterDone, int chapterTotal)
        {
            var current = chapterTotal > 0 ? (double)chapterDone / chapterTotal : 0;
            var chapters = Math.Max(1, report.ChaptersTotal);

            Progress?.Invoke(new DownloadProgress
            {
                Report = report,
                PagesDone = report.PagesDownloaded + report.PagesSkipped + report.PagesFailed,
                PagesTotal = report.PagesTotal,
                Fraction = Math.Min(1.0, (report.ChaptersDone + current) / chapters),
            });
        }

        private static bool IsNetworkFailure(Exception e)
        {
            for (; e != null; e = e.InnerException)
                if (e is HttpRequestException || e is System.Net.Sockets.SocketException || e is TaskCanceledException)
                    return true;

            return false;
        }

                private static string Describe(ModuleFailedException e, string url)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

            switch (e.Reason)
            {
                case "LoginRequired": return $"LoginRequired: {host} needs a login and none worked. Store one with: credentials set {host} <username>";
                case "LoginFailed": return $"LoginFailed: {host} rejected the stored username/password.";
                case "CaptchaRequired": return $"CaptchaRequired: {host} asks for a captcha, which is not supported.";
                case "DomainNotSupported": return $"DomainNotSupported: the module does not handle {host}.";
                default: return e.Reason;
            }
        }

        private void DownloadSeries(string url, ModuleMatch match, ModuleSession session, InfoResult info, DownloadReport report, CancellationToken cancel)
        {
            var chapters = session.GetChapters(url).ToList();
            var single = chapters.Count <= 1;
            report.ChaptersTotal = Math.Max(1, chapters.Count);

            if (chapters.Count == 0)
                chapters.Add(new ChapterResult { Url = url });

            var infoValues = BuildInfoValues(info, url, match);
            var folderName = SafeName(NameFormatter.Format(options.FolderFormat, infoValues));
            var seriesDir = Path.Combine(options.OutputDirectory, folderName);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var combine = options.CombineChapters && !single && ArchivePath(seriesDir) != null;
            var combinedPath = combine ? ArchivePath(seriesDir) : null;
            var chapterDirs = new List<string>();

            if (combine && options.SkipExisting && File.Exists(combinedPath))
            {
                log("exists, skipping: " + combinedPath);
                report.Outputs.Add(combinedPath);
                report.ChaptersDone = report.ChaptersTotal;
                RaiseProgress(report, 0, 0);
                return;
            }

            var errorsBefore = report.Errors.Count;

            for (var c = 0; c < chapters.Count; c++)
            {
                cancel.ThrowIfCancellationRequested();

                var chapter = chapters[c];
                string workDir, archiveBase;

                if (single && options.IgnoreSingleChapterFormatting)
                {
                    workDir = seriesDir;
                    archiveBase = seriesDir;
                }
                else
                {
                    var values = new Dictionary<string, string>(infoValues, StringComparer.OrdinalIgnoreCase)
                    {
                        ["CHAPTER#"] = string.IsNullOrEmpty(chapter.Chapter) ? (c + 1).ToString() : chapter.Chapter,
                        ["CHAPTER"] = chapter.Title ?? "",
                        ["VOLUME"] = chapter.Volume ?? "",
                        ["TITLE"] = string.IsNullOrWhiteSpace(chapter.Title) ? infoValues["TITLE"] : chapter.Title,
                    };

                    var name = SafeName(NameFormatter.Format(options.ChapterFormat, values));

                    for (var n = 2; !usedNames.Add(name); n++)
                        name = SafeName(NameFormatter.Format(options.ChapterFormat, values)) + " (" + n + ")";

                    workDir = Path.Combine(seriesDir, name);
                    archiveBase = workDir;
                }

                DownloadChapter(chapter.Url ?? url, session, infoValues, info, workDir, combine ? null : archiveBase, report, cancel);
                chapterDirs.Add(workDir);
                report.ChaptersDone++;
                RaiseProgress(report, 0, 0);
            }

            // Only pack once every chapter is complete; otherwise keep the folders and let a rerun resume.
            if (!combine || report.Errors.Count > errorsBefore)
                return;

            var entries = new List<KeyValuePair<string, string>>();

            foreach (var dir in chapterDirs)
                foreach (var file in ArchiveFiles.List(dir))
                    entries.Add(new KeyValuePair<string, string>(file, Path.GetFileName(dir) + "/" + Path.GetFileName(file)));

            if (options.SaveFormat == SaveFormat.Pdf)
                PdfWriter.Create(entries.Select(e => e.Key).ToList(), combinedPath, info.Fields);
            else
                CbzWriter.Create(entries, combinedPath, options.SaveFormat == SaveFormat.Cbz ? info.Fields : null);

            log("wrote " + combinedPath);
            report.Outputs.Add(combinedPath);

            if (!options.KeepImages)
            {
                foreach (var dir in chapterDirs)
                    Directory.Delete(dir, recursive: true);

                if (!Directory.EnumerateFileSystemEntries(seriesDir).Any())
                    Directory.Delete(seriesDir);
            }
        }

        /// <param name="archiveBase">Where the chapter's own archive goes (extension added); null means no archive is written here.</param>
        private void DownloadChapter(string chapterUrl, ModuleSession session, Dictionary<string, string> infoValues, InfoResult info,
            string workDir, string archiveBase, DownloadReport report, CancellationToken cancel)
        {
            var archivePath = archiveBase == null ? null : ArchivePath(archiveBase);

            if (archivePath != null && options.SkipExisting && File.Exists(archivePath))
            {
                log("exists, skipping: " + archivePath);
                report.Outputs.Add(archivePath);
                return;
            }

            var pages = session.GetPages(chapterUrl).Where(p => !string.IsNullOrEmpty(p.Url)).ToList();

            if (pages.Count == 0)
            {
                report.Errors.Add("No pages found at " + chapterUrl);
                return;
            }

            Directory.CreateDirectory(workDir);

            // Pages are downloaded, unscrambled and converted in here, and only then moved next to their siblings, so a page
            // that exists in workDir is always finished (resume never trusts a half-processed file).
            var staging = Path.Combine(workDir, StagingFolder);
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            report.PagesTotal += pages.Count;

            var names = PageNames(pages.Count, infoValues, workDir);
            var limiter = new RateLimiter(session.RateLimits(chapterUrl));
            var gate = new object();
            var done = 0;
            var errors = new List<string>();

            Parallel.For(0, pages.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallel), CancellationToken = cancel },
                i =>
                {
                    var existing = FindExisting(workDir, names[i]);

                    if (existing != null)
                    {
                        lock (gate)
                        {
                            report.PagesSkipped++;
                            log($"[{++done}/{pages.Count}] have {Path.GetFileName(existing)}");
                            RaiseProgress(report, done, pages.Count);
                        }

                        return;
                    }

                    try
                    {
                        var file = DownloadPage(pages[i], i, chapterUrl, workDir, names[i], session, limiter, gate, cancel);

                        lock (gate)
                        {
                            report.PagesDownloaded++;
                            log($"[{++done}/{pages.Count}] {Path.GetFileName(file)}");
                            RaiseProgress(report, done, pages.Count);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        lock (gate)
                        {
                            errors.Add($"page {i + 1}: {e.Message}");
                            log($"[{++done}/{pages.Count}] FAILED page {i + 1}: {e.Message}");
                            report.PagesFailed++;
                            RaiseProgress(report, done, pages.Count);
                        }
                    }

                    if (options.PageDelayMilliseconds > 0)
                        cancel.WaitHandle.WaitOne(options.PageDelayMilliseconds);
                });

            Directory.Delete(staging, recursive: true);

            if (errors.Count > 0)
            {
                report.Errors.AddRange(errors.OrderBy(e => e));
                return; // keep what we have; rerun resumes
            }

            if (archivePath == null)
            {
                if (archiveBase != null) // folder format; when combining, the series archive is the output
                    report.Outputs.Add(workDir);

                return;
            }

            if (options.SaveFormat == SaveFormat.Pdf)
                PdfWriter.Create(ArchiveFiles.List(workDir), archivePath, info.Fields);
            else
                CbzWriter.Create(workDir, archivePath, options.SaveFormat == SaveFormat.Cbz ? info.Fields : null, pages.Count);

            log("wrote " + archivePath);
            report.Outputs.Add(archivePath);

            if (!options.KeepImages)
                Directory.Delete(workDir, recursive: true);
        }

        private string ArchivePath(string archiveBase)
        {
            switch (options.SaveFormat)
            {
                case SaveFormat.Cbz: return archiveBase + ".cbz";
                case SaveFormat.Zip: return archiveBase + ".zip";
                case SaveFormat.Pdf: return archiveBase + ".pdf";
                default: return null;
            }
        }

        private List<string> PageNames(int count, Dictionary<string, string> infoValues, string workDir)
        {
            var width = Math.Max(3, count.ToString().Length);
            var names = new List<string>();

            for (var i = 0; i < count; i++)
            {
                var values = new Dictionary<string, string>(infoValues, StringComparer.OrdinalIgnoreCase)
                {
                    ["PAGE"] = (i + 1).ToString().PadLeft(width, '0'),
                    ["PAGES"] = count.ToString(),
                };

                names.Add(SafeName(new NameFormatter(values).Expand(options.PageNameFormat)));
            }

            // A format without %PAGE% would make every file collide.
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                names = names.Select((n, i) => n + "_" + (i + 1).ToString().PadLeft(width, '0')).ToList();

            if (options.FixLongPaths)
                names = names.Select(n => PathNames.FitLength(workDir, n, ".jpeg")).ToList();

            return names;
        }

        private const string StagingFolder = "_staging";
        private const string OriginalFolder = "_original";

        private static string FindExisting(string dir, string baseName)
        {
            foreach (var ext in ImageSniffer.ImageExtensions)
            {
                var path = Path.Combine(dir, baseName + "." + ext);

                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return path;
            }

            return null;
        }

        private string DownloadPage(PageResult page, int index, string chapterUrl, string dir, string baseName, ModuleSession session,
            RateLimiter limiter, object sessionLock, CancellationToken cancel)
        {
            PageResult resolved;

            lock (sessionLock)
                resolved = session.BeforeDownloadPage(page, index, chapterUrl);

            var candidates = new[] { resolved.Url }.Concat(resolved.BackupUrls).Where(u => !string.IsNullOrEmpty(u)).ToList();
            Exception last = null;

            foreach (var candidate in candidates)
            {
                for (var attempt = 0; attempt <= options.RetryFailed; attempt++)
                {
                    cancel.ThrowIfCancellationRequested();

                    try
                    {
                        var staged = Fetch(candidate, resolved, chapterUrl, Path.Combine(dir, StagingFolder), baseName, session, limiter, sessionLock, cancel, allowReaderPage: true);
                        return PostProcess(staged, resolved, index, chapterUrl, dir, session, sessionLock);
                    }
                    catch (HttpStatusException e) when (!e.Retryable)
                    {
                        last = e;
                        break; // 4xx: retrying the same URL is pointless; try the backup
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        last = e;

                        if (attempt < options.RetryFailed)
                            cancel.WaitHandle.WaitOne(options.RetryDelayMilliseconds * (attempt + 1));
                    }
                }
            }

            throw last ?? new InvalidOperationException("No URL for page");
        }

        /// <summary>Module hook (unscrambling) then format conversion, then the finished file moves out of staging.</summary>
        private string PostProcess(string staged, PageResult page, int index, string chapterUrl, string dir, ModuleSession session, object sessionLock)
        {
            var current = staged;

            try
            {
                lock (sessionLock)
                    session.AfterDownloadPage(page, index, current, chapterUrl);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                // A page the module could not finish is wrong output; fail it rather than ship a scrambled image.
                throw new InvalidOperationException("AfterDownloadPage: " + e.Message, e);
            }

            if (options.ConversionRules.Count > 0)
            {
                try
                {
                    var converted = ImageConverter.Convert(current, options.ConversionRules);

                    if (converted != current)
                    {
                        if (options.KeepImages)
                        {
                            var originals = Path.Combine(dir, OriginalFolder);
                            Directory.CreateDirectory(originals);
                            var kept = Path.Combine(originals, Path.GetFileName(current));
                            if (File.Exists(kept))
                                File.Delete(kept);
                            File.Move(current, kept);
                        }
                        else
                        {
                            File.Delete(current);
                        }

                        current = converted;
                    }
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    // Conversion is cosmetic: keep the original file rather than failing the page.
                    log("could not convert " + Path.GetFileName(current) + " (" + e.Message + "); keeping the original");
                }
            }

            var final = Path.Combine(dir, Path.GetFileName(current));

            if (File.Exists(final))
                File.Delete(final);

            File.Move(current, final);
            return final;
        }

        private string Fetch(string url, PageResult page, string chapterUrl, string dir, string baseName, ModuleSession session,
            RateLimiter limiter, object sessionLock, CancellationToken cancel, bool allowReaderPage)
        {
            limiter.Wait(url, cancel);

            var request = new HttpRequest { Url = url, Referer = page.Referer ?? chapterUrl };

            foreach (var h in page.Headers)
                request.Headers[h.Key] = h.Value;

            lock (sessionLock)
                foreach (var c in session.CookiesFor(url))
                    request.Cookies[c.Key] = c.Value;

            var part = Path.Combine(dir, baseName + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part");

            try
            {
                HttpResult result;

                using (var stream = File.Create(part))
                    result = http.Download(request, stream);

                var status = result.StatusCode;

                if (status < 200 || status >= 300)
                    throw new HttpStatusException(status, url);

                var length = new FileInfo(part).Length;

                if (length == 0)
                    throw new IOException("Empty response from " + url);

                var ext = ImageSniffer.FromFile(part);

                if (ext == null && ImageSniffer.LooksLikeText(part))
                {
                    // The module handed us a reader page instead of an image.
                    if (!allowReaderPage)
                        throw new IOException("Not an image: " + url);

                    var html = File.ReadAllText(part, Encoding.UTF8);
                    var imageUrl = ReaderPage.FindImageUrl(html, result.Url ?? url) ?? throw new IOException("No image found on reader page " + url);

                    File.Delete(part);
                    return Fetch(imageUrl, new PageResult { Referer = url, Headers = page.Headers, FileExtensionHint = page.FileExtensionHint }, chapterUrl, dir, baseName, session,
                        limiter, sessionLock, cancel, allowReaderPage: false);
                }

                ext = ext
                    ?? ImageSniffer.FromContentType(result.ContentType)
                    ?? ImageSniffer.NormalizeHint(page.FileExtensionHint)
                    ?? ImageSniffer.FromUrl(url)
                    ?? "jpg";

                var final = Path.Combine(dir, baseName + "." + ext);

                if (File.Exists(final))
                    File.Delete(final);

                File.Move(part, final);
                return final;
            }
            finally
            {
                if (File.Exists(part))
                    File.Delete(part);
            }
        }

        private string SafeName(string name) => PathNames.Sanitize(name, options.ReplaceSpecialWith);

        private static Dictionary<string, string> BuildInfoValues(InfoResult info, string url, ModuleMatch match)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in MangaInfoUserData.PropertyNames)
                values[name] = info.Fields.TryGetValue(name, out var v) ? v : "";

            if (string.IsNullOrWhiteSpace(values["TITLE"]))
                values["TITLE"] = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath.Replace('/', '_') : "untitled";

            values["URL"] = url;
            values["MODULE"] = match.Module.GetName() ?? "";
            values["DOMAIN"] = match.Domain ?? "";
            values["DATE"] = DateTime.Now.ToString("yyyy-MM-dd");
            return values;
        }
    }

    public sealed class HttpStatusException : IOException
    {
        public HttpStatusException(int status, string url) : base($"HTTP {status} for {url}")
        {
            Status = status;
        }

        public int Status { get; }

        /// <summary>Server errors, rate limiting and timeouts are worth another attempt; other 4xx are not.</summary>
        public bool Retryable => Status >= 500 || Status == 429 || Status == 408;
    }
}
