using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public IReadOnlyList<ModuleLoadFailure> Failures { get; }

        /// <summary>Every module object (a file can declare several via <c>Module.New()</c>).</summary>
        public IEnumerable<(LuaModule File, ModuleUserData Module)> Modules =>
            Files.Where(f => !f.IsLibrary).SelectMany(f => f.Modules.Select(m => (f, m)));

        public static ModuleRepository LoadDirectory(string directory, IHttpBackend http = null, CookieJar jar = null)
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("Module directory not found: " + directory);

            var files = new List<LuaModule>();
            var failures = new List<ModuleLoadFailure>();

            foreach (var path in Directory.EnumerateFiles(directory, "*.lua").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    files.Add(LuaModule.Load(path, http, jar));
                }
                catch (Exception e)
                {
                    failures.Add(new ModuleLoadFailure { File = Path.GetFileName(path), Error = e.Message, IsEncrypted = (e as ModuleLoadException)?.IsEncrypted == true });
                }
            }

            using (var stream = typeof(ModuleRepository).Assembly.GetManifestResourceStream("MangaDownloader.Core.Modules.ILikeComix.lua"))
            using (var reader = new StreamReader(stream))
                files.Insert(0, LuaModule.LoadSource(Path.Combine(directory, "ILikeComix.lua"), reader.ReadToEnd(), http, jar));

            using (var stream = typeof(ModuleRepository).Assembly.GetManifestResourceStream("MangaDownloader.Core.Modules.Kagane.lua"))
            using (var reader = new StreamReader(stream))
                files.Insert(0, LuaModule.LoadSource(Path.Combine(directory, "Kagane.lua"), reader.ReadToEnd(), http, jar));

            return new ModuleRepository(files, failures, directory, http, jar);
        }

        /// <summary>Detect a common HTML reader layout and install it only after testing a chapter.</summary>
        public (int Chapters, int Pages) AddFromLink(string link)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType != UriHostNameType.Dns)
                throw new ArgumentException("Enter a complete HTTP or HTTPS series or chapter link.");
            lock (gate)
            {
                if (Find(link) != null)
                    throw new ArgumentException("This site already has a module.");
                var host = uri.Host.ToLowerInvariant();
                var path = Path.Combine(directory, "auto-" + host + ".lua");
                if (File.Exists(path))
                    throw new ArgumentException("A generated module already exists for this site.");
                var source = GenericSiteScript.Replace("__DOMAIN__", host);
                var module = LuaModule.LoadSource(path, source, http, jar);
                var session = new ModuleSession(module);
                var chapters = session.GetChapters(link);
                var chapter = chapters.FirstOrDefault(c => Uri.TryCreate(c.Url, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
                var pages = session.GetPages(chapter?.Url ?? link);
                if (pages.Count == 0 || pages.Any(p => !Uri.TryCreate(p.Url, UriKind.Absolute, out var image) ||
                    (image.Scheme != Uri.UriSchemeHttp && image.Scheme != Uri.UriSchemeHttps)))
                    throw new ArgumentException("Could not detect reader images. Try a direct chapter link; this site may need a custom module.");
                File.WriteAllText(path, source);
                Files = Files.Concat(new[] { module }).ToArray();
                return (chapters.Count, pages.Count);
            }
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
