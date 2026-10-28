using System;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MangaDownloader.Core.Download
{
    /// <summary>
    /// When a module lists reader pages instead of image files, finds the actual image in the page:
    /// a well-known image element first, then og:image, then the largest declared &lt;img&gt;.
    /// </summary>
    public static class ReaderPage
    {
        public static string FindImageUrl(string html, string pageUrl)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html ?? "");

            string Attr(string xpath, params string[] names)
            {
                var node = doc.DocumentNode.SelectSingleNode(xpath);
                return names.Select(n => node?.GetAttributeValue(n, null)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            }

            var url =
                Attr("//img[@id='image' or @id='img' or @id='main-img' or @id='mainImage' or contains(@class,'main-image') or contains(@class,'page-image')]", "src", "data-src")
                ?? Attr("//meta[@property='og:image']", "content")
                ?? LargestImage(doc);

            return url == null ? null : Resolve(HtmlEntity.DeEntitize(url), pageUrl);
        }

        private static string LargestImage(HtmlDocument doc)
        {
            var images = doc.DocumentNode.SelectNodes("//img[@src or @data-src]");

            if (images == null)
                return null;

            long Area(HtmlNode n) =>
                (long)n.GetAttributeValue("width", 0) * n.GetAttributeValue("height", 0);

            var best = images.OrderByDescending(Area).First();
            return best.GetAttributeValue("src", null) ?? best.GetAttributeValue("data-src", null);
        }

        private static string Resolve(string url, string pageUrl)
        {
            if (url.StartsWith("//"))
                return new Uri(pageUrl).Scheme + ":" + url;

            return Uri.TryCreate(new Uri(pageUrl), url, out var combined) ? combined.AbsoluteUri : url;
        }
    }
}
