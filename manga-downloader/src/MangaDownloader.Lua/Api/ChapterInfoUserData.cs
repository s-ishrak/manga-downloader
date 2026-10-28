using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    [MoonSharpUserData]
    public class ChapterInfoUserData
    {
        private string _url;
        private string _title;
        private string _chapter;
        private string _volume;
        private string _language;
        private string _scanlator;
        private string _scanlationGroup;
        private string _translator;
        private string _uploader;
        private string _subtitle;
        private string _version;
        private string _thumbnailUrl;
        public object Url { get => _url; set => _url = LuaConv.Str(value); }
        public object Title { get => _title; set => _title = LuaConv.Str(value); }
        public object Chapter { get => _chapter; set => _chapter = LuaConv.Str(value); }
        public object Volume { get => _volume; set => _volume = LuaConv.Str(value); }
        public object Language { get => _language; set => _language = LuaConv.Str(value); }
        public object Scanlator { get => _scanlator; set => _scanlator = LuaConv.Str(value); }
        public object ScanlationGroup { get => _scanlationGroup; set => _scanlationGroup = LuaConv.Str(value); }
        public object Translator { get => _translator; set => _translator = LuaConv.Str(value); }
        public object Uploader { get => _uploader; set => _uploader = LuaConv.Str(value); }
        public object Subtitle { get => _subtitle; set => _subtitle = LuaConv.Str(value); }
        public object Version { get => _version; set => _version = LuaConv.Str(value); }
        public object ThumbnailUrl { get => _thumbnailUrl; set => _thumbnailUrl = LuaConv.Str(value); }

        public static ChapterInfoUserData New() => new ChapterInfoUserData();

        public override string ToString() => Title as string ?? Url as string ?? "";
    }
}
