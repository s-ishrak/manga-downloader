using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace MangaDownloader.Core.Output
{
    /// <summary>Creates CBZ (zip) archives, optional ComicInfo.xml metadata entry, and chapter bookmark markers.</summary>
    public static class CbzWriter
    {
        public static void Create(string sourceDirectory, string archivePath, IDictionary<string, string> info = null, IList<KeyValuePair<int, string>> chapters = null) =>
            Create(ArchiveFiles.List(sourceDirectory).Select(f => new KeyValuePair<string, string>(f, Path.GetFileName(f))).ToList(), archivePath, info, chapters);

        public static void Create(IList<KeyValuePair<string, string>> entries, string archivePath, IDictionary<string, string> info = null,
            IList<KeyValuePair<int, string>> chapters = null, bool includeDirectories = false)
        {
            if (entries.Count == 0)
                throw new InvalidOperationException("No files to put in an archive.");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath)));
            var temp = archivePath + ".tmp";
            if (File.Exists(temp))
                File.Delete(temp);

            try
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    var addedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in entries)
                    {
                        var target = entry.Value.Replace('\\', '/');

                        if (includeDirectories)
                        {
                            var idx = 0;
                            while ((idx = target.IndexOf('/', idx)) >= 0)
                            {
                                var dir = target.Substring(0, idx + 1);
                                if (addedDirs.Add(dir))
                                    zip.CreateEntry(dir);
                                idx++;
                            }
                        }

                        zip.CreateEntryFromFile(entry.Key, target, CompressionLevel.Optimal);
                    }

                    if (info != null)
                    {
                        var xml = ComicInfo(info, entries.Count, chapters);
                        var meta = zip.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);

                        using var writer = new StreamWriter(meta.Open(), new UTF8Encoding(false));
                        writer.Write(xml);
                    }
                }

                if (File.Exists(archivePath))
                    File.Delete(archivePath);

                File.Move(temp, archivePath);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        public static string BuildComicInfoXml(IDictionary<string, string> info, int pageCount, IList<KeyValuePair<int, string>> chapters = null) =>
            ComicInfo(info, pageCount, chapters);

        /// <summary>ComicInfo.xml (Anansi schema) from the module's info fields.</summary>
        /// <param name="chapters">(first page index, name) per chapter; written as bookmarks, which readers such as Komga show as a table of contents.</param>
        public static string ComicInfo(IDictionary<string, string> info, int pageCount, IList<KeyValuePair<int, string>> chapters = null)
        {
            if (info == null) return null;
            string Get(params string[] keys) => keys.Select(k => info.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
            // A StringBuilder-backed XmlWriter declares UTF-16 even when Encoding is UTF-8.
            // Serialize to bytes so the declaration matches the UTF-8 ZIP entry.
            using var buffer = new MemoryStream();

            using (var w = XmlWriter.Create(buffer, settings))
            {
                w.WriteStartElement("ComicInfo");

                void Element(string name, string value)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                        w.WriteElementString(name, value);
                }

                var chapterTitle = Get("ChapterTitle");
                if (!string.IsNullOrWhiteSpace(chapterTitle))
                {
                    Element("Title", chapterTitle);
                    Element("Series", Get("Series", "Title"));
                }
                else
                {
                    Element("Title", Get("Title"));
                    Element("Series", Get("Series"));
                }

                Element("Number", Get("Chapter", "Number"));
                Element("Volume", Get("Volume"));
                Element("Summary", Get("Summary", "Description"));
                Element("Writer", Get("Author"));
                Element("Penciller", Get("Artist"));
                Element("Publisher", Get("Publisher", "Circle", "Group"));
                Element("Genre", Get("Genres", "Type", "Category"));
                Element("Tags", Get("Tags"));
                Element("Teams", Get("Scanlator", "Teams"));
                Element("Characters", Get("Characters"));
                Element("LanguageISO", Get("Language", "LanguageISO"));
                Element("Web", Get("Url"));
                Element("PageCount", pageCount.ToString());
                w.WriteElementString("Manga", "Yes");

                if (chapters != null && chapters.Count > 0)
                {
                    w.WriteStartElement("Pages");

                    foreach (var chapter in chapters)
                    {
                        w.WriteStartElement("Page");
                        w.WriteAttributeString("Image", chapter.Key.ToString());
                        w.WriteAttributeString("Bookmark", chapter.Value);
                        w.WriteEndElement();
                    }

                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
