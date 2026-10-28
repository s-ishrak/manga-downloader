using System;
using System.Collections.Generic;
using System.Linq;
using MangaDownloader.Lua.Runtime;

namespace MangaDownloader.Core.Library
{
    public sealed class ChapterRef
    {
        public string Title { get; set; }
        public string Url { get; set; }
    }

    public sealed class CheckResult
    {
        public TrackedSeries Series { get; set; }
        public List<ChapterRef> NewChapters { get; } = new List<ChapterRef>();
        public string Error { get; set; }
        public bool Queued { get; set; }
    }

    /// <summary>
    /// Remembers the chapter list of a series and reports chapters that appear later. The first snapshot is the baseline, so only
    /// chapters published after you started tracking count as new.
    /// </summary>
    public sealed class SeriesTracker
    {
        private readonly LibraryDb library;
        private readonly ModuleRepository repository;
        private readonly ICredentialProvider credentials;

        public SeriesTracker(LibraryDb library, ModuleRepository repository, ICredentialProvider credentials = null)
        {
            this.library = library;
            this.repository = repository;
            this.credentials = credentials;
        }

        public TrackedSeries Track(string url, bool autoDownload = false)
        {
            var (title, chapters) = Fetch(url);

            if (chapters.Count == 0)
                throw new InvalidOperationException("This page has no chapter list, so there is nothing to track. (Single-gallery pages are not series.)");

            return library.AddTracked(url, title, chapters.Select(c => c.Url), autoDownload);
        }

        public CheckResult Check(TrackedSeries series)
        {
            var result = new CheckResult { Series = series };

            try
            {
                var (title, chapters) = Fetch(series.Url);
                var known = new HashSet<string>(series.KnownChapters, StringComparer.OrdinalIgnoreCase);

                result.NewChapters.AddRange(chapters.Where(c => !known.Contains(c.Url)));

                // Keep everything ever seen, so a chapter the site drops and re-adds is not reported twice.
                var all = series.KnownChapters.Concat(result.NewChapters.Select(c => c.Url)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                library.UpdateTracked(series.Id, all, result.NewChapters.Count, null, title);

                if (series.AutoDownload && result.NewChapters.Count > 0)
                {
                    library.Enqueue(series.Url, title ?? series.Title, "tracker");
                    result.Queued = true;
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                result.Error = e.Message;
                library.UpdateTracked(series.Id, null, series.NewCount, e.Message);
            }

            result.Series = library.GetTracked(series.Id) ?? series;
            return result;
        }

        public IList<CheckResult> CheckAll() => library.ListTracked().Select(Check).ToList();

        private (string Title, List<ChapterRef> Chapters) Fetch(string url)
        {
            var match = repository.Find(url) ?? throw new InvalidOperationException("No module supports " + url);
            var session = new ModuleSession(match.File, credentials);
            var info = session.GetInfo(url);

            if (info.Ignore)
                throw new InvalidOperationException("This is a list page (it only queues other galleries); track a series page instead.");

            var chapters = session.GetChapters(url).Select(c => new ChapterRef { Title = c.Title, Url = c.Url }).Where(c => !string.IsNullOrEmpty(c.Url)).ToList();
            return (info.Title, chapters);
        }
    }
}
