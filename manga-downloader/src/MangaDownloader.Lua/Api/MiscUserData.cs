using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MangaDownloader.Lua.Runtime;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    /// <summary>URL helpers shared by the free functions and <c>Url</c>.</summary>
    internal static class Urls
    {
        public static string GetRoot(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return url;

            return uri.GetLeftPart(UriPartial.Authority);
        }

        /// <summary>Makes <paramref name="url"/> absolute against <paramref name="baseUrl"/> (protocol-relative, root-relative and relative forms).</summary>
        public static string GetRooted(string url, string baseUrl)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(baseUrl))
                return url;

            if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https"))
                return url;

            if (url.StartsWith("//"))
                return new Uri(baseUrl).Scheme + ":" + url;

            return Uri.TryCreate(new Uri(baseUrl), url, out var combined) ? combined.AbsoluteUri : url;
        }

        public static string GetHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

        /// <summary>Host without "www." (naive registrable domain: last two labels).</summary>
        public static string GetDomain(string url)
        {
            var host = GetHost(url);
            var parts = host.Split('.');
            return parts.Length <= 2 ? host : string.Join(".", parts.Skip(parts.Length - 2));
        }

        public static string StripParameters(string url)
        {
            var i = url.IndexOfAny(new[] { '?', '#' });
            return i < 0 ? url : url.Substring(0, i);
        }

        public static string GetParameter(string url, string name)
        {
            var q = url.IndexOf('?');
            if (q < 0)
                return null;

            var query = url.Substring(q + 1).Split('#')[0];

            foreach (var pair in query.Split('&'))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair.Substring(0, eq));

                if (key == name)
                    return eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }

            return null;
        }

        public static string SetParameter(string url, string name, string value)
        {
            var hash = "";
            var h = url.IndexOf('#');
            if (h >= 0)
            {
                hash = url.Substring(h);
                url = url.Substring(0, h);
            }

            var q = url.IndexOf('?');
            var path = q < 0 ? url : url.Substring(0, q);
            var pairs = q < 0 ? new List<string>() : url.Substring(q + 1).Split('&').Where(p => p.Length > 0).ToList();
            var encoded = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value ?? "");
            var index = pairs.FindIndex(p => Uri.UnescapeDataString(p.Split('=')[0]) == name);

            if (index >= 0)
                pairs[index] = encoded;
            else
                pairs.Add(encoded);

            return path + "?" + string.Join("&", pairs) + hash;
        }
    }

    [MoonSharpUserData]
    public class UrlUserData
    {
        private UriBuilder builder;

        private UrlUserData(Uri uri)
        {
            builder = new UriBuilder(uri);
        }

        public static UrlUserData New(string href) => new UrlUserData(new Uri(href));

        public static UrlUserData New(string href, string baseUrl) => new UrlUserData(new Uri(new Uri(baseUrl), href));

        public static string Combine(string baseUrl, string relative) => Urls.GetRooted(relative, baseUrl);

        public string Href { get => builder.Uri.AbsoluteUri; set => builder = new UriBuilder(new Uri(value)); }

        public string Protocol { get => builder.Scheme + ":"; set => builder.Scheme = value.TrimEnd(':'); }

        public string Hostname { get => builder.Host; set => builder.Host = value; }

        public string Host
        {
            get => builder.Uri.IsDefaultPort ? builder.Host : builder.Host + ":" + builder.Port;
            set => builder.Host = value;
        }

        public string Port { get => builder.Uri.IsDefaultPort ? "" : builder.Port.ToString(); set => builder.Port = int.Parse(value); }

        public string Origin => builder.Scheme + "://" + Host;

        public string PathName { get => builder.Path; set => builder.Path = value; }

        public string Search { get => builder.Query.Length > 0 ? "?" + builder.Query.TrimStart('?') : ""; set => builder.Query = (value ?? "").TrimStart('?'); }

        public string Hash { get => builder.Fragment; set => builder.Fragment = (value ?? "").TrimStart('#'); }

        public string Username { get => builder.UserName; set => builder.UserName = value; }

        public string Password { get => builder.Password; set => builder.Password = value; }

        public string ToJson() => Newtonsoft.Json.JsonConvert.SerializeObject(new { Href, Protocol, Host, Hostname, Port, Origin, PathName, Search, Hash });

        public override string ToString() => Href;
    }

    [MoonSharpUserData]
    public class RegexMatchUserData
    {
        private readonly Match match;

        public RegexMatchUserData(Match match)
        {
            this.match = match;
        }

        public bool Success => match.Success;

        public string Value => match.Value;

        public int Index => match.Index;

        public ListUserData Groups => new ListUserData(match.Groups.Cast<Group>().Select(g => (object)g.Value));

        public override string ToString() => match.Value;
    }

    [MoonSharpUserData]
    public class RegexUserData
    {
        public static string Escape(string text) => Regex.Escape(text);

        public static bool IsMatch(string input, string pattern) => Regex.IsMatch(input ?? "", pattern);

        public static RegexMatchUserData Match(string input, string pattern) => new RegexMatchUserData(Regex.Match(input ?? "", pattern));

        public static ListUserData Matches(string input, string pattern) =>
            new ListUserData(Regex.Matches(input ?? "", pattern).Cast<Match>().Select(m => (object)new RegexMatchUserData(m)));

        public static string Replace(string input, string pattern, object replacement) => Regex.Replace(input ?? "", pattern, LuaConv.Str(replacement) ?? "");
    }

    [MoonSharpUserData]
    public class HashUserData
    {
        public static string MD5(string text) => Hex(System.Security.Cryptography.MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));

        public static string Sha256(string text) => Hex(System.Security.Cryptography.SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    /// <summary><c>Paginator.New(http, dom, nextXPath)</c>: iterates the first page, then follows the "next" link until it disappears.</summary>
    [MoonSharpUserData]
    public class PaginatorUserData : IEnumerable<object>
    {
        private readonly HttpUserData http;
        private readonly HtmlDocumentUserData first;
        private readonly string nextXPath;

        private PaginatorUserData(HttpUserData http, HtmlDocumentUserData first, string nextXPath)
        {
            this.http = http;
            this.first = first;
            this.nextXPath = nextXPath;
        }

        public static PaginatorUserData New(HttpUserData http, HtmlDocumentUserData dom, string nextXPath) => new PaginatorUserData(http, dom, nextXPath);

        public IEnumerator<object> GetEnumerator()
        {
            var seen = new HashSet<string>();
            var current = first;

            while (current != null)
            {
                yield return current;

                var next = current.SelectValue(nextXPath);

                if (string.IsNullOrWhiteSpace(next))
                    yield break;

                var nextUrl = Urls.GetRooted(next, current.Url);

                if (!seen.Add(nextUrl) || nextUrl == first.Url)
                    yield break;

                current = new HtmlDocumentUserData(http.Get(nextUrl), nextUrl);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Placeholders for engine features not implemented yet; they fail loudly only when a module actually uses them.</summary>
    [MoonSharpUserData]
    public class UnsupportedUserData
    {
        public static UnsupportedUserData New(object a) => throw new NotSupportedException("Not implemented yet.");

        public static UnsupportedUserData New(object a, object b) => throw new NotSupportedException("Not implemented yet.");

        public static string Solve(object http, object url) => throw new NotSupportedException("Captcha solving is not implemented.");
    }
}
