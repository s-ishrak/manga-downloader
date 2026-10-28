using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MangaDownloader.Core.Naming
{
    public static class PathNames
    {
        private static readonly string[] Reserved =
            { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        /// <summary>
        /// Makes one path component safe on Windows and Unix: invalid characters are dropped (or replaced with
        /// <paramref name="replacement"/>), trailing dots/spaces removed, reserved device names suffixed.
        /// </summary>
        public static string Sanitize(string name, char? replacement = null)
        {
            var invalid = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }.Concat(Path.GetInvalidFileNameChars()).ToHashSet();
            var sb = new StringBuilder();

            foreach (var c in name ?? "")
            {
                if (char.IsControl(c) || invalid.Contains(c))
                {
                    if (replacement.HasValue)
                        sb.Append(replacement.Value);
                }
                else
                {
                    sb.Append(c);
                }
            }

            var result = string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');

            if (result.Length == 0)
                return "_";

            var stem = result.Split('.')[0];

            if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
                result = "_" + result;

            return result;
        }

        /// <summary>Shortens <paramref name="name"/> (keeping <paramref name="suffix"/>, e.g. an extension) so the full path stays under <paramref name="maxPath"/>.</summary>
        public static string FitLength(string directory, string name, string suffix = "", int maxPath = 240)
        {
            var available = maxPath - directory.Length - 1 - suffix.Length;
            available = Math.Max(available, 16);

            if (name.Length <= available)
                return name;

            return name.Substring(0, available).TrimEnd('.', ' ');
        }
    }
}
