using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    /// <summary>The <c>info</c> global. String-ish properties accept lists/numbers and store them joined (see LuaConv.Str).</summary>
    [MoonSharpUserData]
    public class MangaInfoUserData
    {
        public bool Ignore { get; set; }
        public bool Adult { get; set; }

        private string _title;
        private string _alternativeTitle;
        private string _originalTitle;
        private string _artist;
        private string _author;
        private string _circle;
        private string _group;
        private string _parody;
        private string _characters;
        private string _tags;
        private string _genres;
        private string _language;
        private string _type;
        private string _category;
        private string _series;
        private string _magazine;
        private string _publisher;
        private string _convention;
        private string _scanlator;
        private string _translator;
        private string _uploader;
        private string _summary;
        private string _description;
        private string _status;
        private string _dateReleased;
        private string _published;
        private string _pageCount;
        private string _chapterCount;
        private string _readingDirection;
        private string _communityRating;
        private string _thumbnailUrl;
        private string _source;
        private string _url;
        private string _id;
        public object Title { get => _title; set => _title = LuaConv.Str(value); }
        public object AlternativeTitle { get => _alternativeTitle; set => _alternativeTitle = LuaConv.Str(value); }
        public object OriginalTitle { get => _originalTitle; set => _originalTitle = LuaConv.Str(value); }
        public object Artist { get => _artist; set => _artist = LuaConv.Str(value); }
        public object Author { get => _author; set => _author = LuaConv.Str(value); }
        public object Circle { get => _circle; set => _circle = LuaConv.Str(value); }
        public object Group { get => _group; set => _group = LuaConv.Str(value); }
        public object Parody { get => _parody; set => _parody = LuaConv.Str(value); }
        public object Characters { get => _characters; set => _characters = LuaConv.Str(value); }
        public object Tags { get => _tags; set => _tags = LuaConv.Str(value); }
        public object Genres { get => _genres; set => _genres = LuaConv.Str(value); }
        public object Language { get => _language; set => _language = LuaConv.Str(value); }
        public object Type { get => _type; set => _type = LuaConv.Str(value); }
        public object Category { get => _category; set => _category = LuaConv.Str(value); }
        public object Series { get => _series; set => _series = LuaConv.Str(value); }
        public object Magazine { get => _magazine; set => _magazine = LuaConv.Str(value); }
        public object Publisher { get => _publisher; set => _publisher = LuaConv.Str(value); }
        public object Convention { get => _convention; set => _convention = LuaConv.Str(value); }
        public object Scanlator { get => _scanlator; set => _scanlator = LuaConv.Str(value); }
        public object Translator { get => _translator; set => _translator = LuaConv.Str(value); }
        public object Uploader { get => _uploader; set => _uploader = LuaConv.Str(value); }
        public object Summary { get => _summary; set => _summary = LuaConv.Str(value); }
        public object Description { get => _description; set => _description = LuaConv.Str(value); }
        public object Status { get => _status; set => _status = LuaConv.Str(value); }
        public object DateReleased { get => _dateReleased; set => _dateReleased = LuaConv.Str(value); }
        public object Published { get => _published; set => _published = LuaConv.Str(value); }
        public object PageCount { get => _pageCount; set => _pageCount = LuaConv.Str(value); }
        public object ChapterCount { get => _chapterCount; set => _chapterCount = LuaConv.Str(value); }
        public object ReadingDirection { get => _readingDirection; set => _readingDirection = LuaConv.Str(value); }
        public object CommunityRating { get => _communityRating; set => _communityRating = LuaConv.Str(value); }
        public object ThumbnailUrl { get => _thumbnailUrl; set => _thumbnailUrl = LuaConv.Str(value); }
        public object Source { get => _source; set => _source = LuaConv.Str(value); }
        public object Url { get => _url; set => _url = LuaConv.Str(value); }
        public object Id { get => _id; set => _id = LuaConv.Str(value); }

        public static readonly string[] PropertyNames =
        {
            "Title",
            "AlternativeTitle",
            "OriginalTitle",
            "Artist",
            "Author",
            "Circle",
            "Group",
            "Parody",
            "Characters",
            "Tags",
            "Genres",
            "Language",
            "Type",
            "Category",
            "Series",
            "Magazine",
            "Publisher",
            "Convention",
            "Scanlator",
            "Translator",
            "Uploader",
            "Summary",
            "Description",
            "Status",
            "DateReleased",
            "Published",
            "PageCount",
            "ChapterCount",
            "ReadingDirection",
            "CommunityRating",
            "ThumbnailUrl",
            "Source",
            "Url",
            "Id",
        };

        public IDictionary<string, string> ToDictionary()
        {
            var result = new Dictionary<string, string>();
            foreach (var name in PropertyNames)
            {
                var value = LuaConv.Str(GetType().GetProperty(name).GetValue(this));
                if (!string.IsNullOrEmpty(value))
                    result[name] = value;
            }
            return result;
        }

        public override string ToString() => Title as string ?? "";
    }
}
