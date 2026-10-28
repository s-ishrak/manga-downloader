using System;
using System.Collections.Generic;
using MangaDownloader.Lua.Api;
using MangaDownloader.Net;

namespace MangaDownloader.Lua.Runtime
{
    /// <summary>Everything a running module can reach through its globals: network, settings, cookies, the enqueue sink.</summary>
    public sealed class HostContext
    {
        public HostContext(IHttpBackend http, CookieJar jar = null)
        {
            Http = http;
            Jar = jar ?? new CookieJar();
        }

        /// <summary>The cookie jar shared by every module and request (persisted by the host application).</summary>
        public CookieJar Jar { get; }

        public IHttpBackend Http { get; }

        /// <summary>App-wide settings read through <c>global.GetSetting</c> (e.g. "Downloads.MaxScrapingDepth").</summary>
        public IDictionary<string, string> Settings { get; } = new Dictionary<string, string>();

        /// <summary>URLs a module asked to download next (<c>Enqueue(url)</c>).</summary>
        public List<string> Enqueued { get; } = new List<string>();

        /// <summary>Every module object created by the script (<c>module</c> plus each <c>Module.New()</c>).</summary>
        public List<ModuleUserData> Modules { get; } = new List<ModuleUserData>();

        public Action<string> Log { get; set; } = _ => { };

        /// <summary>URL currently being processed; the base for <c>GetRooted</c> and relative requests.</summary>
        public string CurrentUrl { get; set; }

        public ModuleUserData CurrentModule { get; set; }

        public void SetCookie(string domain, string name, string value) => Jar.Set(domain, name, value);

        public IDictionary<string, string> CookiesFor(string host) => Jar.CookiesFor(host);
    }

    /// <summary>Raised by <c>Fail(...)</c>; carries the Error code or message.</summary>
    public sealed class ModuleFailedException : Exception
    {
        public ModuleFailedException(string reason) : base(reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }
}
