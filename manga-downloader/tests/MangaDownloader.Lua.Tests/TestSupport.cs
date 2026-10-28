using System;
using System.Collections.Generic;
using System.IO;
using MangaDownloader.Lua.Runtime;
using MangaDownloader.Net;

namespace MangaDownloader.Lua.Tests
{
    /// <summary>Serves canned pages by URL so module logic can be tested without the network.</summary>
    public sealed class FixtureHttp : IHttpBackend
    {
        public Dictionary<string, string> Pages { get; } = new Dictionary<string, string>();

        /// <summary>Binary bodies served to Download(), by URL.</summary>
        public Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>();

        public Func<string, int> FailuresBeforeSuccess { get; set; } = _ => 0;

        private readonly Dictionary<string, int> attempts = new Dictionary<string, int>();

        public int DownloadDelayMilliseconds { get; set; }

        public int MaxConcurrentDownloads { get; private set; }

        private int active;

        public HttpResult Download(HttpRequest request, Stream destination)
        {
            int n;

            lock (Requests)
            {
                Requests.Add(request);
                attempts.TryGetValue(request.Url, out n);
                attempts[request.Url] = ++n;
                MaxConcurrentDownloads = Math.Max(MaxConcurrentDownloads, ++active);
            }

            try
            {
                if (DownloadDelayMilliseconds > 0)
                    System.Threading.Thread.Sleep(DownloadDelayMilliseconds);

                var failing = n <= FailuresBeforeSuccess(request.Url);

                if (failing || !Files.TryGetValue(request.Url, out var bytes))
                    return new HttpResult { StatusCode = failing ? 503 : 404, Url = request.Url };

                destination.Write(bytes, 0, bytes.Length);
                return new HttpResult { StatusCode = 200, Url = request.Url, ContentType = "image/jpeg" };
            }
            finally
            {
                lock (Requests)
                    active--;
            }
        }

        public int AttemptsFor(string url)
        {
            lock (Requests)
                return attempts.TryGetValue(url, out var n) ? n : 0;
        }

        public List<HttpRequest> Requests { get; } = new List<HttpRequest>();

        public HttpResult Send(HttpRequest request)
        {
            Requests.Add(request);

            if (!Pages.TryGetValue(request.Url, out var body))
                return new HttpResult { StatusCode = 404, Url = request.Url, Body = "" };

            return new HttpResult { StatusCode = 200, Url = request.Url, Body = body };
        }
    }

    internal static class TestSupport
    {
        /// <summary>
        /// Directory holding the upstream HDoujin Lua modules. Set MANGA_DOWNLOADER_MODULES to run the
        /// module-level tests; they are skipped (return early) when it is not available.
        /// </summary>
        public static string ModulesDirectory()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("MANGA_DOWNLOADER_MODULES"),
                @"D:\Manga\HDoujin.Downloader-2025.1.6.1-win\modules\lua",
            };

            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && Directory.Exists(c))
                    return c;

            return null;
        }

        /// <summary>Writes <paramref name="source"/> to a temp .lua file and loads it as a module.</summary>
        public static LuaModule LoadInline(string source, IHttpBackend http = null, MangaDownloader.Net.CookieJar jar = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "manga-downloader-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "inline.lua");
            File.WriteAllText(path, source);
            return LuaModule.Load(path, http, jar);
        }

        /// <summary>Loads inline Lua whose GetInfo stores a result in info.Title, runs it over <paramref name="html"/>.</summary>
        public static string Eval(string luaExpression, string html = "<html></html>")
        {
            var http = new FixtureHttp();
            http.Pages["https://test.example/page"] = html;

            var module = LoadInline(
                "function Register()\n module.Name = 'T'\n module.Domains.Add('test.example')\nend\n" +
                "function GetInfo()\n info.Title = " + luaExpression + "\nend\n", http);

            return new ModuleSession(module).GetInfo("https://test.example/page").Title;
        }
    }
}

namespace MangaDownloader.Lua.Tests
{
    /// <summary>Backend driven by a delegate, for scripting redirects, challenges and cookies.</summary>
    public sealed class ScriptedHttp : IHttpBackend
    {
        private readonly Func<HttpRequest, HttpResult> handler;

        public ScriptedHttp(Func<HttpRequest, HttpResult> handler)
        {
            this.handler = handler;
        }

        public List<HttpRequest> Requests { get; } = new List<HttpRequest>();

        public HttpResult Send(HttpRequest request)
        {
            lock (Requests)
                Requests.Add(request);

            var r = handler(request);
            r.Url = r.Url ?? request.Url;
            return r;
        }

        public HttpResult Download(HttpRequest request, Stream destination)
        {
            var r = Send(request);

            if (r.StatusCode >= 200 && r.StatusCode < 300)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(r.Body ?? "");
                destination.Write(bytes, 0, bytes.Length);
            }

            return r;
        }
    }
}
