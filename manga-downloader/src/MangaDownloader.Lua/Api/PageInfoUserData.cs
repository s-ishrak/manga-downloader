using System;
using System.Linq;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    [MoonSharpUserData]
    public class PageInfoUserData
    {
        private string url;

        public PageInfoUserData()
        {
        }

        public PageInfoUserData(string url)
        {
            this.url = url;
        }

        public static PageInfoUserData New() => new PageInfoUserData();

        public static PageInfoUserData New(string url) => new PageInfoUserData(url);

        public object Url { get => url; set => url = LuaConv.Str(value); }

        public string Referer { get; set; }

        public ListUserData BackupUrls { get; set; } = new ListUserData();

        public string FileNameHint { get; set; }

        /// <summary>Alias of <see cref="FileNameHint"/> (both spellings exist in modules).</summary>
        public string FilenameHint { get => FileNameHint; set => FileNameHint = value; }

        public string FileExtensionHint { get; set; }

        public string DirectoryPath { get; set; }

        public string ThumbnailUrl { get; set; }

        public string Title { get; set; }

        public bool ExtractContents { get; set; }

        public DictUserData Data { get; } = new DictUserData();

        public override string ToString() => url ?? "";
    }

    [MoonSharpUserData]
    public class PageListUserData : ListUserData
    {
        public string Referer { get; set; }

        public DictUserData Headers { get; } = new DictUserData();

        public override void Add(object item)
        {
            if (item is PageInfoUserData page)
                Items.Add(page);
            else if (item is ListUserData nested)
                AddRange(nested);
            else if (item != null)
                Items.Add(new PageInfoUserData(LuaConv.Str(item)));
        }

        private static bool Matches(object existing, object item) =>
            Equals(existing, item) || (existing is PageInfoUserData p && item is string s && p.Url as string == s);

        public bool Remove(object item)
        {
            var found = Items.FirstOrDefault(i => Matches(i, item));
            return found != null && Items.Remove(found);
        }

        public override bool Contains(object item) => Items.Any(i => Matches(i, item));

        public override void Sort() => Items.Sort((a, b) => string.CompareOrdinal(LuaConv.Str(((PageInfoUserData)a).Url), LuaConv.Str(((PageInfoUserData)b).Url)));

        public override string ToString() => string.Join(Environment.NewLine, Items.Select(i => i.ToString()));
    }

    [MoonSharpUserData]
    public class ChapterListUserData : ListUserData
    {
        public override void Add(object item)
        {
            if (item is ChapterInfoUserData chapter)
                Items.Add(chapter);
            else if (item is ListUserData nested)
                AddRange(nested);
            else if (item != null)
                Items.Add(new ChapterInfoUserData { Url = LuaConv.Str(item) });
        }

        /// <summary><c>chapters.Add(url, title)</c></summary>
        public override void Add(object url, object title)
        {
            Items.Add(new ChapterInfoUserData { Url = url, Title = title });
        }

        public override void Sort() => Items.Sort((a, b) => string.CompareOrdinal(LuaConv.Str(((ChapterInfoUserData)a).Title), LuaConv.Str(((ChapterInfoUserData)b).Title)));

        public override string ToString() => string.Join(Environment.NewLine, Items.Select(i => i.ToString()));
    }
}

namespace MangaDownloader.Lua.Api
{
    [MoonSharp.Interpreter.MoonSharpUserData]
    public class BeforeDownloadPageArgsUserData
    {
        public int PageIndex { get; set; }

        public string FilePath { get; set; }
    }
}
