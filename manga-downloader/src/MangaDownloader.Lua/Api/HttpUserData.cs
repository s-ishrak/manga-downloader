using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using MangaDownloader.Lua.Runtime;
using MangaDownloader.Net;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    [MoonSharpUserData]
    public class HttpResponseUserData
    {
        private readonly HttpResult result;

        public HttpResponseUserData(HttpResult result)
        {
            this.result = result;
            Headers = new DictUserData();
            Cookies = new DictUserData();
            Data = new DictUserData();

            foreach (var h in result.Headers)
                Headers[h.Key] = h.Value;

            foreach (var c in result.Cookies)
                Cookies[c.Key] = c.Value;

            foreach (var c in result.SetCookies)
                Cookies[c.Name] = c.Value;
        }

        public string Body => result.Body;

        /// <summary>Same text as <see cref="Body"/> (kept for modules that read <c>response.Document</c>).</summary>
        public string Document => result.Body;

        public int StatusCode => result.StatusCode;

        public string Url => result.Url;

        public DictUserData Headers { get; set; }

        public DictUserData Cookies { get; set; }

        public DictUserData Data { get; }

        public override string ToString() => result.Body;
    }

    [MoonSharpUserData]
    public class HttpUserData
    {
        private readonly HostContext context;

        public HttpUserData(HostContext context)
        {
            this.context = context;
        }

        public DictUserData Headers { get; set; } = new DictUserData();

        public DictUserData Cookies { get; set; } = new DictUserData();

        public DictUserData PostData { get; } = new DictUserData();

        public string Referer { get; set; }

        public bool AllowAutoRedirect { get; set; } = true;

        /// <summary>A fresh client (no headers, cookies or POST data), as <c>Http.New()</c> / <c>http.New()</c>.</summary>
        public HttpUserData New() => new HttpUserData(context);

        public string Get(string url) => GetResponse(url).Body;

        public HttpResponseUserData GetResponse(string url) => Send("GET", url, null);

        public string Post(string url) => PostResponse(url).Body;

        public string Post(string url, string data) => PostResponse(url, data).Body;

        public HttpResponseUserData PostResponse(string url) => PostResponse(url, null);

        /// <summary>POSTs <paramref name="data"/>, or the accumulated <c>PostData</c> form fields (which are then cleared).</summary>
        public HttpResponseUserData PostResponse(string url, string data)
        {
            if (data == null && PostData.Count() > 0)
            {
                data = string.Join("&", PostData.Entries.Select(e => WebUtility.UrlEncode(e.Key) + "=" + WebUtility.UrlEncode(LuaConv.Str(e.Value) ?? "")));
                PostData.Clear();
            }

            return Send("POST", url, data ?? "");
        }

        public string GetRootedUrl(string url) => Urls.GetRooted(url, context.CurrentUrl);

        private HttpResponseUserData Send(string method, string url, string body)
        {
            url = GetRootedUrl(url);

            var request = new HttpRequest { Url = url, Method = method, Body = body, Referer = Referer, AllowAutoRedirect = AllowAutoRedirect };

            foreach (var header in Headers.Entries)
                request.Headers[header.Key] = LuaConv.Str(header.Value);

            if (context.CurrentModule != null)
                foreach (var header in context.CurrentModule.Cookies.Entries)
                    request.Cookies[header.Key] = LuaConv.Str(header.Value);

            foreach (var cookie in context.CookiesFor(new Uri(url).Host))
                request.Cookies[cookie.Key] = cookie.Value;

            foreach (var cookie in Cookies.Entries)
                request.Cookies[cookie.Key] = LuaConv.Str(cookie.Value);

            var result = context.Http.Send(request);

            foreach (var cookie in result.Cookies)
                Cookies[cookie.Key] = cookie.Value;

            return new HttpResponseUserData(result);
        }

        public override string ToString() => "http";
    }

    [MoonSharpUserData]
    public class GlobalUserData
    {
        private readonly HostContext context;

        public GlobalUserData(HostContext context)
        {
            this.context = context;
        }

        public string GetSetting(string name) => context.Settings.TryGetValue(name, out var v) ? v : null;

        public void SetSetting(string name, object value) => context.Settings[name] = LuaConv.Str(value);

        public void SetCookie(string domain, string name, object value) => context.SetCookie(domain, name, LuaConv.Str(value));

        /// <summary>Copies cookies (e.g. <c>response.Cookies</c>) into the shared jar for the current module's domain.</summary>
        public void SetCookies(DictUserData cookies)
        {
            var domain = LuaConv.Str(context.CurrentModule?.Domain) ?? "";

            foreach (var cookie in cookies.Entries)
                context.SetCookie(domain, cookie.Key, LuaConv.Str(cookie.Value));
        }
    }
}
