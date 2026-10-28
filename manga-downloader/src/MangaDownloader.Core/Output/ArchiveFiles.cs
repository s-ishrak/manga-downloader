using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MangaDownloader.Core.Output
{
    internal static class ArchiveFiles
    {
        /// <summary>The page files of a chapter folder in natural order (page2 before page10). Sub-folders (_original, _staging) and temp files are not part of the output.</summary>
        public static List<string> List(string folder) =>
            Directory.EnumerateFiles(folder)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), new NaturalComparer())
                .ToList();
    }

    internal sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            int i = 0, j = 0;

            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    var si = i;
                    var sj = j;

                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;

                    var a = x.Substring(si, i - si).TrimStart('0');
                    var b = y.Substring(sj, j - sj).TrimStart('0');
                    var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);

                    if (c != 0)
                        return c;
                }
                else
                {
                    var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));

                    if (c != 0)
                        return c;

                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
