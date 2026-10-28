using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MangaDownloader.Imaging;

namespace MangaDownloader.Core.Output
{
    /// <summary>
    /// Writes one image per page into a PDF with no re-encoding for JPEGs (embedded as-is with DCTDecode). Other formats, CMYK JPEGs and
    /// transparent images are converted to RGB JPEG first. Pages are written one at a time, so memory stays at one image.
    /// </summary>
    public static class PdfWriter
    {
        // 96 dpi: a 1000 px wide scan becomes a 750 pt (10.4 in) page, which viewers fit to the window anyway.
        private const double PointsPerPixel = 0.75;

        private sealed class PageImage
        {
            public byte[] Jpeg;
            public int Width;
            public int Height;
            public int Components;
        }

        public static void Create(IList<string> images, string pdfPath, IDictionary<string, string> info = null)
        {
            if (images.Count == 0)
                throw new InvalidOperationException("No images to put in a PDF.");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pdfPath)));
            var temp = pdfPath + ".tmp";
            var offsets = new Dictionary<int, long>();

            try
            {
                using (var file = File.Create(temp))
                {
                    void Text(string s) => file.Write(Encoding.Latin1.GetBytes(s), 0, s.Length);
                    void Begin(int id) { offsets[id] = file.Position; Text($"{id} 0 obj\n"); }

                    var pageCount = images.Count;
                    const int catalog = 1, pages = 2, infoObj = 3;
                    int PageId(int i) => 4 + i * 3;

                    Text("%PDF-1.4\n");
                    file.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6); // marks the file as binary for transfer tools

                    Begin(catalog);
                    Text($"<< /Type /Catalog /Pages {pages} 0 R >>\nendobj\n");

                    Begin(pages);
                    Text("<< /Type /Pages /Count " + pageCount + " /Kids [" + string.Join(" ", Enumerable.Range(0, pageCount).Select(i => PageId(i) + " 0 R")) + "] >>\nendobj\n");

                    Begin(infoObj);
                    Text("<< " + InfoEntries(info) + "/Producer " + TextString("manga-downloader") + " /CreationDate (D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "Z) >>\nendobj\n");

                    for (var i = 0; i < pageCount; i++)
                    {
                        var image = Load(images[i]);
                        var w = Num(image.Width * PointsPerPixel);
                        var h = Num(image.Height * PointsPerPixel);
                        var page = PageId(i);
                        var content = "q " + w + " 0 0 " + h + " 0 0 cm /Im0 Do Q";

                        Begin(page);
                        Text($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 {page + 2} 0 R >> >> /Contents {page + 1} 0 R >>\nendobj\n");

                        Begin(page + 1);
                        Text($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

                        Begin(page + 2);
                        Text($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /{(image.Components == 1 ? "DeviceGray" : "DeviceRGB")} " +
                             $"/BitsPerComponent 8 /Filter /DCTDecode /Length {image.Jpeg.Length} >>\nstream\n");
                        file.Write(image.Jpeg, 0, image.Jpeg.Length);
                        Text("\nendstream\nendobj\n");
                    }

                    var total = 4 + pageCount * 3; // object numbers 0 .. total-1
                    var xref = file.Position;

                    Text($"xref\n0 {total}\n0000000000 65535 f \n");

                    for (var id = 1; id < total; id++)
                        Text(offsets[id].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

                    Text($"trailer\n<< /Size {total} /Root {catalog} 0 R /Info {infoObj} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
                }

                if (File.Exists(pdfPath))
                    File.Delete(pdfPath);

                File.Move(temp, pdfPath);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        private static string InfoEntries(IDictionary<string, string> info)
        {
            if (info == null)
                return "";

            string Get(params string[] keys) => keys.Select(k => info.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var sb = new StringBuilder();

            void Entry(string key, string value)
            {
                if (value != null)
                    sb.Append('/').Append(key).Append(' ').Append(TextString(value)).Append(' ');
            }

            Entry("Title", Get("Title"));
            Entry("Author", Get("Artist", "Author", "Circle"));
            Entry("Subject", Get("Series", "Parody"));
            Entry("Keywords", Get("Tags"));
            return sb.ToString();
        }

        /// <summary>A PDF text string: plain (escaped) when ASCII, otherwise UTF-16BE with a byte-order mark.</summary>
        public static string TextString(string value)
        {
            if (value.All(c => c >= 0x20 && c < 0x7F))
                return "(" + value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";

            return "<FEFF" + string.Concat(Encoding.BigEndianUnicode.GetBytes(value).Select(b => b.ToString("X2"))) + ">";
        }

        private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static PageImage Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var parsed = TryReadJpeg(bytes);

            // Only plain gray/RGB JPEGs can be embedded untouched; everything else is normalised first.
            if (parsed == null || (parsed.Components != 1 && parsed.Components != 3))
            {
                bytes = ImageConverter.ToJpegBytes(path);
                parsed = TryReadJpeg(bytes) ?? throw new InvalidDataException("Could not read " + path + " as an image.");
            }

            parsed.Jpeg = bytes;
            return parsed;
        }

        /// <summary>Width, height and component count from the JPEG start-of-frame marker; null if this is not a JPEG.</summary>
        private static PageImage TryReadJpeg(byte[] b)
        {
            if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8)
                return null;

            var i = 2;

            while (i + 4 < b.Length)
            {
                if (b[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                var marker = b[i + 1];

                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }

                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    i += 2;
                    continue;
                }

                var length = (b[i + 2] << 8) | b[i + 3];
                var isFrame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

                if (isFrame && i + 9 < b.Length)
                    return new PageImage { Height = (b[i + 5] << 8) | b[i + 6], Width = (b[i + 7] << 8) | b[i + 8], Components = b[i + 9] };

                i += 2 + length;
            }

            return null;
        }
    }
}
