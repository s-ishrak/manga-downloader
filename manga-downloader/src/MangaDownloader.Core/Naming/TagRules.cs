using System;
using System.Collections.Generic;
using System.Linq;

namespace MangaDownloader.Core.Naming
{
    public static class TagRules
    {
        public static List<string> Apply(IEnumerable<string> tags, IEnumerable<string> rules)
        {
            if (tags == null)
                return new List<string>();

            var tagList = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            if (rules == null)
                return tagList;

            var ruleList = rules.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
            if (ruleList.Count == 0)
                return tagList;

            var current = new List<string>(tagList);

            foreach (var rule in ruleList)
            {
                var arrowIdx = rule.IndexOf("->", StringComparison.Ordinal);
                if (arrowIdx >= 0)
                {
                    var source = rule.Substring(0, arrowIdx).Trim();
                    var target = rule.Substring(arrowIdx + 2).Trim();
                    for (var i = 0; i < current.Count; i++)
                    {
                        if (string.Equals(current[i], source, StringComparison.OrdinalIgnoreCase))
                            current[i] = target;
                    }
                    continue;
                }

                if (rule.StartsWith("-", StringComparison.Ordinal))
                {
                    var pattern = rule.Substring(1).Trim();
                    if (pattern.EndsWith(":*", StringComparison.Ordinal))
                    {
                        var ns = pattern.Substring(0, pattern.Length - 1); // e.g. "parody:"
                        current.RemoveAll(t => t.StartsWith(ns, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        current.RemoveAll(t => string.Equals(t, pattern, StringComparison.OrdinalIgnoreCase));
                    }
                    continue;
                }

                if (rule.StartsWith("~", StringComparison.Ordinal))
                {
                    var ns = rule.Substring(1).Trim();
                    if (!ns.EndsWith(":", StringComparison.Ordinal))
                        ns += ":";

                    for (var i = 0; i < current.Count; i++)
                    {
                        if (current[i].StartsWith(ns, StringComparison.OrdinalIgnoreCase))
                            current[i] = current[i].Substring(ns.Length).Trim();
                    }
                    continue;
                }
            }

            return current.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
