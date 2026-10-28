using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MangaDownloader.Core.Download
{
    /// <summary>Works out what a downloaded file really is (magic bytes beat headers and URL guesses).</summary>
    public static class ImageSniffer
    {
        public static readonly string[] ImageExtensions = { "jpg", "jpeg", "png", "gif", "webp", "avif", "bmp", "jxl", "heic", "tiff", "svg" };

        /// <summary>Extension (without dot) for recognised image data, or null.</summary>
        public static string FromBytes(byte[] b, int length)
        {
            bool Starts(params byte[] sig) => length >= sig.Length && sig.Select((x, i) => b[i] == x).All(x => x);

            if (Starts(0xFF, 0xD8, 0xFF)) return "jpg";
            if (Starts(0x89, 0x50, 0x4E, 0x47)) return "png";
            if (Starts(0x47, 0x49, 0x46, 0x38)) return "gif";
            if (length >= 12 && Starts(0x52, 0x49, 0x46, 0x46) && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "webp";
            if (length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p')
            {
                var brand = Encoding.ASCII.GetString(b, 8, 4);
                if (brand.StartsWith("avi")) return "avif";
                if (brand.StartsWith("hei") || brand.StartsWith("mif")) return "heic";
            }
            if (Starts(0x42, 0x4D)) return "bmp";
            if (Starts(0xFF, 0x0A) || Starts(0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20)) return "jxl";
            if (Starts(0x49, 0x49, 0x2A, 0x00) || Starts(0x4D, 0x4D, 0x00, 0x2A)) return "tiff";
            return null;
        }

        public static string FromFile(string path)
        {
            var buffer = new byte[16];
            int read;

            using (var f = File.OpenRead(path))
                read = f.Read(buffer, 0, buffer.Length);

            return FromBytes(buffer, read);
        }

        /// <summary>True when the file starts like a web page/JSON/text rather than binary data.</summary>
        public static bool LooksLikeText(string path)
        {
            var buffer = new byte[256];
            int read;

            using (var f = File.OpenRead(path))
                read = f.Read(buffer, 0, buffer.Length);

            var head = Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿', ' ', '\r', '\n', '\t').ToLowerInvariant();
            return head.StartsWith("<") || head.StartsWith("{") || head.StartsWith("[");
        }

        public static string FromContentType(string contentType)
        {
            switch ((contentType ?? "").ToLowerInvariant())
            {
                case "image/jpeg": case "image/jpg": return "jpg";
                case "image/png": return "png";
                case "image/gif": return "gif";
                case "image/webp": return "webp";
                case "image/avif": return "avif";
                case "image/bmp": return "bmp";
                case "image/jxl": return "jxl";
                case "image/heic": return "heic";
                default: return null;
            }
        }

        public static string FromUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            var ext = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
            return ImageExtensions.Contains(ext) ? (ext == "jpeg" ? "jpg" : ext) : null;
        }

        public static string NormalizeHint(string hint)
        {
            var ext = (hint ?? "").Trim().TrimStart('.').ToLowerInvariant();
            return ext == "jpeg" ? "jpg" : ImageExtensions.Contains(ext) ? ext : null;
        }
    }
}
