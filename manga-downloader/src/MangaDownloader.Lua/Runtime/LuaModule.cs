using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MangaDownloader.Lua.Api;
using MangaDownloader.Net;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Runtime
{
    /// <summary>A loaded <c>.lua</c> scraper: its script, host context and the module objects its <c>Register()</c> declared.</summary>
    public sealed class LuaModule
    {
        private static readonly object RegistrationLock = new object();
        private static bool registered;

        private LuaModule(string path, Script script, HostContext context)
        {
            Path = path;
            Script = script;
            Context = context;
        }

        public string Path { get; }

        /// <summary>
        /// A module's Lua script is single-threaded. Every ModuleSession call locks this, so the download queue, the tracker and the
        /// web UI can all use the same loaded module without corrupting its state.
        /// </summary>
        public object SyncRoot { get; } = new object();

        public string FileName => System.IO.Path.GetFileName(Path);

        public Script Script { get; }

        public HostContext Context { get; }

        /// <summary>One entry per <c>module</c> / <c>Module.New()</c> object after <c>Register()</c> ran.</summary>
        public IReadOnlyList<ModuleUserData> Modules => Context.Modules;

        /// <summary>True when the file has no <c>Register()</c> and only provides functions for other modules to <c>require</c>.</summary>
        public bool IsLibrary => Context.Modules.Count == 0 || Script.Globals.Get("Register").Type != DataType.Function;

        public static LuaModule Load(string path, IHttpBackend http = null, CookieJar jar = null)
        {
            EnsureTypesRegistered();

            var context = new HostContext(http ?? new NoNetworkBackend(), jar);
            var script = ScriptFactory.Create(context, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));

            try
            {
                script.DoString(File.ReadAllText(path, Encoding.UTF8), null, System.IO.Path.GetFileName(path));
            }
            catch (EncryptedModuleException e)
            {
                throw new ModuleLoadException(path, "Encrypted module (DoEncryptedString); key not recovered.", e) { IsEncrypted = true };
            }
            catch (InterpreterException e)
            {
                if (IsEncrypted(e))
                    throw new ModuleLoadException(path, "Encrypted module (DoEncryptedString); key not recovered.", e) { IsEncrypted = true };

                throw new ModuleLoadException(path, e.DecoratedMessage ?? e.Message, e);
            }

            var module = new LuaModule(path, script, context);

            // Helper files (WordPress, TSReader...) only define shared functions for others to require.
            if (script.Globals.Get("Register").Type != DataType.Function)
                return module;

            try
            {
                script.Call(script.Globals.Get("Register"));
            }
            catch (InterpreterException e)
            {
                throw new ModuleLoadException(path, "Register(): " + (e.DecoratedMessage ?? e.Message), e);
            }

            // A file that starts with `module = Module.New()` leaves the original module object empty; drop such leftovers.
            context.Modules.RemoveAll(m => string.IsNullOrEmpty(m.GetName()));

            if (context.Modules.Count == 0)
                throw new ModuleLoadException(path, "Register() did not set a module name or domain.", null);

            return module;
        }

        /// <summary>Finds the module object serving <paramref name="url"/> and the registered domain it matched.</summary>
        public ModuleUserData FindModule(string url, out string domain)
        {
            domain = null;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            var host = StripWww(uri.Host);
            ModuleUserData best = null;
            string bestDomain = null;

            foreach (var module in Modules)
            {
                foreach (var candidate in module.Domains.All)
                {
                    var d = StripWww(candidate);
                    var match = host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase);

                    if (match && (bestDomain == null || d.Length > StripWww(bestDomain).Length))
                    {
                        best = module;
                        bestDomain = candidate;
                    }
                }
            }

            domain = bestDomain;
            return best;
        }

        private static bool IsEncrypted(Exception e)
        {
            for (; e != null; e = e.InnerException)
                if (e is EncryptedModuleException)
                    return true;

            return false;
        }

        private static string StripWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host.Substring(4) : host;

        private static void EnsureTypesRegistered()
        {
            lock (RegistrationLock)
            {
                if (registered)
                    return;

                MoonSharp.Interpreter.UserData.RegisterAssembly(typeof(LuaModule).Assembly);
                registered = true;
            }
        }

        private sealed class NoNetworkBackend : IHttpBackend
        {
            public HttpResult Send(HttpRequest request) => throw new InvalidOperationException("No HTTP backend configured; cannot request " + request.Url);

            public HttpResult Download(HttpRequest request, System.IO.Stream destination) => Send(request);
        }
    }

    public sealed class ModuleLoadException : Exception
    {
        public ModuleLoadException(string path, string message, Exception inner) : base(message, inner)
        {
            Path = path;
        }

        public string Path { get; }

        /// <summary>True for files wrapped in <c>DoEncryptedString</c>, whose decryption key lives in the original executable.</summary>
        public bool IsEncrypted { get; set; }
    }

    public sealed class EncryptedModuleException : Exception
    {
        public EncryptedModuleException() : base("Encrypted module.")
        {
        }
    }
}
