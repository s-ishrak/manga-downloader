using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;

namespace MangaDownloader.Net
{
    public sealed class HttpRequest
    {
        public string Url { get; set; }
        public string Method { get; set; } = "GET";
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IDictionary<string, string> Cookies { get; } = new Dictionary<string, string>();
        public string Referer { get; set; }
        public string Body { get; set; }
        public string ContentType { get; set; }
        public bool AllowAutoRedirect { get; set; } = true;
    }

    public sealed class HttpResult
    {
        public int StatusCode { get; set; }
        public string Body { get; set; } = "";
        public string Url { get; set; }
        public string ContentType { get; set; }
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IDictionary<string, string> Cookies { get; } = new Dictionary<string, string>();

        /// <summary>Every Set-Cookie received, with domain/path/expiry (the persistent jar stores these).</summary>
        public IList<CookieEntry> SetCookies { get; } = new List<CookieEntry>();
    }

    public interface IHttpBackend
    {
        HttpResult Send(HttpRequest request);

        /// <summary>Streams the response body into <paramref name="destination"/> (only when the status is 2xx); <c>Body</c> stays empty.</summary>
        HttpResult Download(HttpRequest request, Stream destination);
    }

    public sealed class ProxySettings
    {
        public string Url { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        /// <summary>Accepts <c>http://host:port</c> or <c>http://user:pass@host:port</c>.</summary>
        public static ProxySettings Parse(string url)
        {
            var uri = new Uri(url.Contains("://") ? url : "http://" + url);

            if (uri.Scheme != "http" && uri.Scheme != "https")
                throw new NotSupportedException("Only http/https proxies are supported on this runtime (SOCKS needs .NET 6+): " + uri.Scheme);

            var settings = new ProxySettings { Url = uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", "") };

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(new[] { ':' }, 2);
                settings.Username = Uri.UnescapeDataString(parts[0]);
                settings.Password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            }

            return settings;
        }
    }

    public sealed class HttpClientOptions
    {
        public string UserAgent { get; set; } = HttpClientBackend.DefaultUserAgent;
        public ProxySettings Proxy { get; set; }
        public int TimeoutSeconds { get; set; } = 60;
    }

    /// <summary>Plain HttpClient backend (no TLS impersonation or Cloudflare solving yet - phase 4).</summary>
    public sealed class HttpClientBackend : IHttpBackend, IDisposable
    {
        public const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        private readonly HttpClient follow;
        private readonly HttpClient noFollow;

        private readonly HttpClientOptions options;

        public HttpClientBackend() : this(new HttpClientOptions())
        {
        }

        public HttpClientBackend(HttpClientOptions options)
        {
            this.options = options;
            follow = Create(options, true);
            noFollow = Create(options, false);
        }

        private static HttpClient Create(HttpClientOptions options, bool autoRedirect)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = autoRedirect,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            };

            if (options.Proxy != null)
            {
                handler.Proxy = new WebProxy(options.Proxy.Url)
                {
                    Credentials = options.Proxy.Username == null ? null : new NetworkCredential(options.Proxy.Username, options.Proxy.Password),
                };
                handler.UseProxy = true;
            }

            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        }

        public HttpResult Send(HttpRequest request) => Execute(request, null);

        public HttpResult Download(HttpRequest request, Stream destination) => Execute(request, destination);

        private HttpResult Execute(HttpRequest request, Stream destination)
        {
            var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            if (!request.Headers.ContainsKey("User-Agent"))
                message.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);

            foreach (var header in request.Headers)
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (!string.IsNullOrEmpty(request.Referer))
                message.Headers.Referrer = new Uri(request.Referer);

            if (request.Cookies.Count > 0)
                message.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", request.Cookies.Select(c => c.Key + "=" + c.Value)));

            if (request.Body != null)
                message.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType ?? "application/x-www-form-urlencoded");

            var client = request.AllowAutoRedirect ? follow : noFollow;

            using (var response = client.SendAsync(message, destination == null ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                var result = new HttpResult
                {
                    StatusCode = (int)response.StatusCode,
                    Url = response.RequestMessage?.RequestUri?.AbsoluteUri ?? request.Url,
                    ContentType = response.Content.Headers.ContentType?.MediaType,
                };

                if (destination == null)
                    result.Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                else if (response.IsSuccessStatusCode)
                    response.Content.CopyToAsync(destination).GetAwaiter().GetResult();

                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    result.Headers[header.Key] = string.Join(", ", header.Value);

                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var setCookie in setCookies)
                    {
                        var cookie = SetCookieParser.Parse(setCookie, new Uri(result.Url));

                        if (cookie != null)
                        {
                            result.Cookies[cookie.Name] = cookie.Value;
                            result.SetCookies.Add(cookie);
                        }
                    }
                }

                return result;
            }
        }

        public void Dispose()
        {
            follow.Dispose();
            noFollow.Dispose();
        }
    }
}
