using System;
using System.IO;
using MangaDownloader.Core;
using MangaDownloader.Lua.Runtime;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public class ComixTests
    {
        [Theory]
        [InlineData("gxj7x-the-perverted-virgin-public-morals-committee-members-secret-naughty-request", "")]
        [InlineData("55w9k-gibo-san-wa-boku-no-mono", "")]
        [InlineData("55w9k-gibo-san-wa-boku-no-mono", "<h3>Alternative One / Alternative Two</h3>")]
        public void OptionalAlternativeTitlesDoNotBreakMetadata(string slug, string alternativeTitles)
        {
            var url = "https://comix.to/title/" + slug;
            var http = new FixtureHttp();
            http.Pages[url] = "<h1>Example</h1>" + alternativeTitles +
                "<div class='description'>Summary</div><div>Authors:<a>Author</a></div>";
            var dir = Path.Combine(Path.GetTempPath(), "comix-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // A synced upstream scraper must not shadow the bundled fix.
                File.WriteAllText(Path.Combine(dir, "Comix.lua"),
                    "function Register() module.Name='Comix' module.Domains.Add('comix.to') end " +
                    "function GetInfo() error('outdated scraper') end");
                var match = ModuleRepository.LoadDirectory(dir, http).Find(url);
                Assert.NotNull(match);
                var info = new ModuleSession(match.File).GetInfo(url);
                Assert.Equal("Example", info.Title);
                Assert.Equal("Summary", info.Fields["Summary"]);
                Assert.Equal("Author", info.Fields["Author"]);
                if (alternativeTitles.Length > 0)
                {
                    Assert.Contains("Alternative One", info.Fields["AlternativeTitle"]);
                    Assert.Contains("Alternative Two", info.Fields["AlternativeTitle"]);
                }
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
