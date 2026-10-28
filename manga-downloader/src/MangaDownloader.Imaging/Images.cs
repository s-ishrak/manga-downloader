using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageMagick;

namespace MangaDownloader.Imaging
{
    /// <summary>Reads an image's size without decoding the pixels.</summary>
    public static class ImageProbe
    {
        public static (int Width, int Height) Size(string path)
        {
            var info = new MagickImageInfo(path);
            return ((int)info.Width, (int)info.Height);
        }
    }

    /// <summary>"Convert these formats to that one": <c>webp,avif=jpg:80</c> (quality after the colon, default 80).</summary>
    public sealed class ImageConversionRule
    {
        public string[] From { get; set; }
        public string To { get; set; }
        public int Quality { get; set; } = 80;

        public bool Matches(string extension) => From.Contains(Normalize(extension), StringComparer.OrdinalIgnoreCase);

        public static ImageConversionRule Parse(string text)
        {
            var eq = text.IndexOf('=');

            if (eq <= 0 || eq == text.Length - 1)
                throw new FormatException("Conversion rule must look like webp,avif=jpg:80 but was: " + text);

            var target = text.Substring(eq + 1).Split(':');
            var rule = new ImageConversionRule
            {
                From = text.Substring(0, eq).Split(',').Select(Normalize).Where(e => e.Length > 0).ToArray(),
                To = Normalize(target[0]),
            };

            if (target.Length > 1 && !int.TryParse(target[1], out var quality))
                throw new FormatException("Quality must be a number: " + text);
            else if (target.Length > 1)
                rule.Quality = Math.Max(1, Math.Min(100, int.Parse(target[1])));

            if (!new[] { "jpg", "png", "webp" }.Contains(rule.To))
                throw new FormatException("Can only convert to jpg, png or webp: " + text);

            return rule;
        }

        public static string Normalize(string extension)
        {
            var e = (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();
            return e == "jpeg" ? "jpg" : e;
        }
    }

    public static class ImageConverter
    {
        /// <summary>
        /// Converts <paramref name="path"/> if a rule matches its extension. Returns the new file path (same extension swapped), or
        /// <paramref name="path"/> itself when nothing applies (no rule, animated image). The original is left in place; the caller decides what to do with it.
        /// </summary>
        public static string Convert(string path, IEnumerable<ImageConversionRule> rules)
        {
            var ext = ImageConversionRule.Normalize(Path.GetExtension(path));
            var rule = rules.FirstOrDefault(r => r.Matches(ext));

            if (rule == null || rule.To == ext)
                return path;

            var target = Path.ChangeExtension(path, rule.To);

            using (var frames = new MagickImageCollection(path))
            {
                if (frames.Count > 1)
                    return path; // animated: flattening it would destroy it

                var image = frames[0];
                image.AutoOrient();

                if (rule.To == "jpg" && image.HasAlpha)
                    image.ColorAlpha(MagickColors.White); // flatten onto white; JPEG has no alpha

                image.Quality = (uint)rule.Quality;
                image.Format = rule.To == "jpg" ? MagickFormat.Jpeg : rule.To == "png" ? MagickFormat.Png : MagickFormat.WebP;

                var temp = target + ".converting";

                try
                {
                    image.Write(temp);

                    if (File.Exists(target))
                        File.Delete(target);

                    File.Move(temp, target);
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }

            return target;
        }

        /// <summary>A baseline RGB/gray JPEG of any supported image (first frame, flattened on white). Used to embed non-JPEG pages in a PDF.</summary>
        public static byte[] ToJpegBytes(string path, int quality = 90)
        {
            using (var image = new MagickImage(path))
            {
                image.AutoOrient();

                if (image.HasAlpha)
                    image.ColorAlpha(MagickColors.White); // flatten onto white; JPEG has no alpha

                image.ColorSpace = image.ColorSpace == ColorSpace.Gray ? ColorSpace.Gray : ColorSpace.sRGB;
                image.Quality = (uint)quality;
                return image.ToByteArray(MagickFormat.Jpeg);
            }
        }
    }

    /// <summary>
    /// Re-assembles a scrambled page: pieces are copied from the original image onto a copy of it, then saved over the file.
    /// This is what modules reach as <c>ImageUnscrambler.New(args.Image)</c>.
    /// </summary>
    public sealed class ImageUnscrambler : IDisposable
    {
        private readonly string path;
        private readonly MagickImage source;
        private readonly MagickImage canvas;

        public ImageUnscrambler(string path)
        {
            this.path = path;
            source = new MagickImage(path);
            canvas = new MagickImage(source);
        }

        public int Width => (int)source.Width;

        public int Height => (int)source.Height;

        public void Copy(int sourceX, int sourceY, int width, int height, int destinationX, int destinationY)
        {
            if (width <= 0 || height <= 0)
                return;

            using (var piece = new MagickImage(source))
            {
                piece.Crop(new MagickGeometry(sourceX, sourceY, (uint)width, (uint)height));
                piece.ResetPage();
                canvas.Composite(piece, destinationX, destinationY, CompositeOperator.Copy);
            }
        }

        /// <summary>Writes the result over the original file in its own format (lossy formats at high quality).</summary>
        public void Save()
        {
            canvas.Quality = 95;
            var temp = path + ".unscrambling";

            try
            {
                canvas.Write(temp, source.Format);

                File.Delete(path);
                File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        public void Dispose()
        {
            source.Dispose();
            canvas.Dispose();
        }
    }
}
