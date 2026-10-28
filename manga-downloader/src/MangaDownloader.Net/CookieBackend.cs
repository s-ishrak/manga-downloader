using System;
using System.IO;
using System.Linq;

namespace MangaDownloader.Net
{
    /// <summary>
    /// Adds the jar's cookies to every request, stores every Set-Cookie, and follows redirects by hand so cookies set
    /// (or needed) on each hop are honoured. Requests with <c>AllowAutoRedirect = false</c> get the raw redirect back.
    /// </summary>
    public sealed class CookieBackend : IHttpBackend
    {
        private const int MaxRedirects = 10;

        private readonly IHttpBackend inner;

        public CookieBackend(IHttpBackend inner, CookieJar jar)
        {
            this.inner = inner;
            Jar = jar;
        }

        public CookieJar Jar { get; }

        public HttpResult Send(HttpRequest request) => Execute(request, null);

        public HttpResult Download(HttpRequest request, Stream destination) => Execute(request, destination);

        private HttpResult Execute(HttpRequest original, Stream destination)
        {
            var current = Copy(original);
            var follow = original.AllowAutoRedirect;
            var earlier = new System.Collections.Generic.List<CookieEntry>();

            for (var hop = 0; ; hop++)
            {
                var uri = new Uri(current.Url);
                var hopRequest = Copy(current);
                hopRequest.AllowAutoRedirect = false;

                // Cookies from the jar first, so cookies the caller set explicitly win.
                foreach (var cookie in Jar.CookiesFor(uri))
                    if (!hopRequest.Cookies.ContainsKey(cookie.Key))
                        hopRequest.Cookies[cookie.Key] = cookie.Value;

                var result = destination == null ? inner.Send(hopRequest) : inner.Download(hopRequest, destination);

                foreach (var setCookie in result.SetCookies)
                    Jar.Set(setCookie);

                result.Url = current.Url;

                // Cookies set on earlier hops (a login POST usually sets the session cookie on its redirect) belong to the final
                // response too, otherwise a module checking response.Cookies after a redirected POST never sees them.
                foreach (var cookie in earlier)
                {
                    result.SetCookies.Insert(0, cookie);
                    result.Cookies[cookie.Name] = cookie.Value;
                }

                var status = result.StatusCode;
                var isRedirect = status == 301 || status == 302 || status == 303 || status == 307 || status == 308;

                if (!follow || !isRedirect || hop >= MaxRedirects || !result.Headers.TryGetValue("Location", out var location) || string.IsNullOrEmpty(location))
                    return result;

                // 303 (and 301/302 after a POST) turn into a GET without a body, as browsers do.
                if (status == 303 || ((status == 301 || status == 302) && current.Method == "POST"))
                {
                    current.Method = "GET";
                    current.Body = null;
                    current.ContentType = null;
                }

                earlier.AddRange(result.SetCookies.Where(c => !earlier.Any(e => e.Name == c.Name)));
                current.Referer = current.Url;
                current.Url = new Uri(uri, location).AbsoluteUri;
            }
        }

        private static HttpRequest Copy(HttpRequest r)
        {
            var copy = new HttpRequest
            {
                Url = r.Url,
                Method = r.Method,
                Referer = r.Referer,
                Body = r.Body,
                ContentType = r.ContentType,
                AllowAutoRedirect = r.AllowAutoRedirect,
            };

            foreach (var h in r.Headers)
                copy.Headers[h.Key] = h.Value;

            foreach (var c in r.Cookies)
                copy.Cookies[c.Key] = c.Value;

            return copy;
        }
    }
}
