using System.Linq;
using MangaDownloader.Lua.Runtime;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    /// <summary>Conformance tests for the host semantics the modules rely on (plan section 1.3).</summary>
    public class LuaApiTests
    {
        private const string Html =
            "<html><head><title>Hello &amp; bye</title></head><body>" +
            "<h1> Big Title </h1>" +
            "<ul class='tags'><li><a href='/t/a'>alpha</a></li><li><a href='/t/b'>beta</a></li><li><a href='/t/c'>gamma</a></li></ul>" +
            "<span class='pages'>Pages: 24</span>" +
            "<a class='next' href='/page2'>next</a>" +
            "</body></html>";

        [Fact]
        public void DotAndColonCallsAreEquivalent()
        {
            Assert.Equal("Big Title", TestSupport.Eval("dom.SelectValue('//h1'):trim()", Html));
            Assert.Equal("Big Title", TestSupport.Eval("dom:SelectValue('//h1'):trim()", Html));
        }

        [Fact]
        public void ListsAreZeroIndexedAndIterable()
        {
            Assert.Equal("alpha", TestSupport.Eval("dom.SelectValues('//ul[@class=\"tags\"]//a')[0]", Html));
            Assert.Equal("gamma", TestSupport.Eval("dom.SelectValues('//ul[@class=\"tags\"]//a')[2]", Html));
            Assert.Equal("alpha|beta|gamma|", TestSupport.Eval(
                "(function() local o = '' for t in dom.SelectValues('//ul[@class=\"tags\"]//a') do o = o .. t .. '|' end return o end)()", Html));
            Assert.Equal("3", TestSupport.Eval("tostring(dom.SelectValues('//ul[@class=\"tags\"]//a').Count())", Html));
            Assert.Equal("3", TestSupport.Eval("tostring(#dom.SelectValues('//ul[@class=\"tags\"]//a'))", Html));
        }

        [Fact]
        public void AttributesTextAndEntitiesAreSelected()
        {
            Assert.Equal("/page2", TestSupport.Eval("dom.SelectValue('//a[@class=\"next\"]/@href')", Html));
            Assert.Equal("Hello & bye", TestSupport.Eval("dom.Title", Html));
            Assert.Equal("beta", TestSupport.Eval("dom.SelectValue('//a[@href=\"/t/b\"]/text()[1]')", Html));
        }

        [Fact]
        public void MissingValuesAreNilAndIsEmptyHandlesThem()
        {
            Assert.Equal("true", TestSupport.Eval("tostring(isempty(dom.SelectValue('//nope')))", Html));
            Assert.Equal("true", TestSupport.Eval("tostring(isempty(dom.SelectValues('//nope')))", Html));
            Assert.Equal("true", TestSupport.Eval("tostring(isempty(''))", Html));
            Assert.Equal("false", TestSupport.Eval("tostring(isempty('x'))", Html));
        }

        [Fact]
        public void NodesSupportRelativeXPathAndAttributes()
        {
            Assert.Equal("beta", TestSupport.Eval("dom.SelectElements('//ul/li')[1].SelectValue('a')", Html));
            Assert.Equal("/t/c", TestSupport.Eval("dom.SelectElements('//ul/li')[2].SelectValue('a/@href')", Html));
            Assert.Equal("next", TestSupport.Eval("dom.SelectElement('//a[@class=\"next\"]').GetAttribute('class')", Html));
        }

        [Fact]
        public void StringExtensionsMatchGsemacTextSemantics()
        {
            Assert.Equal("24", TestSupport.Eval("('Pages: 24'):after(':'):trim()"));
            Assert.Equal("Pages", TestSupport.Eval("('Pages: 24'):before(':')"));
            Assert.Equal("a-b", TestSupport.Eval("('a-b-c'):beforelast('-')"));
            Assert.Equal("c", TestSupport.Eval("('a-b-c'):afterlast('-')"));
            Assert.Equal("mid", TestSupport.Eval("('x[mid]y'):between('[', ']')"));
            Assert.Equal("no-sep", TestSupport.Eval("('no-sep'):after('#')")); // not found -> unchanged
            Assert.Equal("true", TestSupport.Eval("tostring(('/g/123/'):contains('/g/'))"));
            Assert.Equal("123", TestSupport.Eval("('/g/123/'):regex('/g/(\\\\d+)', 1)"));
            Assert.Equal("123", TestSupport.Eval("('/g/123/'):regex('/g/(\\\\d+)')")); // first group by default
            Assert.Equal("b, c", TestSupport.Eval("('a,b,c'):split(','):join(', '):after('a, ')"));
            Assert.Equal("Hello World", TestSupport.Eval("('hello world'):title()"));
            Assert.Equal("x/y", TestSupport.Eval("('/x/y/'):trim('/')"));
            Assert.Equal("1|2|3", TestSupport.Eval("('a1b2c3'):regexmany('(\\\\d)', 1):join('|')"));
        }

        [Fact]
        public void ListsAssignedToInfoFieldsAreJoined()
        {
            var http = new FixtureHttp();
            http.Pages["https://test.example/p"] = Html;

            var module = TestSupport.LoadInline(
                "function Register() module.Name='T' module.Domains.Add('test.example') end\n" +
                "function GetInfo()\n info.Tags = dom.SelectValues('//ul[@class=\"tags\"]//a')\n info.PageCount = tonumber(dom.SelectValue('//span[@class=\"pages\"]'):after(':'))\n info.Adult = true\nend\n", http);

            var info = new ModuleSession(module).GetInfo("https://test.example/p");

            Assert.Equal("alpha, beta, gamma", info.Fields["Tags"]);
            Assert.Equal("24", info.Fields["PageCount"]);
        }

        [Fact]
        public void JsonSupportsJsonPathKeysAndIndexing()
        {
            const string json = "{\"a\":{\"b\":[1,2,3]},\"name\":\"x\",\"map\":{\"k1\":\"v1\",\"k2\":\"v2\"}}";
            Assert.Equal("2", TestSupport.Eval("Json.New('" + json + "').SelectValue('a.b[1]')"));
            Assert.Equal("x", TestSupport.Eval("Json.New('" + json + "')['name'].Value"));
            Assert.Equal("k1,k2", TestSupport.Eval("(function() local o = {} for k in Json.New('" + json + "')['map'].Keys do o[#o+1] = k end return table.concat(o, ',') end)()"));
            Assert.Equal("3", TestSupport.Eval("tostring(Json.New('" + json + "')['a']['b'].Count())"));
            Assert.Equal("1,2,3", TestSupport.Eval("Json.New('" + json + "').SelectValues('a.b[*]'):join(',')"));
        }

        [Fact]
        public void FreeFunctionsBehave()
        {
            Assert.Equal("https://x.test", TestSupport.Eval("GetRoot('https://x.test/a/b?c=1')"));
            Assert.Equal("https://x.test/img.jpg", TestSupport.Eval("GetRooted('/img.jpg', 'https://x.test/a/b')"));
            Assert.Equal("https://i.test/a/0.jpg", TestSupport.Eval("FormatString('//{0}.test/{1}/{2}.jpg', 'i', 'a', 0):gsub('^//', 'https://')"));
            Assert.Equal("5", TestSupport.Eval("GetParameter('https://x.test/?p=5&q=1', 'p')"));
            Assert.Equal("https://x.test/?p=7", TestSupport.Eval("SetParameter('https://x.test/?p=5', 'p', 7)"));
            Assert.Equal("https://x.test/a", TestSupport.Eval("StripParameters('https://x.test/a?x=1')"));
            Assert.Equal("hello", TestSupport.Eval("DecodeBase64('aGVsbG8=')"));
            Assert.Equal("a-b", TestSupport.Eval("RegexReplace('a_b', '_', '-')"));
            Assert.Equal("true", TestSupport.Eval("tostring(toboolean('true') and not toboolean('false'))"));
        }

        [Fact]
        public void EnqueueAndIgnoreProduceListResults()
        {
            var http = new FixtureHttp();
            http.Pages["https://test.example/list"] = "<a class='g' href='/g/1'>1</a><a class='g' href='/g/2'>2</a>";

            var module = TestSupport.LoadInline(
                "function Register() module.Name='T' module.Domains.Add('test.example') end\n" +
                "function GetInfo()\n for u in dom.SelectValues('//a[@class=\"g\"]/@href') do Enqueue(u) end\n info.Ignore = true\nend\n", http);

            var info = new ModuleSession(module).GetInfo("https://test.example/list");

            Assert.True(info.Ignore);
            Assert.Equal(new[] { "https://test.example/g/1", "https://test.example/g/2" }, info.Enqueued.ToArray());
        }

        [Fact]
        public void PagesAcceptStringsAndPageInfoAndResolveRelativeUrls()
        {
            var http = new FixtureHttp();
            http.Pages["https://test.example/g/1"] = "<html></html>";

            var module = TestSupport.LoadInline(
                "function Register() module.Name='T' module.Domains.Add('test.example') end\n" +
                "function GetPages()\n pages.Add('/img/1.jpg')\n local p = PageInfo.New('https://cdn.test/2.png')\n p.Referer = 'https://ref.test/'\n p.BackupUrls.Add('https://b.test/2.png')\n pages.Add(p)\nend\n", http);

            var pages = new ModuleSession(module).GetPages("https://test.example/g/1");

            Assert.Equal(2, pages.Count);
            Assert.Equal("https://test.example/img/1.jpg", pages[0].Url);
            Assert.Equal("https://cdn.test/2.png", pages[1].Url);
            Assert.Equal("https://ref.test/", pages[1].Referer);
            Assert.Equal(new[] { "https://b.test/2.png" }, pages[1].BackupUrls.ToArray());
        }

        [Fact]
        public void ChaptersSupportAddReverseAndChapterInfo()
        {
            var http = new FixtureHttp();
            http.Pages["https://test.example/s"] = "<html></html>";

            var module = TestSupport.LoadInline(
                "function Register() module.Name='T' module.Domains.Add('test.example') end\n" +
                "function GetChapters()\n chapters.Add('/c/1', 'One')\n local c = ChapterInfo.New()\n c.Url = '/c/2'\n c.Title = 'Two'\n chapters.Add(c)\n chapters.Reverse()\nend\n", http);

            var chapters = new ModuleSession(module).GetChapters("https://test.example/s");

            Assert.Equal(new[] { "Two", "One" }, chapters.Select(c => c.Title).ToArray());
            Assert.Equal("https://test.example/c/1", chapters[1].Url);
        }

        [Fact]
        public void PaginatorFollowsNextLinksUntilTheyRunOut()
        {
            var http = new FixtureHttp();
            http.Pages["https://test.example/1"] = "<a class='n' href='/2'>n</a><b>one</b>";
            http.Pages["https://test.example/2"] = "<a class='n' href='/3'>n</a><b>two</b>";
            http.Pages["https://test.example/3"] = "<b>three</b>";

            var module = TestSupport.LoadInline(
                "function Register() module.Name='T' module.Domains.Add('test.example') end\n" +
                "function GetInfo()\n local t = {}\n for p in Paginator.New(http, dom, '//a[@class=\"n\"]/@href') do t[#t+1] = p.SelectValue('//b') end\n info.Title = table.concat(t, ',')\nend\n", http);

            Assert.Equal("one,two,three", new ModuleSession(module).GetInfo("https://test.example/1").Title);
        }

        [Fact]
        public void MultipleModulesPerFileAndDomainMatching()
        {
            var module = TestSupport.LoadInline(
                "function Register()\n module.Name = 'Base'\n module.Domains.Add('base.test')\n" +
                " module = Module.New()\n module.Name = 'Second'\n module.Domains.Add('a.second.test', 'A')\n module.Domains.Add('second.test', 'Second')\n RegisterModule(module)\nend\n");

            Assert.Equal(new[] { "Base", "Second" }, module.Modules.Select(m => m.GetName()).ToArray());

            var match = module.FindModule("https://www.a.second.test/x", out var domain);
            Assert.Equal("Second", match.GetName());
            Assert.Equal("a.second.test", domain); // most specific domain wins
            Assert.Null(module.FindModule("https://other.test/", out _));
        }

        [Fact]
        public void FailRaisesModuleFailedWithTheErrorCode()
        {
            var http = new FixtureHttp();
            http.Pages["https://test.example/x"] = "<html></html>";

            var module = TestSupport.LoadInline(
                "function Register() module.Name='T' module.Domains.Add('test.example') end\nfunction GetInfo() Fail(Error.LoginRequired) end\n", http);

            var e = Assert.Throws<ModuleFailedException>(() => new ModuleSession(module).GetInfo("https://test.example/x"));
            Assert.Equal("LoginRequired", e.Reason);
        }

        [Fact]
        public void SettingsAreDeclaredAndReadable()
        {
            var http = new FixtureHttp();
            http.Pages["https://test.example/x"] = "<html></html>";

            var module = TestSupport.LoadInline(
                "function Register()\n module.Name='T'\n module.Domains.Add('test.example')\n module.Settings.AddCheck('Flag', true).WithToolTip('tip')\n module.Settings.AddChoice('Lang', 'en', {'en','fr'}).WithMultiSelect()\nend\n" +
                "function GetInfo() info.Title = tostring(toboolean(module.Settings['Flag'])) .. '/' .. module.Settings['Lang'] end\n", http);

            Assert.Equal("true/en", new ModuleSession(module).GetInfo("https://test.example/x").Title);
            Assert.Equal(2, module.Modules[0].Settings.Count());
        }
    }
}
