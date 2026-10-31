using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace MangaDownloader.Core.Output
{
    public static class CbzWriter
    {
        /// <summary>
        /// Zips the image files of <paramref name="sourceFolder"/> (in natural name order, stored uncompressed since images
        /// are already compressed) plus an optional ComicInfo.xml. Written to a temp file and moved into place, so a crash never leaves a half-written archive.
        /// </summary>
        public static void Create(string sourceFolder, string archivePath, IDictionary<string, string> info)
        {
            Create(ArchiveFiles.List(sourceFolder).Select(f => new KeyValuePair<string, string>(f, Path.GetFileName(f))).ToList(), archivePath, info);
        }

        /// <summary>Same, from explicit (file, entry name) pairs in archive order, so several chapter folders can share one archive.</summary>
        public static void Create(IList<KeyValuePair<string, string>> files, string archivePath, IDictionary<string, string> info,
            IList<KeyValuePair<int, string>> chapters = null)
        {
            var temp = archivePath + ".tmp";

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath)));

            if (File.Exists(temp))
                File.Delete(temp);

            try
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    foreach (var file in files)
                        zip.CreateEntryFromFile(file.Key, file.Value, CompressionLevel.NoCompression);

                    if (info != null)
                    {
                        var entry = zip.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);

                        using (var stream = entry.Open())
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                            writer.Write(ComicInfo(info, files.Count, chapters));
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

        /// <summary>ComicInfo.xml (Anansi schema) from the module's info fields.</summary>
        /// <param name="chapters">(first page index, name) per chapter; written as bookmarks, which readers such as Komga show as a table of contents.</param>
        public static string ComicInfo(IDictionary<string, string> info, int pageCount, IList<KeyValuePair<int, string>> chapters = null)
        {
            string Get(params string[] keys) => keys.Select(k => info.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
            var sb = new StringBuilder();

            using (var w = XmlWriter.Create(sb, settings))
            {
                w.WriteStartElement("ComicInfo");

                void Element(string name, string value)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                        w.WriteElementString(name, value);
                }

                Element("Title", Get("Title"));
                Element("Series", Get("Series"));
                Element("Summary", Get("Summary", "Description"));
                Element("Writer", Get("Author"));
                Element("Penciller", Get("Artist"));
                Element("Publisher", Get("Publisher", "Circle", "Group"));
                Element("Genre", Get("Genres", "Type", "Category"));
                Element("Tags", Get("Tags"));
                Element("Characters", Get("Characters"));
                Element("LanguageISO", Get("Language"));
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

            return sb.ToString();
        }
    }
}
