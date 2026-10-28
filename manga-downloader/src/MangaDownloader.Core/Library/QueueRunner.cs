using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MangaDownloader.Core.Download;

namespace MangaDownloader.Core.Library
{
    /// <summary>
    /// Works through the queue one item at a time (the pages of an item download in parallel; modules are single-threaded Lua, so items are not).
    /// Progress, results and history are written to the database as it goes, which is what makes a killed run resumable.
    /// </summary>
    public sealed class QueueRunner : IDisposable
    {
        private readonly LibraryDb library;
        private readonly Func<DownloadEngine> engineFactory;
        private readonly Action<string> log;
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0);
        private readonly object gate = new object();

        private CancellationTokenSource stopAll;
        private CancellationTokenSource currentItem;
        private Task loop;
        private long? currentId;
        private volatile bool paused;

        public QueueRunner(LibraryDb library, Func<DownloadEngine> engineFactory, Action<string> log = null)
        {
            this.library = library;
            this.engineFactory = engineFactory;
            this.log = log ?? (_ => { });
        }

        /// <summary>Raised whenever an item's state or progress changed (from the runner's thread).</summary>
        public event Action Changed;

        public bool IsPaused => paused;

        public bool IsRunning => loop != null && !loop.IsCompleted;

        public long? CurrentId
        {
            get
            {
                lock (gate)
                    return currentId;
            }
        }

        /// <summary>Starts the background loop (idempotent). Items left "Downloading" by a crash are queued again first.</summary>
        public void Start()
        {
            lock (gate)
            {
                if (IsRunning)
                    return;

                library.ResetInterrupted();
                stopAll = new CancellationTokenSource();
                var token = stopAll.Token;
                loop = Task.Run(() => Loop(token));
            }
        }

        public void Pause() => paused = true;

        public void Resume()
        {
            paused = false;
            wake.Release();
        }

        /// <summary>Tells the loop there is something new in the queue.</summary>
        public void Wake() => wake.Release();

        /// <summary>Stops the running item (it stays resumable) and ends the loop.</summary>
        public void Stop()
        {
            Task toWait;

            lock (gate)
            {
                stopAll?.Cancel();
                toWait = loop;
            }

            wake.Release();

            try
            {
                toWait?.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException)
            {
                // the loop only ends by cancellation
            }
        }

        /// <summary>Cancels one item: a running one stops (and can be retried); a waiting one is marked stopped.</summary>
        public bool Cancel(long id)
        {
            lock (gate)
            {
                if (currentId == id && currentItem != null)
                {
                    currentItem.Cancel();
                    return true;
                }
            }

            var item = library.Get(id);

            if (item == null || item.Status != QueueStatus.Queued)
                return false;

            library.SetStatus(id, QueueStatus.Stopped);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Processes everything currently queued, then returns (for the command line). Returns the number of items handled.</summary>
        public int RunUntilEmpty(CancellationToken cancel)
        {
            library.ResetInterrupted();
            var handled = 0;

            for (QueueItem item; (item = library.NextQueued()) != null;)
            {
                cancel.ThrowIfCancellationRequested();
                Process(item, cancel);
                handled++;
            }

            return handled;
        }

        private void Loop(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                var item = paused ? null : library.NextQueued();

                if (item == null)
                {
                    try
                    {
                        wake.Wait(stop);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                try
                {
                    Process(item, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private void Process(QueueItem item, CancellationToken stop)
        {
            using (var itemCancel = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                lock (gate)
                {
                    currentId = item.Id;
                    currentItem = itemCancel;
                }

                library.SetStatus(item.Id, QueueStatus.Downloading);
                library.SetProgress(item.Id, 0, 0, null, 0);
                Changed?.Invoke();

                var title = item.Title;
                var reports = new List<DownloadReport>();

                try
                {
                    var engine = engineFactory();
                    var sums = new Dictionary<DownloadReport, (int Done, int Total)>();
                    var fraction = 0.0;
                    var throttle = Stopwatch.StartNew();

                    engine.Progress = p =>
                    {
                        lock (sums)
                        {
                            sums[p.Report] = (p.PagesDone, p.PagesTotal);
                            fraction = p.Fraction;

                            if (throttle.ElapsedMilliseconds < 300)
                                return;

                            throttle.Restart();
                            library.SetProgress(item.Id, sums.Values.Sum(v => v.Done), sums.Values.Sum(v => v.Total), p.Report.Title, (int)Math.Round(fraction * 1000));
                        }

                        Changed?.Invoke();
                    };

                    reports.AddRange(engine.Download(item.Url, itemCancel.Token));
                }
                catch (OperationCanceledException)
                {
                    // Pages already on disk are kept: running the item again resumes it.
                    library.SetStatus(item.Id, QueueStatus.Stopped, "stopped");
                    Changed?.Invoke();

                    if (stop.IsCancellationRequested)
                        throw;

                    return;
                }
                catch (Exception e)
                {
                    Finish(item, title, QueueStatus.Failed, e.Message, reports);
                    return;
                }
                finally
                {
                    lock (gate)
                    {
                        currentId = null;
                        currentItem = null;
                    }
                }

                var failed = reports.Where(r => !r.Success).ToList();
                var status = failed.Count == 0 ? QueueStatus.Completed : QueueStatus.Failed;
                var error = failed.Count == 0 ? null : string.Join(" | ", failed.SelectMany(r => r.Errors).Take(5));

                if (reports.Count > 0)
                    title = reports.Count == 1 ? reports[0].Title ?? title : (reports[0].Title ?? title) + " (+" + (reports.Count - 1) + " more)";

                Finish(item, title, status, error, reports);
            }
        }

        private void Finish(QueueItem item, string title, QueueStatus status, string error, List<DownloadReport> reports)
        {
            var outputs = reports.SelectMany(r => r.Outputs).ToList();
            var done = reports.Sum(r => r.PagesDownloaded + r.PagesSkipped);
            var total = reports.Sum(r => r.PagesTotal);

            library.SetProgress(item.Id, done, total, title, status == QueueStatus.Completed ? 1000 : (int?)null);
            library.SetStatus(item.Id, status, error, outputs);
            library.AddHistory(item.Url, title ?? item.Url, status, total, outputs, error);

            log($"{status}: {title ?? item.Url}" + (error != null ? " - " + error : ""));
            Changed?.Invoke();
        }

        public void Dispose()
        {
            Stop();
            stopAll?.Dispose();
            wake.Dispose();
        }
    }
}
