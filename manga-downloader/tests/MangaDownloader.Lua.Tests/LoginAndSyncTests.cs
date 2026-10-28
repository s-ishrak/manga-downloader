using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MangaDownloader.Core;
using MangaDownloader.Core.Download;
using MangaDownloader.Core.Network;
using MangaDownloader.Core.Sync;
using MangaDownloader.Lua.Runtime;
using MangaDownloader.Net;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    internal sealed class FakeCredentials : ICredentialProvider
    {
        public Dictionary<string, (string, string)> Sites { get; } = new Dictionary<string, (string, string)>();

        public bool TryGet(string domain, string moduleName, out string username, out string password)
        {
            if (Sites.TryGetValue(domain, out var c))
            {
                (username, password) = c;
                return true;
            }

            username = password = null;
            return false;
        }
    }

    public class LoginTests
    {
        // Pages are only visible after Login() set the cookie; Login posts the stored credentials.
        private const string Module =
            "function Register() module.Name='Secure' module.Domains.Add('secure.test') end\n" +
            "function Login()\n" +
            "  http.PostData.Add('user', username) http.PostData.Add('pass', password)\n" +
            "  local r = http.PostResponse('https://secure.test/login')\n" +
            "  if not r.Cookies.Contains('sid') then Fail(Error.LoginFailed) end\n" +
            "  global.SetCookies(r.Cookies)\n" +
            "end\n" +
            "function GetInfo()\n" +
            "  if dom.SelectValue('//h1') == 'Please log in' then Fail(Error.LoginRequired) end\n" +
            "  info.Title = dom.SelectValue('//h1')\n" +
            "end\n";

        private static ScriptedHttp Site(List<string> posts) => new ScriptedHttp(req =>
        {
            if (req.Method == "POST")
            {
                posts.Add(req.Body);
                var ok = req.Body == "user=alice&pass=s3cret";
                var r = new HttpResult { StatusCode = 200, Body = "" };

                if (ok)
                    r.SetCookies.Add(SetCookieParser.Parse("sid=good", new Uri(req.Url)));

                return r;
            }

            var loggedIn = req.Cookies.TryGetValue("sid", out var sid) && sid == "good";
            return new HttpResult { StatusCode = 200, Body = loggedIn ? "<h1>Members only gallery</h1>" : "<h1>Please log in</h1>" };
        });

        private static (ModuleSession session, ScriptedHttp http, List<string> posts) Setup(FakeCredentials creds)
        {
            var posts = new List<string>();
            var jar = new CookieJar();
            var http = Site(posts);
            var stack = new CookieBackend(http, jar);
            var module = TestSupport.LoadInline(Module, stack, jar);
            return (new ModuleSession(module, creds), http, posts);
        }

        [Fact]
        public void LogsInWithStoredCredentialsBeforeFetchingTheGallery()
        {
            var creds = new FakeCredentials();
            creds.Sites["secure.test"] = ("alice", "s3cret");
            var (session, _, posts) = Setup(creds);

            var info = session.GetInfo("https://secure.test/g/1");

            Assert.Equal("Members only gallery", info.Title);
            Assert.Equal(new[] { "user=alice&pass=s3cret" }, posts.ToArray());
        }

        [Fact]
        public void LogsInOncePerSession()
        {
            var creds = new FakeCredentials();
            creds.Sites["secure.test"] = ("alice", "s3cret");
            var (session, _, posts) = Setup(creds);

            session.GetInfo("https://secure.test/g/1");
            session.GetInfo("https://secure.test/g/2");
            session.GetPages("https://secure.test/g/2");

            Assert.Single(posts);
        }

        [Fact]
        public void WithoutCredentialsLoginRequiredPropagates()
        {
            var (session, _, posts) = Setup(new FakeCredentials());

            var e = Assert.Throws<ModuleFailedException>(() => session.GetInfo("https://secure.test/g/1"));

            Assert.Equal("LoginRequired", e.Reason);
            Assert.Empty(posts);
        }

        [Fact]
        public void WrongCredentialsSurfaceLoginFailedAndAreNotRetried()
        {
            var creds = new FakeCredentials();
            creds.Sites["secure.test"] = ("alice", "wrong");
            var (session, _, posts) = Setup(creds);

            var e = Assert.Throws<ModuleFailedException>(() => session.GetInfo("https://secure.test/g/1"));

            Assert.Equal("LoginFailed", e.Reason);
            Assert.Single(posts);
        }

        [Fact]
        public void CredentialsAreNotLeftInScriptGlobals()
        {
            var creds = new FakeCredentials();
            creds.Sites["secure.test"] = ("alice", "s3cret");
            var posts = new List<string>();
            var jar = new CookieJar();
            var module = TestSupport.LoadInline(Module + "function GetPages() pages.Add(tostring(password) .. '|' .. tostring(username)) end\n",
                new CookieBackend(Site(posts), jar), jar);

            var pages = new ModuleSession(module, creds).GetPages("https://secure.test/g/1");

            Assert.Contains("nil", pages.Single().Url);
            Assert.DoesNotContain("s3cret", pages.Single().Url);
        }

        [Fact]
        public void CredentialStoreRoundTripsAndFallsBackToParentDomains()
        {
            var file = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"), "credentials.json");
            var store = new CredentialStore(file);
            store.Set("Example.com", "alice", "pw");

            var reloaded = new CredentialStore(file);
            Assert.True(reloaded.TryGet("m.example.com", "x", out var user, out var pass));
            Assert.Equal(("alice", "pw"), (user, pass));
            Assert.False(reloaded.TryGet("example.org", "x", out _, out _));
            Assert.False(reloaded.TryGet("com", "x", out _, out _));
            Assert.Equal("alice", reloaded.UserFor("example.com"));

            Assert.True(reloaded.Remove("example.com"));
            Assert.False(new CredentialStore(file).TryGet("example.com", "x", out _, out _));
        }

        [Fact]
        public void GlobalSetCookieFeedsTheSharedJar()
        {
            var jar = new CookieJar();
            var module = TestSupport.LoadInline(
                "function Register() module.Name='C' module.Domains.Add('c.test') global.SetCookie('c.test', 'age_check', 'yes') end\n", jar: jar);

            Assert.Equal("yes", jar.CookiesFor(new Uri("https://c.test/x"))["age_check"]);
            Assert.NotNull(module);
        }
    }

    public class SandboxTests
    {
        [Fact]
        public void FileLoadingFunctionsAreRemovedAndRequireOnlyTakesPlainNames()
        {
            Assert.Equal("nil", TestSupport.Eval("tostring(dofile)"));
            Assert.Equal("nil", TestSupport.Eval("tostring(loadfile)"));
            Assert.Throws<InvalidOperationException>(() => TestSupport.Eval("tostring(require('../secret'))"));
            Assert.Throws<InvalidOperationException>(() => TestSupport.Eval("tostring(require('C:\\\\windows\\\\x'))"));
        }
    }

    public class ModuleSyncTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        private static string Raw(string name) => "https://raw.githubusercontent.com/o/r/master/modules/lua/" + name;

        /// <summary>A fake GitHub: contents listing for the folder plus raw file bodies.</summary>
        private sealed class FakeGitHub
        {
            public Dictionary<string, string> Files = new Dictionary<string, string>();
            public Dictionary<string, string> ListingShaOverride = new Dictionary<string, string>();
            public List<string> Downloaded = new List<string>();
            public int Status = 200;

            public IHttpBackend Backend => new ScriptedHttp(req =>
            {
                if (req.Url.StartsWith("https://api.github.com/repos/o/r/contents/modules/lua"))
                {
                    if (Status != 200)
                        return new HttpResult { StatusCode = Status, Body = "{}" };

                    var items = Files.Select(f => "{\"name\":\"" + f.Key + "\",\"type\":\"file\",\"sha\":\"" +
                        (ListingShaOverride.TryGetValue(f.Key, out var s) ? s : ModuleSync.GitBlobSha(Encoding.UTF8.GetBytes(f.Value))) +
                        "\",\"download_url\":\"" + Raw(f.Key) + "\"}");
                    return new HttpResult { StatusCode = 200, Body = "[" + string.Join(",", items) + "]" };
                }

                var name = req.Url.Substring(req.Url.LastIndexOf('/') + 1);
                Downloaded.Add(name);
                return Files.TryGetValue(name, out var body) ? new HttpResult { StatusCode = 200, Body = body } : new HttpResult { StatusCode = 404 };
            });
        }

        private static readonly GitHubLocation Source = GitHubLocation.Parse("https://github.com/o/r/tree/master/modules/lua/");

        [Fact]
        public void ParsesRepositoryUrlsAndRepositoriesJson()
        {
            var l = GitHubLocation.Parse("https://github.com/HDoujinDownloader/HDoujin-Downloader/tree/master/modules/lua/");
            Assert.Equal(("HDoujinDownloader", "HDoujin-Downloader", "master", "modules/lua"), (l.Owner, l.Repo, l.Ref, l.Path));
            Assert.Equal("https://api.github.com/repos/HDoujinDownloader/HDoujin-Downloader/contents/modules/lua?ref=master", l.ContentsApiUrl);
            Assert.Throws<FormatException>(() => GitHubLocation.Parse("https://evil.test/o/r/tree/master/x"));

            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "repositories.json");
            File.WriteAllText(file, "[{\"name\":\"Internal\",\"remotePath\":null,\"priority\":1,\"settings\":{\"synchronizeAutomatically\":true}}," +
                                    "{\"name\":\"Official\",\"remotePath\":\"https://github.com/o/r/tree/master/modules/lua/\",\"priority\":0,\"disabledModules\":[\"X.lua\"],\"settings\":{\"synchronizeAutomatically\":false}}]");
            var sources = RepositorySource.Load(file);

            var official = Assert.Single(sources); // Internal has no remote
            Assert.Equal("Official", official.Name);
            Assert.False(official.SynchronizeAutomatically);
            Assert.Equal(new[] { "X.lua" }, official.DisabledModules.ToArray());
        }

        [Fact]
        public void DownloadsNewFilesThenOnlyWhatChanged()
        {
            var gh = new FakeGitHub();
            gh.Files["A.lua"] = "function Register() end -- a";
            gh.Files["B.lua"] = "function Register() end -- b";
            var sync = new ModuleSync(gh.Backend);

            var first = sync.Sync(Source, dir);
            Assert.Equal(new[] { "A.lua", "B.lua" }, first.Added.OrderBy(n => n).ToArray());
            Assert.Equal("function Register() end -- a", File.ReadAllText(Path.Combine(dir, "A.lua")));

            gh.Downloaded.Clear();
            var second = sync.Sync(Source, dir);
            Assert.False(second.Changed);
            Assert.Equal(2, second.Unchanged);
            Assert.Empty(gh.Downloaded);

            gh.Files["B.lua"] = "function Register() end -- b v2";
            gh.Files["C.lua"] = "-- c";
            var third = sync.Sync(Source, dir);
            Assert.Equal(new[] { "B.lua" }, third.Updated.ToArray());
            Assert.Equal(new[] { "C.lua" }, third.Added.ToArray());
            Assert.Equal(new[] { "B.lua", "C.lua" }, gh.Downloaded.OrderBy(n => n).ToArray());
        }

        [Fact]
        public void RemovesOnlyFilesItInstalledAndKeepsUserFiles()
        {
            var gh = new FakeGitHub();
            gh.Files["A.lua"] = "-- a";
            gh.Files["Old.lua"] = "-- old";
            var sync = new ModuleSync(gh.Backend);
            sync.Sync(Source, dir);
            File.WriteAllText(Path.Combine(dir, "Mine.lua"), "-- user module");

            gh.Files.Remove("Old.lua");
            var result = sync.Sync(Source, dir);

            Assert.Equal(new[] { "Old.lua" }, result.Removed.ToArray());
            Assert.False(File.Exists(Path.Combine(dir, "Old.lua")));
            Assert.True(File.Exists(Path.Combine(dir, "Mine.lua")));
        }

        [Fact]
        public void RepairsLocallyModifiedFiles()
        {
            var gh = new FakeGitHub();
            gh.Files["A.lua"] = "-- a";
            var sync = new ModuleSync(gh.Backend);
            sync.Sync(Source, dir);
            File.WriteAllText(Path.Combine(dir, "A.lua"), "-- tampered");

            var result = sync.Sync(Source, dir);

            Assert.Equal(new[] { "A.lua" }, result.Updated.ToArray());
            Assert.Equal("-- a", File.ReadAllText(Path.Combine(dir, "A.lua")));
        }

        [Fact]
        public void RejectsDownloadsThatDoNotMatchTheirChecksum()
        {
            var gh = new FakeGitHub();
            gh.Files["A.lua"] = "-- served content";
            gh.ListingShaOverride["A.lua"] = new string('0', 40);

            var result = new ModuleSync(gh.Backend).Sync(Source, dir);

            Assert.Empty(result.Added);
            Assert.Contains("checksum mismatch", result.Failed.Single());
            Assert.False(File.Exists(Path.Combine(dir, "A.lua")));
            Assert.Empty(Directory.GetFiles(dir, "*.download"));
        }

        [Fact]
        public void IgnoresUnsafeNamesNonLuaFilesAndForeignHosts()
        {
            var backend = new ScriptedHttp(req => new HttpResult
            {
                StatusCode = 200,
                Body = "[" +
                       "{\"name\":\"..\\\\evil.lua\",\"type\":\"file\",\"sha\":\"1\",\"download_url\":\"" + Raw("x") + "\"}," +
                       "{\"name\":\"notes.txt\",\"type\":\"file\",\"sha\":\"1\",\"download_url\":\"" + Raw("notes.txt") + "\"}," +
                       "{\"name\":\"sub\",\"type\":\"dir\",\"sha\":\"1\",\"download_url\":null}," +
                       "{\"name\":\"Foreign.lua\",\"type\":\"file\",\"sha\":\"1\",\"download_url\":\"https://evil.test/Foreign.lua\"}" +
                       "]",
            });

            var result = new ModuleSync(backend).Sync(Source, dir);

            Assert.Empty(result.Added);
            Assert.Empty(result.Failed);
            Assert.Empty(Directory.GetFiles(dir, "*.lua"));
            Assert.Single(backend.Requests); // only the listing; nothing was downloaded
        }

        [Fact]
        public void ReportsRateLimitingClearly()
        {
            var gh = new FakeGitHub { Status = 403 };
            var e = Assert.Throws<IOException>(() => new ModuleSync(gh.Backend).Sync(Source, dir));
            Assert.Contains("rate limit", e.Message);
        }

        [Fact]
        public void SyncedModulesLoadAndMatchUrls()
        {
            var gh = new FakeGitHub();
            gh.Files["Demo.lua"] = "function Register() module.Name='Demo' module.Domains.Add('demo.test') end";
            new ModuleSync(gh.Backend).Sync(Source, dir);

            var repository = ModuleRepository.LoadDirectory(dir);

            Assert.Equal("Demo", repository.Find("https://demo.test/g/1").Module.GetName());
        }
    }
}
