using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MangaDownloader.Core.Library
{
    public sealed class ImportedEntry
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public QueueStatus Status { get; set; } = QueueStatus.Queued;
        public int PagesDone { get; set; }
        public int PagesTotal { get; set; }
    }

    public sealed class ImportResult
    {
        public int Added { get; set; }
        public int AlreadyPresent { get; set; }
        public List<string> Skipped { get; } = new List<string>();
        public string Version { get; set; }
    }

    /// <summary>Optional explicit 0-based column numbers; anything left null is detected from the cell contents.</summary>
    public sealed class QueueColumnMap
    {
        public int? Url { get; set; }
        public int? Title { get; set; }
        public int? Progress { get; set; }
        public int? Status { get; set; }
    }

    /// <summary>
    /// Imports HDoujin's <c>queue.manga</c>: a <c>[HDoujin Download List, V2.1]</c> line, then one tab-separated row per entry (43 columns in V2.1).
    /// The exact column order is not documented and no populated sample was available when this was written, so cells are recognised by
    /// what they contain (the gallery URL, an <c>n/m</c> progress cell, a status word, the first free-text cell as title) unless a
    /// <see cref="QueueColumnMap"/> says otherwise.
    /// </summary>
    public static class QueueMangaImporter
    {
        private static readonly Regex Header = new Regex(@"^\[HDoujin Download List, V(?<v>[\d.]+)\]\s*$");
        private static readonly Regex Progress = new Regex(@"^(\d+)\s*/\s*(\d+)$");
        private static readonly Regex Favicon = new Regex(@"(favicon|\.ico($|\?)|\.(png|gif|svg|webp)($|\?))", RegexOptions.IgnoreCase);

        public static IList<ImportedEntry> ParseFile(string path, QueueColumnMap map, out string version, ICollection<string> skipped = null) =>
            Parse(File.ReadAllText(path, Encoding.UTF8), map, out version, skipped);

        public static IList<ImportedEntry> Parse(string text, QueueColumnMap map, out string version, ICollection<string> skipped = null)
        {
            var lines = text.TrimStart('﻿').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var header = lines.Length > 0 ? Header.Match(lines[0]) : Match.Empty;

            if (!header.Success)
                throw new FormatException("Not a HDoujin queue file (expected a first line like \"[HDoujin Download List, V2.1]\").");

            version = header.Groups["v"].Value;
            var entries = new List<ImportedEntry>();

            for (var n = 1; n < lines.Length; n++)
            {
                if (string.IsNullOrWhiteSpace(lines[n]))
                    continue;

                var cells = lines[n].Split('\t');
                var entry = ParseRow(cells, map);

                if (entry == null)
                    skipped?.Add($"line {n + 1}: no usable gallery URL");
                else
                    entries.Add(entry);
            }

            return entries;
        }

        private static ImportedEntry ParseRow(string[] cells, QueueColumnMap map)
        {
            map = map ?? new QueueColumnMap();
            string Cell(int? index) => index.HasValue && index.Value < cells.Length ? cells[index.Value].Trim() : null;

            var url = Cell(map.Url) ?? cells.Select(c => c.Trim()).FirstOrDefault(c => IsUrl(c) && !Favicon.IsMatch(c));

            if (string.IsNullOrEmpty(url))
                return null;

            var entry = new ImportedEntry { Url = url };

            var progress = Cell(map.Progress) ?? cells.Select(c => c.Trim()).FirstOrDefault(c => Progress.IsMatch(c));
            var pm = progress == null ? Match.Empty : Progress.Match(progress);

            if (pm.Success)
            {
                entry.PagesDone = int.Parse(pm.Groups[1].Value);
                entry.PagesTotal = int.Parse(pm.Groups[2].Value);
            }

            var status = Cell(map.Status) ?? cells.Select(c => c.Trim()).FirstOrDefault(c => MapStatus(c) != null);
            entry.Status = MapStatus(status) ?? QueueStatus.Queued;

            // A finished entry whose progress cell was missing still counts all its pages as done.
            if (entry.Status == QueueStatus.Completed && entry.PagesTotal > 0)
                entry.PagesDone = entry.PagesTotal;

            entry.Title = Cell(map.Title) ?? cells.Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0 && !IsUrl(c) && !Progress.IsMatch(c) && MapStatus(c) == null && !LooksLikePath(c) && !Regex.IsMatch(c, @"^(true|false|-?\d+)$", RegexOptions.IgnoreCase));
            return entry;
        }

        private static bool IsUrl(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikePath(string s) => Regex.IsMatch(s, @"^([A-Za-z]:[\\/]|\\\\|/)");

        /// <summary>Maps HDoujin's status words; null when the text is not a status.</summary>
        public static QueueStatus? MapStatus(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "queued":
                case "downloading":
                case "waiting":
                case "paused":
                case "pending":
                    return QueueStatus.Queued; // anything unfinished resumes
                case "completed":
                case "complete":
                case "finished":
                case "done":
                    return QueueStatus.Completed;
                case "failed":
                case "error":
                    return QueueStatus.Failed;
                case "stopped":
                case "canceled":
                case "cancelled":
                    return QueueStatus.Stopped;
                default:
                    return null;
            }
        }

        public static ImportResult Import(LibraryDb library, string path, QueueColumnMap map = null) =>
            ImportText(library, File.ReadAllText(path, Encoding.UTF8), map);

        /// <summary>Imports the contents of a queue.manga file (the web page uploads the text, since a browser cannot share the file's path).</summary>
        public static ImportResult ImportText(LibraryDb library, string text, QueueColumnMap map = null)
        {
            var result = new ImportResult();
            var entries = Parse(text, map, out var version, result.Skipped);
            result.Version = version;

            foreach (var entry in entries)
            {
                var before = library.ListQueue().Count(q => LibraryDb.UrlKey(q.Url) == LibraryDb.UrlKey(entry.Url));

                if (before > 0)
                {
                    result.AlreadyPresent++;
                    continue;
                }

                library.Enqueue(entry.Url, entry.Title, "import", entry.Status, entry.PagesDone, entry.PagesTotal);
                result.Added++;
            }

            return result;
        }
    }
}
