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
        private ModuleRepository(IReadOnlyList<LuaModule> files, IReadOnlyList<ModuleLoadFailure> failures)
        {
            Files = files;
            Failures = failures;
        }

        public IReadOnlyList<LuaModule> Files { get; }

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

            return new ModuleRepository(files, failures);
        }

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
