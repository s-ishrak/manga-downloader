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
    /// Works through the queue, up to <see cref="MaxConcurrent"/> items at a time (the pages of an item download in parallel as well).
    /// Modules are single-threaded Lua, so two items on the same module take turns inside it; the downloads themselves overlap.
    /// Progress, results and history are written to the database as it goes, which is what makes a killed run resumable.
    /// </summary>
    public sealed class QueueRunner : IDisposable
    {
        /// <summary>The most items that may download at once.</summary>
        public const int ConcurrencyLimit = 8;

        private readonly LibraryDb library;
        private readonly Func<DownloadEngine> engineFactory;
        private readonly Action<string> log;
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0);
        private readonly object gate = new object();
        private readonly Dictionary<long, ActiveItem> active = new Dictionary<long, ActiveItem>();

        private CancellationTokenSource stopAll;
        private Task loop;
        private volatile bool paused;
        private int maxConcurrent = 1;

        private sealed class ActiveItem
        {
            public CancellationTokenSource Cancel;

            /// <summary>True when the item is interrupted because the queue was stopped, so it goes back to Queued instead of Stopped.</summary>
            public volatile bool Requeue;
        }

        public QueueRunner(LibraryDb library, Func<DownloadEngine> engineFactory, Action<string> log = null)
        {
            this.library = library;
            this.engineFactory = engineFactory;
            this.log = log ?? (_ => { });
        }

        /// <summary>Raised whenever an item's state or progress changed (from the runner's thread).</summary>
        public event Action Changed;

        /// <summary>True while the queue is stopped: nothing new starts and the items that were downloading went back to Queued.</summary>
        public bool IsPaused => paused;

        public bool IsRunning => loop != null && !loop.IsCompleted;

        /// <summary>How many items download at the same time (1 to <see cref="ConcurrencyLimit"/>; other values are clamped).</summary>
        public int MaxConcurrent
        {
            get => Volatile.Read(ref maxConcurrent);
            set
            {
                Volatile.Write(ref maxConcurrent, Math.Max(1, Math.Min(ConcurrencyLimit, value)));
                Wake(); // a higher limit may let waiting items start now
            }
        }

        public int ActiveCount
        {
            get
            {
                lock (gate)
                    return active.Count;
            }
        }

        public bool IsActive(long id)
        {
            lock (gate)
                return active.ContainsKey(id);
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

        /// <summary>
        /// Stops the queue: nothing new starts, and the items downloading right now are interrupted and go back to Queued
        /// (pages already on disk are kept) until <see cref="Resume"/>.
        /// </summary>
        public void Pause()
        {
            paused = true;

            lock (gate)
                foreach (var item in active.Values)
                {
                    item.Requeue = true;
                    item.Cancel.Cancel();
                }
        }

        /// <summary>Starts the queue again after <see cref="Pause"/>.</summary>
        public void Resume()
        {
            paused = false;
            Wake();
        }

        /// <summary>Tells the loop there is something new in the queue.</summary>
        public void Wake()
        {
            try
            {
                wake.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Stops the running items (they stay resumable) and ends the loop.</summary>
        public void Stop()
        {
            Task toWait;

            lock (gate)
            {
                stopAll?.Cancel();
                toWait = loop;
            }

            Wake();

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
                if (active.TryGetValue(id, out var running))
                {
                    running.Requeue = false;
                    running.Cancel.Cancel();
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
            return Pump(cancel, untilEmpty: true);
        }

        private void Loop(CancellationToken stop)
        {
            try
            {
                Pump(stop, untilEmpty: false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>Keeps up to <see cref="MaxConcurrent"/> items downloading; returns when the queue is empty (<paramref name="untilEmpty"/>) or throws when stopped.</summary>
        private int Pump(CancellationToken stop, bool untilEmpty)
        {
            var handled = 0;
            var running = new List<Task>();

            try
            {
                while (true)
                {
                    stop.ThrowIfCancellationRequested();
                    handled += running.RemoveAll(t => t.IsCompleted);

                    var item = !paused && running.Count < MaxConcurrent ? library.NextQueued() : null;

                    if (item != null)
                    {
                        // Claimed here, not in the task, so the next pass cannot pick the same item again.
                        var slot = Begin(item, stop);

                        running.Add(Task.Run(() =>
                        {
                            try
                            {
                                Process(item, slot);
                            }
                            finally
                            {
                                Wake(); // a slot is free
                            }
                        }));

                        continue;
                    }

                    if (untilEmpty && running.Count == 0)
                        return handled;

                    wake.Wait(stop);
                }
            }
            finally
            {
                // Items still running here were cancelled along with `stop`; let them record that before returning.
                try
                {
                    Task.WaitAll(running.ToArray(), TimeSpan.FromSeconds(30));
                }
                catch (AggregateException)
                {
                }
            }
        }

        private ActiveItem Begin(QueueItem item, CancellationToken stop)
        {
            var slot = new ActiveItem { Cancel = CancellationTokenSource.CreateLinkedTokenSource(stop) };

            lock (gate)
            {
                active[item.Id] = slot;

                // The queue was stopped between the loop's check and now.
                if (paused)
                {
                    slot.Requeue = true;
                    slot.Cancel.Cancel();
                }
            }

            library.SetStatus(item.Id, QueueStatus.Downloading);
            library.SetProgress(item.Id, 0, 0, null, 0, 0, 0);
            Changed?.Invoke();
            return slot;
        }

        private void Process(QueueItem item, ActiveItem slot)
        {
            try
            {
                var title = item.Title;
                var reports = new List<DownloadReport>();

                try
                {
                    var engine = engineFactory();
                    var sums = new Dictionary<DownloadReport, (int Done, int Total, int ChaptersDone, int ChaptersTotal)>();
                    var fraction = 0.0;
                    var throttle = Stopwatch.StartNew();

                    engine.Progress = p =>
                    {
                        lock (sums)
                        {
                            sums[p.Report] = (p.PagesDone, p.PagesTotal, p.Report.ChaptersDone, p.Report.ChaptersTotal);
                            fraction = p.Fraction;

                            if (throttle.ElapsedMilliseconds < 300)
                                return;

                            throttle.Restart();
                            library.SetProgress(item.Id, sums.Values.Sum(v => v.Done), sums.Values.Sum(v => v.Total), p.Report.Title, (int)Math.Round(fraction * 1000),
                                sums.Values.Sum(v => v.ChaptersDone), sums.Values.Sum(v => v.ChaptersTotal));
                        }

                        Changed?.Invoke();
                    };

                    reports.AddRange(engine.Download(item.Url, slot.Cancel.Token));
                }
                catch (OperationCanceledException)
                {
                    // Pages already on disk are kept: running the item again resumes it.
                    if (slot.Requeue)
                        library.SetStatus(item.Id, QueueStatus.Queued);
                    else
                        library.SetStatus(item.Id, QueueStatus.Stopped, "stopped");

                    Changed?.Invoke();
                    return;
                }
                catch (Exception e)
                {
                    Finish(item, title, QueueStatus.Failed, e.Message, reports);
                    return;
                }

                var failed = reports.Where(r => !r.Success).ToList();
                var status = failed.Count == 0 ? QueueStatus.Completed : QueueStatus.Failed;
                var error = failed.Count == 0 ? null : string.Join(" | ", failed.SelectMany(r => r.Errors).Take(5));

                if (reports.Count > 0)
                    title = reports.Count == 1 ? reports[0].Title ?? title : (reports[0].Title ?? title) + " (+" + (reports.Count - 1) + " more)";

                Finish(item, title, status, error, reports);
            }
            finally
            {
                lock (gate)
                    active.Remove(item.Id);

                slot.Cancel.Dispose();
            }
        }

        private void Finish(QueueItem item, string title, QueueStatus status, string error, List<DownloadReport> reports)
        {
            var outputs = reports.SelectMany(r => r.Outputs).ToList();
            var done = reports.Sum(r => r.PagesDownloaded + r.PagesSkipped);
            var total = reports.Sum(r => r.PagesTotal);

            library.SetProgress(item.Id, done, total, title, status == QueueStatus.Completed ? 1000 : (int?)null,
                reports.Sum(r => r.ChaptersDone), reports.Sum(r => r.ChaptersTotal));
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
