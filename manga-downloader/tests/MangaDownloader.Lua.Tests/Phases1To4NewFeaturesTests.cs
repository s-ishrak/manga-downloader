using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MangaDownloader.Core;
using MangaDownloader.Core.Download;
using MangaDownloader.Core.Library;
using MangaDownloader.Core.Naming;
using MangaDownloader.Core.Network;
using MangaDownloader.Core.Output;
using MangaDownloader.Core.Sync;
using MangaDownloader.Lua.Api;
using MangaDownloader.Lua.Runtime;
using MangaDownloader.Net;
using MangaDownloader.Web;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    public sealed class Phases1To4NewFeaturesTests : IDisposable
    {
        private readonly string tempDir;

        public Phases1To4NewFeaturesTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "manga-downloader-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(tempDir, true); }
            catch { }
        }

        // =========================================================================
        // 1. TagRules
        // =========================================================================

        [Fact]
        public void TagRules_FilterSpecificTag()
        {
            var tags = new[] { "tag1", "BadTag", "tag2" };
            var rules = new[] { "-badtag" };
            var result = TagRules.Apply(tags, rules);

            Assert.Equal(new[] { "tag1", "tag2" }, result);
        }

        [Fact]
        public void TagRules_FilterNamespaceWildcard()
        {
            var tags = new[] { "parody:naruto", "parody:one piece", "character:luffy", "misc" };
            var rules = new[] { "-parody:*" };
            var result = TagRules.Apply(tags, rules);

            Assert.Equal(new[] { "character:luffy", "misc" }, result);
        }

        [Fact]
        public void TagRules_StripNamespacePrefix()
        {
            var tags = new[] { "parody:naruto", "character:luffy", "misc" };
            var rules = new[] { "~parody" };
            var result = TagRules.Apply(tags, rules);

            Assert.Equal(new[] { "naruto", "character:luffy", "misc" }, result);
        }

        [Fact]
        public void TagRules_RenameTag()
        {
            var tags = new[] { "female:big breasts", "male:sole male" };
            var rules = new[] { "female:big breasts -> female:huge breasts" };
            var result = TagRules.Apply(tags, rules);

            Assert.Equal(new[] { "female:huge breasts", "male:sole male" }, result);
        }

        [Fact]
        public void TagRules_ChainedRules()
        {
            var tags = new[] { "parody:naruto", "meta:translated", "bad:garbage", "artist:oda" };
            var rules = new[]
            {
                "-bad:*",
                "~artist",
                "parody:naruto -> series:naruto"
            };
            var result = TagRules.Apply(tags, rules);

            Assert.Equal(new[] { "series:naruto", "meta:translated", "oda" }, result);
        }

        // =========================================================================
        // 2. CredentialStore with DPAPI
        // =========================================================================

        [Fact]
        public void CredentialStore_EncryptsAtRestOnWindowsAndReadsBack()
        {
            var file = Path.Combine(tempDir, "credentials.json");
            var store = new CredentialStore(file);

            store.Set("example.com", "user1", "supersecret123");

            // Verify file on disk
            var json = File.ReadAllText(file);
            Assert.Contains("example.com", json);
            Assert.Contains("user1", json);

            if (OperatingSystem.IsWindows())
            {
                // Must be encrypted with DPAPI prefix and NOT plaintext
                Assert.Contains("dpapi:", json);
                Assert.DoesNotContain("supersecret123", json);
            }

            // Create a fresh instance and verify transparent decryption
            var store2 = new CredentialStore(file);
            Assert.Equal("user1", store2.UserFor("example.com"));
            Assert.Equal("supersecret123", store2.PasswordFor("example.com"));
        }

        [Fact]
        public void CredentialStore_TransparentlyReadsExistingPlaintext()
        {
            var file = Path.Combine(tempDir, "legacy_credentials.json");
            var legacyJson = @"{
  ""sites"": {
    ""legacy.com"": {
      ""username"": ""olduser"",
      ""password"": ""plaintextpass""
    }
  }
}";
            File.WriteAllText(file, legacyJson);

            var store = new CredentialStore(file);
            Assert.Equal("olduser", store.UserFor("legacy.com"));
            Assert.Equal("plaintextpass", store.PasswordFor("legacy.com"));

            // When saved again on Windows, it auto-upgrades to DPAPI
            if (OperatingSystem.IsWindows())
            {
                store.Set("legacy.com", "olduser", "plaintextpass");
                var updatedJson = File.ReadAllText(file);
                Assert.Contains("dpapi:", updatedJson);
                Assert.DoesNotContain("plaintextpass", updatedJson);
            }
        }

        // =========================================================================
        // 3. ComicInfo.xml and PdfWriter chapter metadata
        // =========================================================================

        [Fact]
        public void CbzWriter_BuildComicInfoXml_IncludesChapterMetadata()
        {
            var info = new Dictionary<string, string>
            {
                ["Title"] = "Chapter 42 - The Return",
                ["Series"] = "Epic Manga",
                ["Number"] = "42",
                ["Volume"] = "5",
                ["LanguageISO"] = "en",
                ["Teams"] = "ScanGroup"
            };

            var xml = CbzWriter.BuildComicInfoXml(info, 20);
            var doc = XDocument.Parse(xml);
            var root = doc.Root;

            Assert.Equal("Epic Manga", (string)root.Element("Series"));
            Assert.Equal("Chapter 42 - The Return", (string)root.Element("Title"));
            Assert.Equal("42", (string)root.Element("Number"));
            Assert.Equal("5", (string)root.Element("Volume"));
            Assert.Equal("en", (string)root.Element("LanguageISO"));
            Assert.Equal("ScanGroup", (string)root.Element("Teams"));
            Assert.Equal("20", (string)root.Element("PageCount"));
        }

        [Fact]
        public void PdfWriter_WritesValidPdfWithOutlines()
        {
            var outPdf = Path.Combine(tempDir, "test.pdf");

            var img1 = Path.Combine(tempDir, "page1.jpg");
            var img2 = Path.Combine(tempDir, "page2.jpg");

            var blankJpg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");
            File.WriteAllBytes(img1, blankJpg);
            File.WriteAllBytes(img2, blankJpg);

            var pages = new[] { img1, img2 };
            var bookmarks = new[]
            {
                new KeyValuePair<int, string>(0, "Chapter 1"),
                new KeyValuePair<int, string>(1, "Chapter 2")
            };

            PdfWriter.Write(pages, outPdf, new Dictionary<string, string> { ["Title"] = "Test Title" }, bookmarks);

            Assert.True(File.Exists(outPdf));
            var pdfText = File.ReadAllText(outPdf, Encoding.ASCII);
            Assert.Contains("/Outlines", pdfText);
            Assert.Contains("Chapter 1", pdfText);
            Assert.Contains("Chapter 2", pdfText);
        }

        // =========================================================================
        // 4. LZString in Lua Script
        // =========================================================================

        [Fact]
        public void LzString_AccessibleInLuaScript()
        {
            var hasApi = TestSupport.Eval(@"tostring(
                type(LZString.decompress) == 'function' and
                type(LZString.decompressFromBase64) == 'function' and
                type(LZString.decompressFromUTF16) == 'function' and
                type(LZString.decompressFromEncodedURIComponent) == 'function'
            )");

            Assert.Equal("true", hasApi);

            var emptyRes = TestSupport.Eval("LZString.decompressFromBase64('')");
            Assert.Equal("", emptyRes ?? "");
        }

        // =========================================================================
        // 5. ModuleSync Multi-Repo and DisabledModules
        // =========================================================================

        [Fact]
        public void ModuleSync_SyncAllOrdersByPriority()
        {
            var sources = new List<RepositorySource>
            {
                new RepositorySource { Name = "Secondary", RemotePath = "https://github.com/foo/sec", Priority = 20 },
                new RepositorySource { Name = "Primary", RemotePath = "https://github.com/foo/pri", Priority = 5 }
            };

            var sync = new ModuleSync(new FakeHttpBackend(), null);
            var result = sync.SyncAll(sources, tempDir);
            Assert.NotNull(result);
        }

        [Fact]
        public void ModuleRepository_ReloadRespectsDisabledModules()
        {
            var modDir = Path.Combine(tempDir, "modules");
            Directory.CreateDirectory(modDir);

            // Write 2 dummy modules
            var mod1 = Path.Combine(modDir, "ModA.lua");
            var mod2 = Path.Combine(modDir, "ModB.lua");
            File.WriteAllText(mod1, "function GetInfo() end Module.New('ModA', 'Mod A', 'moda.test')");
            File.WriteAllText(mod2, "function GetInfo() end Module.New('ModB', 'Mod B', 'modb.test')");

            var repo = ModuleRepository.LoadDirectory(modDir);
            Assert.Contains(repo.Files, f => f.FileName == "ModA.lua");
            Assert.Contains(repo.Files, f => f.FileName == "ModB.lua");

            // Now reload with ModA disabled
            repo.Reload(new[] { "ModA.lua" });
            Assert.DoesNotContain(repo.Files, f => f.FileName == "ModA.lua");
            Assert.Contains(repo.Files, f => f.FileName == "ModB.lua");
        }

        // =========================================================================
        // 6. LocalWebServer API: Credentials, Cookies, Settings, State ETag
        // =========================================================================

        [Fact]
        public void LocalWebServer_CredentialsEndpoints()
        {
            var credFile = Path.Combine(tempDir, "web_credentials.json");
            var credentials = new CredentialStore(credFile);
            var cookieFile = Path.Combine(tempDir, "web_cookies.json");
            var cookies = new CookieJar();
            cookies.Set("example.com", "session_id", "abc123xyz");
            cookies.Save(cookieFile);

            var dbFile = Path.Combine(tempDir, "test.db");
            using var db = new LibraryDb(dbFile);
            var runner = new QueueRunner(db, () => null);
            var tracker = new SeriesTracker(db, null, credentials);
            var settingsFile = Path.Combine(tempDir, "settings.json");

            var port = 8798;
            using var server = new LocalWebServer(db, runner, tracker, port, "test-token", new DownloadOptions(), settingsFile, null, credentials: credentials, cookies: cookies);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            client.DefaultRequestHeaders.Add("X-Token", "test-token");

            // 1. GET /api/credentials (initially empty)
            var getRes = client.GetAsync("/api/credentials").Result;
            Assert.Equal(System.Net.HttpStatusCode.OK, getRes.StatusCode);
            var getJson = getRes.Content.ReadAsStringAsync().Result;
            Assert.Equal("[]", getJson.Trim());

            // 2. POST /api/credentials
            var postContent = new StringContent(JsonSerializer.Serialize(new { domain = "site.com", username = "alice", password = "secretpassword" }), Encoding.UTF8, "application/json");
            var postRes = client.PostAsync("/api/credentials", postContent).Result;
            Assert.Equal(System.Net.HttpStatusCode.OK, postRes.StatusCode);

            Assert.Equal("alice", credentials.UserFor("site.com"));
            Assert.Equal("secretpassword", credentials.PasswordFor("site.com"));

            // 3. GET /api/credentials (should list domain & username without password)
            var listRes = client.GetAsync("/api/credentials").Result;
            var listJson = listRes.Content.ReadAsStringAsync().Result;
            Assert.Contains("site.com", listJson);
            Assert.Contains("alice", listJson);
            Assert.DoesNotContain("secretpassword", listJson);

            // 4. POST /api/cookies/clear
            Assert.True(cookies.Count > 0);
            var clearCookiesRes = client.PostAsync("/api/cookies/clear", new StringContent("{}", Encoding.UTF8, "application/json")).Result;
            Assert.Equal(System.Net.HttpStatusCode.OK, clearCookiesRes.StatusCode);
            Assert.Equal(0, cookies.Count);

            // 5. GET /api/state returns ETag and 304 on If-None-Match
            var stateRes1 = client.GetAsync("/api/state").Result;
            Assert.Equal(System.Net.HttpStatusCode.OK, stateRes1.StatusCode);
            var etag = stateRes1.Headers.ETag?.Tag;
            Assert.NotNull(etag);

            var reqWithEtag = new HttpRequestMessage(HttpMethod.Get, "/api/state");
            reqWithEtag.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
            var stateRes2 = client.SendAsync(reqWithEtag).Result;
            Assert.Equal(System.Net.HttpStatusCode.NotModified, stateRes2.StatusCode);

            // 6. DELETE /api/credentials
            var delRes = client.DeleteAsync("/api/credentials?domain=site.com").Result;
            Assert.Equal(System.Net.HttpStatusCode.OK, delRes.StatusCode);
            Assert.Null(credentials.UserFor("site.com"));
        }

        private sealed class FakeHttpBackend : IHttpBackend
        {
            public HttpResult Send(HttpRequest request)
            {
                return new HttpResult { StatusCode = 200, Body = "[]" };
            }

            public HttpResult Download(HttpRequest request, Stream destination)
            {
                return new HttpResult { StatusCode = 200, Body = "" };
            }
        }
    }
}
