using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using MangaDownloader.Core;
using MangaDownloader.Core.Download;
using MangaDownloader.Core.Output;
using MangaDownloader.Imaging;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    internal static class Pics
    {
        /// <summary>A width x height image whose left half is <paramref name="left"/> and right half <paramref name="right"/>.</summary>
        public static MagickImage TwoTone(MagickColor left, MagickColor right, uint width = 40, uint height = 20)
        {
            var image = new MagickImage(left, width, height);
            using (var half = new MagickImage(right, width / 2, height))
                image.Composite(half, (int)(width / 2), 0, CompositeOperator.Over);
            return image;
        }

        public static byte[] Bytes(MagickImage image, MagickFormat format)
        {
            image.Quality = 95;
            return image.ToByteArray(format);
        }

        /// <summary>Colour at a pixel as (R,G,B) in 0-255.</summary>
        public static (int R, int G, int B) Pixel(string path, int x, int y)
        {
            using (var image = new MagickImage(path))
            {
                var c = image.GetPixels().GetPixel(x, y).ToColor();
                return (c.R, c.G, c.B);
            }
        }

        public static bool IsMostly(string path, int x, int y, string channel)
        {
            var (r, g, b) = Pixel(path, x, y);
            switch (channel)
            {
                case "red": return r > 200 && g < 60 && b < 60;
                case "blue": return b > 200 && r < 60 && g < 60;
                case "white": return r > 240 && g > 240 && b > 240;
                default: throw new ArgumentException(channel);
            }
        }
    }

    public class ImageConversionTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));

        public ImageConversionTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        private string Save(byte[] bytes, string name)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        [Fact]
        public void ParsesConversionRules()
        {
            var rule = ImageConversionRule.Parse("webp,AVIF=jpeg:70");
            Assert.Equal(new[] { "webp", "avif" }, rule.From);
            Assert.Equal("jpg", rule.To);
            Assert.Equal(70, rule.Quality);
            Assert.True(rule.Matches(".WebP"));
            Assert.False(rule.Matches("png"));
            Assert.Equal(80, ImageConversionRule.Parse("webp=jpg").Quality);

            Assert.Throws<FormatException>(() => ImageConversionRule.Parse("webp"));
            Assert.Throws<FormatException>(() => ImageConversionRule.Parse("webp=bmp"));
            Assert.Throws<FormatException>(() => ImageConversionRule.Parse("webp=jpg:high"));
        }

        [Theory]
        [InlineData(MagickFormat.WebP, "webp")]
        [InlineData(MagickFormat.Avif, "avif")]
        public void ConvertsWebpAndAvifToJpegKeepingSizeAndColours(MagickFormat format, string ext)
        {
            var source = Save(Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), format), "page." + ext);
            Assert.Equal(ext, ImageSniffer.FromFile(source));

            var result = ImageConverter.Convert(source, new[] { ImageConversionRule.Parse("webp,avif=jpg:80") });

            Assert.Equal(Path.Combine(dir, "page.jpg"), result);
            Assert.Equal("jpg", ImageSniffer.FromFile(result));
            Assert.Equal((40, 20), ImageProbe.Size(result));
            Assert.True(Pics.IsMostly(result, 5, 10, "red"));
            Assert.True(Pics.IsMostly(result, 35, 10, "blue"));
            Assert.True(File.Exists(source), "the converter leaves the original for the caller to dispose of");
        }

        [Fact]
        public void TransparentPixelsBecomeWhiteInJpeg()
        {
            using (var image = new MagickImage(MagickColors.Transparent, 20, 20))
            {
                var source = Save(Pics.Bytes(image, MagickFormat.Png), "t.png");
                var result = ImageConverter.Convert(source, new[] { ImageConversionRule.Parse("png=jpg") });

                Assert.True(Pics.IsMostly(result, 10, 10, "white"));
            }
        }

        [Fact]
        public void LeavesUnmatchedAndAnimatedImagesAlone()
        {
            var png = Save(Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), MagickFormat.Png), "a.png");
            var rules = new[] { ImageConversionRule.Parse("webp,avif=jpg") };
            Assert.Equal(png, ImageConverter.Convert(png, rules));

            using (var frames = new MagickImageCollection())
            {
                frames.Add(new MagickImage(MagickColors.Red, 10, 10));
                frames.Add(new MagickImage(MagickColors.Blue, 10, 10));
                var gif = Path.Combine(dir, "anim.gif");
                frames.Write(gif);

                Assert.Equal(gif, ImageConverter.Convert(gif, new[] { ImageConversionRule.Parse("gif=jpg") }));
            }
        }

        [Fact]
        public void UnscramblerMovesPiecesAndSavesInPlace()
        {
            // Two 20x20 tiles, red | blue; swap them.
            var path = Save(Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), MagickFormat.Png), "s.png");

            using (var u = new ImageUnscrambler(path))
            {
                Assert.Equal((40, 20), (u.Width, u.Height));
                u.Copy(0, 0, 20, 20, 20, 0);
                u.Copy(20, 0, 20, 20, 0, 0);
                u.Save();
            }

            Assert.True(Pics.IsMostly(path, 5, 10, "blue"));
            Assert.True(Pics.IsMostly(path, 35, 10, "red"));
            Assert.Equal("png", ImageSniffer.FromFile(path));
            Assert.Empty(Directory.GetFiles(dir, "*.unscrambling"));
        }
    }

    public class PdfWriterTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));

        public PdfWriterTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        private string Save(byte[] bytes, string name)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>Walks the PDF the way a reader does: startxref -> xref table -> every object offset must point at "N 0 obj".</summary>
        private static string Validate(byte[] pdf, out int objectCount)
        {
            var text = Encoding.Latin1.GetString(pdf);
            Assert.StartsWith("%PDF-1.4", text);
            Assert.EndsWith("%%EOF\n", text);

            var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n").Groups[1].Value);
            Assert.StartsWith("xref\n", text.Substring(startxref));

            var header = Regex.Match(text.Substring(startxref), @"xref\n0 (\d+)\n");
            objectCount = int.Parse(header.Groups[1].Value);
            var entries = Regex.Matches(text.Substring(startxref + header.Length), @"(\d{10}) (\d{5}) ([nf]) \n").Cast<Match>().Take(objectCount).ToList();
            Assert.Equal(objectCount, entries.Count);
            Assert.Equal('f', entries[0].Groups[3].Value[0]);

            for (var id = 1; id < objectCount; id++)
                Assert.StartsWith($"{id} 0 obj\n", text.Substring(int.Parse(entries[id].Groups[1].Value)));

            Assert.Contains($"/Size {objectCount}", text);
            return text;
        }

        [Fact]
        public void EmbedsJpegsUntouchedAndConvertsOtherFormats()
        {
            var jpegBytes = Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue, 200, 100), MagickFormat.Jpeg);
            var jpeg = Save(jpegBytes, "1.jpg");
            var png = Save(Pics.Bytes(Pics.TwoTone(MagickColors.Blue, MagickColors.Red, 120, 80), MagickFormat.Png), "2.png");
            var webp = Save(Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Red, 60, 60), MagickFormat.WebP), "3.webp");
            var pdfPath = Path.Combine(dir, "out", "book.pdf");

            PdfWriter.Create(new[] { jpeg, png, webp }, pdfPath, new Dictionary<string, string> { ["Title"] = "My (Book)", ["Artist"] = "Álice" });

            var pdf = File.ReadAllBytes(pdfPath);
            var text = Validate(pdf, out var objects);

            Assert.Equal(4 + 3 * 3, objects);
            Assert.Contains("/Count 3", text);
            Assert.Equal(3, Regex.Matches(text, @"/Type /Page /Parent").Count);
            Assert.Contains("/MediaBox [0 0 150 75]", text);  // 200x100 px at 96 dpi
            Assert.Contains("/MediaBox [0 0 90 60]", text);
            Assert.Contains("/Filter /DCTDecode", text);
            Assert.Contains("/Title (My \\(Book\\))", text);
            Assert.Contains("/Author <FEFF00C1006C006900630065>", text); // non-ASCII -> UTF-16BE hex

            // The original JPEG bytes appear verbatim in the file (no recompression).
            Assert.True(IndexOf(pdf, jpegBytes) > 0);
            Assert.False(File.Exists(pdfPath + ".tmp"));
        }

        [Fact]
        public void ReportsEmptyInput()
        {
            Assert.Throws<InvalidOperationException>(() => PdfWriter.Create(new string[0], Path.Combine(dir, "x.pdf")));
        }

        [Fact]
        public void EncodesTextStrings()
        {
            Assert.Equal("(plain \\\\ \\( \\))", PdfWriter.TextString("plain \\ ( )"));
            Assert.Equal("<FEFF00E9>", PdfWriter.TextString("é"));
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j])
                    j++;

                if (j == needle.Length)
                    return i;
            }

            return -1;
        }
    }

    public class Phase3EngineTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));
        private readonly FixtureHttp http = new FixtureHttp();

        public Phase3EngineTests()
        {
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        private DownloadEngine Engine(string luaBody, DownloadOptions options)
        {
            var modules = Path.Combine(root, "modules");
            Directory.CreateDirectory(modules);
            File.WriteAllText(Path.Combine(modules, "t.lua"), "function Register() module.Name='T' module.Domains.Add('t.test') end\n" + luaBody);
            options.OutputDirectory = Path.Combine(root, "out");
            return new DownloadEngine(ModuleRepository.LoadDirectory(modules, http), http, options);
        }

        private const string Gallery =
            "function GetInfo() info.Title = 'Pics' info.Artist = 'Zed' end\n" +
            "function GetPages() pages.Add('https://img.test/1') pages.Add('https://img.test/2') end\n";

        private void Serve(string url, byte[] bytes) => http.Files[url] = bytes;

        private void Gallery2(MagickFormat format)
        {
            http.Pages["https://t.test/g"] = "<html></html>";
            Serve("https://img.test/1", Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), format));
            Serve("https://img.test/2", Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), format));
        }

        [Fact]
        public void ConvertsWebpPagesToJpegBeforePackingAndDropsTheOriginals()
        {
            Gallery2(MagickFormat.WebP);
            var options = new DownloadOptions();
            options.ConversionRules.Add(ImageConversionRule.Parse("webp,avif=jpg:80"));

            var report = Engine(Gallery, options).Download("https://t.test/g").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));

            using (var zip = ZipFile.OpenRead(report.Outputs.Single()))
                Assert.Equal(new[] { "Pics_001.jpg", "Pics_002.jpg" }, zip.Entries.Where(e => e.Name.EndsWith(".jpg") || e.Name.EndsWith(".webp")).Select(e => e.Name).OrderBy(n => n).ToArray());

            Assert.False(Directory.Exists(Path.Combine(root, "out", "Pics")));
        }

        [Fact]
        public void KeepImagesKeepsConvertedFilesAndTheirOriginals()
        {
            Gallery2(MagickFormat.Avif);
            var options = new DownloadOptions { SaveFormat = SaveFormat.Folder, KeepImages = true };
            options.ConversionRules.Add(ImageConversionRule.Parse("webp,avif=jpg"));

            var report = Engine(Gallery, options).Download("https://t.test/g").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            var folder = Path.Combine(root, "out", "Pics");
            Assert.Equal(new[] { "Pics_001.jpg", "Pics_002.jpg" }, Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n).ToArray());
            Assert.Equal(new[] { "Pics_001.avif", "Pics_002.avif" }, Directory.GetFiles(Path.Combine(folder, "_original")).Select(Path.GetFileName).OrderBy(n => n).ToArray());
            Assert.False(Directory.Exists(Path.Combine(folder, "_staging")));
        }

        [Fact]
        public void WithoutKeepImagesOriginalsAreDeletedEvenInFolderOutput()
        {
            Gallery2(MagickFormat.WebP);
            var options = new DownloadOptions { SaveFormat = SaveFormat.Folder };
            options.ConversionRules.Add(ImageConversionRule.Parse("webp=jpg"));

            Engine(Gallery, options).Download("https://t.test/g");

            var folder = Path.Combine(root, "out", "Pics");
            Assert.Equal(2, Directory.GetFiles(folder, "*.jpg").Length);
            Assert.Empty(Directory.GetFiles(folder, "*.webp", SearchOption.AllDirectories));
        }

        [Fact]
        public void AFileThatCannotBeConvertedIsKeptAsIs()
        {
            http.Pages["https://t.test/g"] = "<html></html>";
            // Starts like a WebP but is not decodable.
            Serve("https://img.test/1", Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPjunkjunkjunk"));
            Serve("https://img.test/2", Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), MagickFormat.WebP));
            var options = new DownloadOptions { SaveFormat = SaveFormat.Folder };
            options.ConversionRules.Add(ImageConversionRule.Parse("webp=jpg"));
            var log = new List<string>();

            var modules = Path.Combine(root, "modules");
            Directory.CreateDirectory(modules);
            File.WriteAllText(Path.Combine(modules, "t.lua"), "function Register() module.Name='T' module.Domains.Add('t.test') end\n" + Gallery);
            options.OutputDirectory = Path.Combine(root, "out");
            var report = new DownloadEngine(ModuleRepository.LoadDirectory(modules, http), http, options, log.Add).Download("https://t.test/g").Single();

            Assert.True(report.Success);
            var folder = Path.Combine(root, "out", "Pics");
            Assert.True(File.Exists(Path.Combine(folder, "Pics_001.webp")));
            Assert.True(File.Exists(Path.Combine(folder, "Pics_002.jpg")));
            Assert.Contains(log, l => l.Contains("could not convert Pics_001.webp"));
        }

        private const string UnscrambleModule =
            "function GetInfo() info.Title = 'Scr' end\n" +
            "function GetPages() local p = PageInfo.New('https://img.test/1') p.Data['swap'] = 'yes' pages.Add(p) end\n" +
            "function AfterDownloadPage()\n" +
            "  if isempty(page.Data['swap']) then return end\n" +
            "  local u = ImageUnscrambler.New(args.Image)\n" +
            "  local w = args.Image.Width / 2\n" +
            "  u.Copy(0, 0, w, args.Image.Height, w, 0)\n" +
            "  u.Copy(w, 0, w, args.Image.Height, 0, 0)\n" +
            "  u.Save()\n" +
            "end\n";

        [Fact]
        public void AfterDownloadPageUnscramblesTheImageBeforeItIsKept()
        {
            http.Pages["https://t.test/g"] = "<html></html>";
            Serve("https://img.test/1", Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), MagickFormat.Png));

            var report = Engine(UnscrambleModule, new DownloadOptions { SaveFormat = SaveFormat.Folder }).Download("https://t.test/g").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            var file = Path.Combine(root, "out", "Scr", "Scr_001.png");
            Assert.True(Pics.IsMostly(file, 5, 10, "blue"));
            Assert.True(Pics.IsMostly(file, 35, 10, "red"));
        }

        [Fact]
        public void PageDataSetInGetPagesReachesTheHookAndConversionRunsAfterIt()
        {
            http.Pages["https://t.test/g"] = "<html></html>";
            Serve("https://img.test/1", Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), MagickFormat.WebP));
            var options = new DownloadOptions { SaveFormat = SaveFormat.Folder };
            options.ConversionRules.Add(ImageConversionRule.Parse("webp=png"));

            var report = Engine(UnscrambleModule, options).Download("https://t.test/g").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            var file = Path.Combine(root, "out", "Scr", "Scr_001.png");
            Assert.True(Pics.IsMostly(file, 5, 10, "blue")); // unscrambled as webp, then converted
        }

        [Fact]
        public void AFailingHookFailsThePageAndLeavesNothingBehind()
        {
            http.Pages["https://t.test/g"] = "<html></html>";
            Serve("https://img.test/1", Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), MagickFormat.Png));
            Serve("https://img.test/2", Pics.Bytes(Pics.TwoTone(MagickColors.Red, MagickColors.Blue), MagickFormat.Png));
            var lua = "function GetInfo() info.Title = 'Bad' end\n" +
                      "function GetPages() pages.Add('https://img.test/1') pages.Add('https://img.test/2') end\n" +
                      "function AfterDownloadPage() if args.PageIndex == 0 then error('boom') end end\n";

            var report = Engine(lua, new DownloadOptions { SaveFormat = SaveFormat.Folder }).Download("https://t.test/g").Single();

            Assert.False(report.Success);
            Assert.Contains("page 1: AfterDownloadPage", report.Errors.Single());
            var folder = Path.Combine(root, "out", "Bad");
            Assert.Equal(new[] { "Bad_002.png" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray()); // page 1 never became "done"
            Assert.False(Directory.Exists(Path.Combine(folder, "_staging")));
        }

        [Fact]
        public void WritesAPdfWithOnePagePerImage()
        {
            Gallery2(MagickFormat.WebP);

            var report = Engine(Gallery, new DownloadOptions { SaveFormat = SaveFormat.Pdf }).Download("https://t.test/g").Single();

            Assert.True(report.Success, string.Join("; ", report.Errors));
            var pdf = report.Outputs.Single();
            Assert.EndsWith("Pics.pdf", pdf);
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
            Assert.Contains("/Count 2", text);
            Assert.Contains("/Title (Pics)", text);
            Assert.Contains("/Author (Zed)", text);
            Assert.False(Directory.Exists(Path.Combine(root, "out", "Pics")));
        }
    }
}
