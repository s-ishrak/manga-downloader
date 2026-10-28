using System;
using MangaDownloader.Imaging;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    /// <summary><c>args.Image</c> in <c>AfterDownloadPage</c>: the downloaded file and its size.</summary>
    [MoonSharpUserData]
    public class ImageUserData
    {
        private int? width;
        private int? height;

        public ImageUserData(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }

        public int Width => width ?? Probe().Width;

        public int Height => height ?? Probe().Height;

        private (int Width, int Height) Probe()
        {
            var size = ImageProbe.Size(FilePath);
            width = size.Width;
            height = size.Height;
            return size;
        }

        public override string ToString() => FilePath;
    }

    [MoonSharpUserData]
    public class AfterDownloadPageArgsUserData
    {
        public ImageUserData Image { get; set; }

        public int PageIndex { get; set; }

        public string FilePath => Image?.FilePath;
    }

    /// <summary><c>ImageUnscrambler.New(args.Image)</c>, then <c>Copy(srcX, srcY, w, h, destX, destY)</c> per piece and <c>Save()</c>.</summary>
    [MoonSharpUserData]
    public class ImageUnscramblerUserData : IDisposable
    {
        private readonly ImageUnscrambler inner;

        private ImageUnscramblerUserData(string path)
        {
            inner = new ImageUnscrambler(path);
        }

        public static ImageUnscramblerUserData New(ImageUserData image) => new ImageUnscramblerUserData(image.FilePath);

        public void Copy(double sourceX, double sourceY, double width, double height, double destinationX, double destinationY) =>
            inner.Copy(R(sourceX), R(sourceY), R(width), R(height), R(destinationX), R(destinationY));

        public void Save()
        {
            inner.Save();
            Dispose();
        }

        public void Dispose() => inner.Dispose();

        private static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    }
}
