using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MangaDownloader.Lua.Api;
using MangaDownloader.Lua.Runtime;
using MangaDownloader.Net;

namespace MangaDownloader.Core
{
    public sealed class ModuleLoadFailure
    {
        public string File { get; set; }
        public string Error { get; set; }
        public bool IsEncrypted { get; set; }
    }

    public sealed class ModuleMatch
    {
        public LuaModule File { get; set; }
        public ModuleUserData Module { get; set; }
        public string Domain { get; set; }
    }

    /// <summary>All Lua scrapers found in one directory, loaded once and matched to URLs by domain.</summary>
    public sealed class ModuleRepository
    {
        private ModuleRepository(IReadOnlyList<LuaModule> files, IReadOnlyList<ModuleLoadFailure> failures, string directory, IHttpBackend http, CookieJar jar)
        {
            Files = files;
            this.directory = directory;
            this.http = http;
            this.jar = jar;
            Failures = failures;
        }

        private readonly string directory;
        private readonly IHttpBackend http;
        private readonly CookieJar jar;
        private readonly object gate = new object();
        public IReadOnlyList<LuaModule> Files { get; private set; }

        public string Directory => directory;
        public IReadOnlyList<LuaModule> All => Files;

        public IReadOnlyList<ModuleLoadFailure> Failures { get; private set; }

        /// <summary>Every module object (a file can declare several via <c>Module.New()</c>).</summary>
        public IEnumerable<(LuaModule File, ModuleUserData Module)> Modules =>
            Files.Where(f => !f.IsLibrary).SelectMany(f => f.Modules.Select(m => (f, m)));

        private static (List<LuaModule> Files, List<ModuleLoadFailure> Failures) Scan(string directory, IHttpBackend http, CookieJar jar, IEnumerable<string> disabledModules)
        {
            var files = new List<LuaModule>();
            var failures = new List<ModuleLoadFailure>();
            var disabled = new HashSet<string>(disabledModules ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.lua").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(path);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(path);
                if (disabled.Contains(fileName) || disabled.Contains(nameWithoutExt))
                    continue;

                try
                {
                    files.Add(LuaModule.Load(path, http, jar));
                }
                catch (Exception e)
                {
                    failures.Add(new ModuleLoadFailure { File = fileName, Error = e.Message, IsEncrypted = (e as ModuleLoadException)?.IsEncrypted == true });
                }
            }

            void TryLoadEmbedded(string resName, string fileName)
            {
                if (disabled.Contains(fileName) || disabled.Contains(Path.GetFileNameWithoutExtension(fileName)))
                    return;

                using var stream = typeof(ModuleRepository).Assembly.GetManifestResourceStream(resName);
                if (stream == null) return;
                using var reader = new StreamReader(stream);
                files.Insert(0, LuaModule.LoadSource(Path.Combine(directory, fileName), reader.ReadToEnd(), http, jar));
            }

            TryLoadEmbedded("MangaDownloader.Core.Modules.ILikeComix.lua", "ILikeComix.lua");
            TryLoadEmbedded("MangaDownloader.Core.Modules.Kagane.lua", "Kagane.lua");
            TryLoadEmbedded("MangaDownloader.Core.Modules.Comix.lua", "Comix.lua");

            return (files, failures);
        }

        public static ModuleRepository LoadDirectory(string directory, IHttpBackend http = null, CookieJar jar = null, IEnumerable<string> disabledModules = null)
        {
            if (!System.IO.Directory.Exists(directory))
                throw new DirectoryNotFoundException("Module directory not found: " + directory);

            if (disabledModules == null)
            {
                var defaultRepoFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "manga-downloader", "repositories.json");
                if (File.Exists(defaultRepoFile))
                {
                    try { disabledModules = Sync.RepositorySource.Load(defaultRepoFile).SelectMany(s => s.DisabledModules); }
                    catch { }
                }
            }

            var (files, failures) = Scan(directory, http, jar, disabledModules);
            return new ModuleRepository(files, failures, directory, http, jar);
        }

        public void Reload(IEnumerable<string> disabledModules = null)
        {
            if (disabledModules == null)
            {
                var defaultRepoFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "manga-downloader", "repositories.json");
                if (File.Exists(defaultRepoFile))
                {
                    try { disabledModules = Sync.RepositorySource.Load(defaultRepoFile).SelectMany(s => s.DisabledModules); }
                    catch { }
                }
            }

            lock (gate)
            {
                var (files, failures) = Scan(directory, http, jar, disabledModules);
                Files = files;
                Failures = failures;
            }
        }

        /// <summary>Detect a common HTML reader layout and install it only after testing a chapter.</summary>
        public (int Chapters, int Pages) AddFromLink(string link, CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType != UriHostNameType.Dns)
                throw new ArgumentException("Enter a complete HTTP or HTTPS series or chapter link.");
            if (!Monitor.TryEnter(gate))
                throw new ArgumentException("Another site check is already running. Wait for it to finish before adding another site.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var existing = Find(link);
                if (existing != null)
                    throw new ArgumentException(existing.Module.GetName(existing.Domain) + " is already supported. Use Add to queue for this link. Add site cannot repair an existing site's scraper.");
                var host = uri.Host.ToLowerInvariant();
                var path = Path.Combine(directory, "auto-" + host + ".lua");
                if (File.Exists(path))
                    throw new ArgumentException("A generated module already exists for this site.");
                var source = GenericSiteScript.Replace("__DOMAIN__", host);
                var module = LuaModule.LoadSource(path, source, http == null ? null : new SiteCheckBackend(http, deadline.Token), jar);
                var session = new ModuleSession(module);
                var chapters = session.GetChapters(link);
                var chapter = chapters.FirstOrDefault(c => Uri.TryCreate(c.Url, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
                var pages = session.GetPages(chapter?.Url ?? link);
                deadline.Token.ThrowIfCancellationRequested();
                if (pages.Count == 0 || pages.Any(p => !Uri.TryCreate(p.Url, UriKind.Absolute, out var image) ||
                    (image.Scheme != Uri.UriSchemeHttp && image.Scheme != Uri.UriSchemeHttps)))
                    throw new ArgumentException("Could not detect reader images. Try a direct chapter link; this site may need a custom module.");
                File.WriteAllText(path, source);
                // The installed module must use the normal backend, not the expired detection deadline.
                Files = Files.Concat(new[] { LuaModule.LoadSource(path, source, http, jar) }).ToArray();
                return (chapters.Count, pages.Count);
            }
            catch (Exception) when (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The site check timed out after 30 seconds. The site may be unavailable or need a custom module. You can keep using the app and try again later.");
            }
            finally { Monitor.Exit(gate); }
        }

        private sealed class SiteCheckBackend : IHttpBackend
        {
            private readonly IHttpBackend inner;
            private readonly CancellationToken cancellation;

            public SiteCheckBackend(IHttpBackend inner, CancellationToken cancellation)
            {
                this.inner = inner;
                this.cancellation = cancellation;
            }

            public HttpResult Send(HttpRequest request)
            {
                cancellation.ThrowIfCancellationRequested();
                request.CancellationToken = cancellation;
                request.AllowInteractiveChallenge = false;
                var result = inner.Send(request);
                cancellation.ThrowIfCancellationRequested();
                return result;
            }

            public HttpResult Download(HttpRequest request, Stream destination) => throw new InvalidOperationException("Site detection only reads HTML pages.");
        }

        private const string GenericSiteScript = @"function Register()
 module.Name = 'Custom: __DOMAIN__'
 module.Domains.Add('__DOMAIN__')
end
function GetInfo()
 info.Title = dom.SelectValue('//h1') or dom.SelectValue('//title')
end
function GetChapters()
 if url:lower():match(""chapter[%-%_/%d]"") or dom.SelectElement('//img[contains(@class,""wp-manga-chapter-img"") or contains(@class,""chapter-img"") or contains(@class,""page-image"") or ancestor::*[contains(@class,""reading-content"") or @id=""readerarea"" or @id=""chapter-content"" or contains(@class,""chapter-content"") or contains(@class,""reader-content"")]]') then return end
 for a in dom.SelectElements('//a[@href and (contains(translate(@href,""CHAPTER"",""chapter""),""chapter"") or contains(translate(.,""CHAPTER"",""chapter""),""chapter""))]') do
  local href = a.SelectValue('@href')
  if href and href ~= url and not href:match('^#') then
   local c = ChapterInfo.New()
   c.Url = href
   c.Title = a.SelectValue('.')
   chapters.Add(c)
  end
 end
end
function GetPages()
 for img in dom.SelectElements('//img[contains(@class,""wp-manga-chapter-img"") or contains(@class,""reading-content"") or contains(@class,""chapter-img"") or contains(@class,""page-image"") or ancestor::*[contains(@class,""reading-content"") or @id=""readerarea"" or @id=""chapter-content"" or contains(@class,""chapter-content"") or contains(@class,""reader-content"")]]') do
  local src = img.SelectValue('@data-src') or img.SelectValue('@data-lazy-src') or img.SelectValue('@src')
  if src and src ~= '' then pages.Add(src:trim()) end
 end
end";

        /// <summary>The module serving <paramref name="url"/>; when several match, the most specific (longest) domain wins.</summary>
        public ModuleMatch Find(string url)
        {
            ModuleMatch best = null;

            foreach (var file in Files)
            {
                var module = file.FindModule(url, out var domain);

                if (module != null && (best == null || domain.Length > best.Domain.Length))
                    best = new ModuleMatch { File = file, Module = module, Domain = domain };
            }

            return best;
        }
    }
}
