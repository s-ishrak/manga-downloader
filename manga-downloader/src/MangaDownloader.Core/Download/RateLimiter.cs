using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MangaDownloader.Lua.Runtime;

namespace MangaDownloader.Core.Download
{
    /// <summary>Sliding-window limiter for a module's <c>RateLimits</c> rules; blocks the caller until a request is allowed.</summary>
    public sealed class RateLimiter
    {
        private readonly IList<RateLimitRule> rules;
        private readonly Dictionary<RateLimitRule, Queue<long>> windows = new Dictionary<RateLimitRule, Queue<long>>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly object gate = new object();

        public RateLimiter(IList<RateLimitRule> rules)
        {
            this.rules = rules ?? new List<RateLimitRule>();
        }

        public void Wait(string url, CancellationToken cancel)
        {
            foreach (var rule in rules.Where(r => url.IndexOf(r.Pattern, StringComparison.OrdinalIgnoreCase) >= 0 && r.Requests > 0))
            {
                while (true)
                {
                    int delay;

                    lock (gate)
                    {
                        if (!windows.TryGetValue(rule, out var q))
                            windows[rule] = q = new Queue<long>();

                        var now = clock.ElapsedMilliseconds;

                        while (q.Count > 0 && now - q.Peek() >= rule.WindowMilliseconds)
                            q.Dequeue();

                        if (q.Count < rule.Requests)
                        {
                            q.Enqueue(now);
                            break;
                        }

                        delay = (int)Math.Max(1, rule.WindowMilliseconds - (now - q.Peek()));
                    }

                    if (cancel.WaitHandle.WaitOne(delay))
                        cancel.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
