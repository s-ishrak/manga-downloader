using System;
using System.Collections.Generic;
using System.Linq;
using MangaDownloader.Lua.Api;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Runtime
{
    /// <summary>Supplies stored site credentials to <c>Login()</c>. Implementations must not log the password.</summary>
    public interface ICredentialProvider
    {
        bool TryGet(string domain, string moduleName, out string username, out string password);
    }

    public sealed class InfoResult
    {
        public IDictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();

        /// <summary>True for list/search pages: nothing to download, but <see cref="Enqueued"/> holds the galleries found.</summary>
        public bool Ignore { get; set; }

        public IList<string> Enqueued { get; set; } = new List<string>();

        public string Title => Fields.TryGetValue("Title", out var t) ? t : null;
    }

    public sealed class ChapterResult
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string Chapter { get; set; }
        public string Volume { get; set; }
        public string Language { get; set; }
        public string Scanlator { get; set; }
    }

    public sealed class PageResult
    {
        public string Url { get; set; }
        public string Referer { get; set; }
        public IList<string> BackupUrls { get; set; } = new List<string>();
        public string FileNameHint { get; set; }
        public string FileExtensionHint { get; set; }
        public IDictionary<string, object> Data { get; set; } = new Dictionary<string, object>();

        /// <summary>Request headers the module asked for (<c>pages.Headers</c> plus <c>http.Headers</c>).</summary>
        public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>A module's <c>module.RateLimits.Add(pattern, requests, ms)</c> rule: at most N requests per window to URLs containing the pattern.</summary>
    public sealed class RateLimitRule
    {
        public string Pattern { get; set; }
        public int Requests { get; set; }
        public int WindowMilliseconds { get; set; }
    }

    /// <summary>
    /// Runs a module's entry points (<c>GetInfo</c>, <c>GetChapters</c>, <c>GetPages</c>) for one URL, the way the
    /// download pipeline does: fetch the URL, expose <c>url</c>/<c>doc</c>/<c>dom</c>, call the function, read the result objects.
    /// </summary>
    public sealed class ModuleSession
    {
        private readonly LuaModule module;
        private readonly Dictionary<string, string> pageCache = new Dictionary<string, string>();

        private readonly ICredentialProvider credentials;
        private readonly HashSet<string> loggedInDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ModuleSession(LuaModule module, ICredentialProvider credentials = null)
        {
            this.module = module;
            this.credentials = credentials;
        }

        /// <summary>
        /// Runs <paramref name="action"/> after logging in (when the module has <c>Login()</c> and credentials are stored), and once more
        /// after a login if the module fails with <c>LoginRequired</c>. Fails with the original error when there is nothing to log in with.
        /// </summary>
        private T WithLogin<T>(string url, Func<T> action)
        {
            lock (module.SyncRoot)
            {
                TryLogin(url);

                try
                {
                    return action();
                }
                catch (ModuleFailedException e) when (e.Reason == "LoginRequired" && TryLogin(url))
                {
                    return action();
                }
            }
        }

        private bool TryLogin(string url)
        {
            var matched = module.FindModule(url, out var domain);

            if (matched == null || credentials == null || loggedInDomains.Contains(domain))
                return false;

            if (module.Script.Globals.Get("Login").Type != DataType.Function)
                return false;

            if (!credentials.TryGet(domain, matched.GetName(), out var username, out var password))
                return false;

            Prepare(url, out _);
            loggedInDomains.Add(domain); // one attempt per session, successful or not

            Script.Globals["username"] = username;
            Script.Globals["password"] = password;

            try
            {
                CallIfDefined("Login");
            }
            finally
            {
                Script.Globals["username"] = DynValue.Nil;
                Script.Globals["password"] = DynValue.Nil;
            }

            pageCache.Clear(); // pages fetched before logging in may differ
            return true;
        }

        private Script Script => module.Script;

        public InfoResult GetInfo(string url) => WithLogin(url, () => GetInfoCore(url));

        private InfoResult GetInfoCore(string url)
        {
            Prepare(url, out _);
            var info = new MangaInfoUserData();
            Script.Globals["info"] = info;
            module.Context.Enqueued.Clear();

            CallIfDefined("GetInfo");

            info.Url = info.Url ?? url;

            return new InfoResult
            {
                Fields = info.ToDictionary(),
                Ignore = info.Ignore,
                Enqueued = module.Context.Enqueued.ToList(),
            };
        }

        /// <summary>Runs <c>GetChapters</c> against <paramref name="url"/>; empty when the module has no such function (single-chapter works).</summary>
        public IList<ChapterResult> GetChapters(string url) => WithLogin(url, () => GetChaptersCore(url));

        private IList<ChapterResult> GetChaptersCore(string url)
        {
            Prepare(url, out _);
            var chapters = new ChapterListUserData();
            Script.Globals["chapters"] = chapters;

            CallIfDefined("GetChapters");

            return chapters.OfType<ChapterInfoUserData>().Select(c => new ChapterResult
            {
                Url = Absolute(c.Url as string, url),
                Title = c.Title as string,
                Chapter = c.Chapter as string,
                Volume = c.Volume as string,
                Language = c.Language as string,
                Scanlator = c.Scanlator as string,
            }).ToList();
        }

        public IList<PageResult> GetPages(string url) => WithLogin(url, () => GetPagesCore(url));

        private IList<PageResult> GetPagesCore(string url)
        {
            Prepare(url, out _);
            var pages = new PageListUserData();
            Script.Globals["pages"] = pages;

            CallIfDefined("GetPages");

            // A module may have navigated elsewhere (it reassigns the url global); relative image URLs resolve against that.
            // (the url global may also be relative, e.g. HentaiFox sets it to the reader link).
            var baseUrl = Urls.GetRooted(Script.Globals.Get("url").CastToString() ?? url, url);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in ((HttpUserData)Script.Globals.Get("http").UserData.Object).Headers.Entries)
                headers[h.Key] = LuaConv.Str(h.Value);
            foreach (var h in pages.Headers.Entries)
                headers[h.Key] = LuaConv.Str(h.Value);

            return pages.OfType<PageInfoUserData>().Select(p => new PageResult
            {
                Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
                Url = Absolute(p.Url as string, baseUrl),
                Referer = p.Referer ?? pages.Referer ?? url,
                BackupUrls = p.BackupUrls.Select(LuaConv.Str).ToList(),
                FileNameHint = p.FileNameHint,
                FileExtensionHint = p.FileExtensionHint,
                Data = p.Data.Entries.ToDictionary(e => e.Key, e => e.Value),
            }).ToList();
        }

        /// <summary>The module's rate-limit rules (domain or path fragment, requests per window).</summary>
        public IList<RateLimitRule> RateLimits(string url)
        {
            lock (module.SyncRoot)
                return RateLimitsUnlocked(url);
        }

        private IList<RateLimitRule> RateLimitsUnlocked(string url)
        {
            var matched = module.FindModule(url, out _);
            return matched == null ? new List<RateLimitRule>() : matched.RateLimits.Rules
                .Select(r => new RateLimitRule { Pattern = r.Domain, Requests = r.Requests, WindowMilliseconds = r.Milliseconds }).ToList();
        }

        /// <summary>Cookies the module/host want sent with a request to <paramref name="url"/>.</summary>
        public IDictionary<string, string> CookiesFor(string url)
        {
            lock (module.SyncRoot)
                return CookiesForUnlocked(url);
        }

        private IDictionary<string, string> CookiesForUnlocked(string url)
        {
            var result = new Dictionary<string, string>();
            var matched = module.FindModule(url, out _);

            if (matched != null)
                foreach (var c in matched.Cookies.Entries)
                    result[c.Key] = LuaConv.Str(c.Value);

            foreach (var c in module.Context.CookiesFor(new Uri(url).Host))
                result[c.Key] = c.Value;

            return result;
        }

        /// <summary>
        /// Runs the module's <c>BeforeDownloadPage</c> (if any) for one page and returns the page with any changes
        /// (<c>page.Url</c>, <c>page.Referer</c>). Module scripts are single-threaded, so callers must not overlap calls.
        /// </summary>
        public PageResult BeforeDownloadPage(PageResult page, int pageIndex, string pageUrlContext)
        {
            lock (module.SyncRoot)
                return BeforeDownloadPageUnlocked(page, pageIndex, pageUrlContext);
        }

        private PageResult BeforeDownloadPageUnlocked(PageResult page, int pageIndex, string pageUrlContext)
        {
            var f = Script.Globals.Get("BeforeDownloadPage");

            if (f.Type != DataType.Function)
                return page;

            var matched = module.FindModule(pageUrlContext, out var domain);

            if (matched != null)
            {
                matched.Domain = domain;
                Script.Globals["module"] = matched;
                module.Context.CurrentModule = matched;
            }

            module.Context.CurrentUrl = pageUrlContext;

            var info = new PageInfoUserData(page.Url) { Referer = page.Referer, FileExtensionHint = page.FileExtensionHint, FileNameHint = page.FileNameHint };
            foreach (var d in page.Data)
                info.Data[d.Key] = d.Value;

            Script.Globals["page"] = info;
            Script.Globals["args"] = new BeforeDownloadPageArgsUserData { PageIndex = pageIndex };

            CallIfDefined("BeforeDownloadPage");

            return new PageResult
            {
                Url = Absolute(info.Url as string, pageUrlContext),
                Referer = info.Referer ?? page.Referer,
                BackupUrls = page.BackupUrls,
                FileNameHint = info.FileNameHint,
                FileExtensionHint = info.FileExtensionHint,
                Data = info.Data.Entries.ToDictionary(e => e.Key, e => e.Value),
                Headers = page.Headers,
            };
        }

        /// <summary>
        /// Runs the module's <c>AfterDownloadPage</c> (if any) on the downloaded file, e.g. to unscramble it in place.
        /// <paramref name="page"/> must be the page returned by <see cref="BeforeDownloadPage"/> so <c>page.Data</c> carries over.
        /// Returns false when the module has no such function. Callers must not overlap calls.
        /// </summary>
        public bool AfterDownloadPage(PageResult page, int pageIndex, string filePath, string pageUrlContext)
        {
            lock (module.SyncRoot)
                return AfterDownloadPageUnlocked(page, pageIndex, filePath, pageUrlContext);
        }

        private bool AfterDownloadPageUnlocked(PageResult page, int pageIndex, string filePath, string pageUrlContext)
        {
            var f = Script.Globals.Get("AfterDownloadPage");

            if (f.Type != DataType.Function)
                return false;

            var matched = module.FindModule(pageUrlContext, out var domain);

            if (matched != null)
            {
                matched.Domain = domain;
                Script.Globals["module"] = matched;
                module.Context.CurrentModule = matched;
            }

            module.Context.CurrentUrl = pageUrlContext;

            var info = new PageInfoUserData(page.Url) { Referer = page.Referer, FileExtensionHint = page.FileExtensionHint, FileNameHint = page.FileNameHint };
            foreach (var d in page.Data)
                info.Data[d.Key] = d.Value;

            Script.Globals["page"] = info;
            Script.Globals["args"] = new AfterDownloadPageArgsUserData { Image = new ImageUserData(filePath), PageIndex = pageIndex };

            CallIfDefined("AfterDownloadPage");
            return true;
        }

        private void Prepare(string url, out string domain)
        {
            var matched = module.FindModule(url, out domain);

            if (matched == null)
                throw new ModuleFailedException("DomainNotSupported");

            matched.Domain = domain;
            module.Context.CurrentModule = matched;
            module.Context.CurrentUrl = url;

            Script.Globals["module"] = matched;
            Script.Globals["url"] = url;

            // GetInfo, GetChapters and GetPages all start from the same page; fetch it once per session.
            string html = "";

            if (!matched.DeferHttpRequests && !pageCache.TryGetValue(url, out html))
                pageCache[url] = html = module.Context.Http.Send(new Net.HttpRequest { Url = url }).Body;
            Script.Globals["doc"] = html;
            Script.Globals["dom"] = new HtmlDocumentUserData(html, url);
        }

        private void CallIfDefined(string function)
        {
            var f = Script.Globals.Get(function);

            if (f.Type != DataType.Function)
                return;

            try
            {
                Script.Call(f);
            }
            catch (InterpreterException e)
            {
                // Fail(...) surfaces as ModuleFailedException wrapped by MoonSharp.
                for (Exception inner = e; inner != null; inner = inner.InnerException)
                    if (inner is ModuleFailedException failed)
                        throw failed;

                throw new InvalidOperationException(function + "(): " + (e.DecoratedMessage ?? e.Message), e);
            }
        }

        private static string Absolute(string value, string baseUrl) => value == null ? null : Urls.GetRooted(value, baseUrl);
    }
}
