using System;
using System.Linq;
using System.Text;
using MangaDownloader.Lua.Api;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;

namespace MangaDownloader.Lua.Runtime
{
    /// <summary>Builds a MoonSharp script with the globals, static classes and free functions the modules expect.</summary>
    internal static class ScriptFactory
    {
        /// <summary>Version reported through the <c>API_VERSION</c> global (date-style, as modules compare against e.g. 20241117).</summary>
        public const double ApiVersion = 20250106;

        public static Script Create(HostContext ctx, string moduleDirectory)
        {
            var script = new Script(CoreModules.Preset_Default);

            // Modules share helper files with require "Madara" etc.
            if (moduleDirectory != null)
                script.Options.ScriptLoader = new FileSystemScriptLoader { ModulePaths = new[] { System.IO.Path.Combine(moduleDirectory, "?.lua") } };

            script.Options.DebugPrint = text => ctx.Log(text);

            StringExtensions.Register(script);
            Harden(script);

            var g = script.Globals;

            g["module"] = NewModule(ctx);
            g["info"] = new MangaInfoUserData();
            g["chapters"] = new ChapterListUserData();
            g["pages"] = new PageListUserData();
            g["page"] = new PageInfoUserData();
            g["http"] = new HttpUserData(ctx);
            g["global"] = new GlobalUserData(ctx);
            g["API_VERSION"] = ApiVersion;
            g["url"] = "";
            g["doc"] = "";

            // Static classes (Type.New(...) and friends).
            g["Dom"] = UserData.CreateStatic<HtmlDocumentUserData>();
            g["Json"] = UserData.CreateStatic<JsonUserData>();
            g["Url"] = UserData.CreateStatic<UrlUserData>();
            g["Regex"] = UserData.CreateStatic<RegexUserData>();
            g["Hash"] = UserData.CreateStatic<HashUserData>();
            g["JavaScript"] = UserData.CreateStatic<JavaScriptUserData>();
            g["PageInfo"] = UserData.CreateStatic<PageInfoUserData>();
            g["ChapterInfo"] = UserData.CreateStatic<ChapterInfoUserData>();
            g["List"] = UserData.CreateStatic<ListUserData>();
            g["Paginator"] = UserData.CreateStatic<PaginatorUserData>();
            g["Captcha"] = UserData.CreateStatic<UnsupportedUserData>();
            g["ImageUnscrambler"] = UserData.CreateStatic<ImageUnscramblerUserData>();
            g["LZString"] = UserData.CreateStatic<UnsupportedUserData>();

            // Statics that need the host context.
            g["Http"] = Factory(script, "New", () => new HttpUserData(ctx));
            g["Module"] = Factory(script, "New", () => NewModule(ctx));

            var errors = new Table(script);
            foreach (var code in new[] { "LoginRequired", "LoginFailed", "CaptchaRequired", "DomainNotSupported" })
                errors[code] = code;
            g["Error"] = errors;

            RegisterFunctions(script, ctx);
            return script;
        }

        /// <summary>
        /// Modules may come from a remote repository, so take away what could read arbitrary files: no dofile/loadfile,
        /// and require only accepts plain module names (resolved inside the module directory by the script loader).
        /// </summary>
        private static void Harden(Script script)
        {
            script.Globals["dofile"] = DynValue.Nil;
            script.Globals["loadfile"] = DynValue.Nil;

            var require = script.Globals.Get("require");

            if (require.Type == DataType.Nil || require.Type == DataType.Void)
                return;

            script.Globals["require"] = DynValue.NewCallback((c, a) =>
            {
                var name = a.Count > 0 && a[0].Type == DataType.String ? a[0].String : null;

                if (name == null || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_\-]+$"))
                    throw new ScriptRuntimeException("require: invalid module name");

                return script.Call(require, name);
            });
        }

        private static ModuleUserData NewModule(HostContext ctx)
        {
            var module = new ModuleUserData { Factory = () => NewModule(ctx) };
            ctx.Modules.Add(module);
            return module;
        }

        private static DynValue Factory(Script script, string name, Func<object> create)
        {
            var table = new Table(script);
            table[name] = DynValue.NewCallback((c, a) => UserData.Create(create()));
            return DynValue.NewTable(table);
        }

        private static string S(CallbackArguments a, int i) => a.Count > i && a[i].Type != DataType.Nil ? (a[i].Type == DataType.String ? a[i].String : LuaConv.Str(a[i].ToObject())) : null;

        private static void RegisterFunctions(Script script, HostContext ctx)
        {
            void Fn(string name, Func<CallbackArguments, DynValue> body) =>
                script.Globals[name] = DynValue.NewCallback((c, a) => body(a));

            Fn("isempty", a => DynValue.NewBoolean(a.Count == 0 || LuaConv.IsEmpty(a[0])));
            Fn("toboolean", a => DynValue.NewBoolean(a.Count > 0 && LuaConv.ToBool(a[0])));
            Fn("RegexReplace", a => DynValue.NewString(System.Text.RegularExpressions.Regex.Replace(S(a, 0) ?? "", S(a, 1) ?? "", S(a, 2) ?? "")));
            Fn("FormatString", a => DynValue.NewString(string.Format(S(a, 0) ?? "", Enumerable.Range(1, Math.Max(0, a.Count - 1)).Select(i => (object)S(a, i)).ToArray())));
            Fn("GetRoot", a => DynValue.NewString(Urls.GetRoot(S(a, 0))));
            Fn("GetRooted", a => DynValue.NewString(Urls.GetRooted(S(a, 0), S(a, 1) ?? ctx.CurrentUrl)));
            Fn("GetHost", a => DynValue.NewString(Urls.GetHost(S(a, 0))));
            Fn("GetDomain", a => DynValue.NewString(Urls.GetDomain(S(a, 0))));
            Fn("GetParameter", a => { var v = Urls.GetParameter(S(a, 0) ?? "", S(a, 1) ?? ""); return v == null ? DynValue.Nil : DynValue.NewString(v); });
            Fn("SetParameter", a => DynValue.NewString(Urls.SetParameter(S(a, 0) ?? "", S(a, 1) ?? "", S(a, 2))));
            Fn("StripParameters", a => DynValue.NewString(Urls.StripParameters(S(a, 0) ?? "")));
            Fn("DecodeBase64", a => DynValue.NewString(DecodeBase64(S(a, 0))));
            Fn("Log", a => { ctx.Log(string.Join(" ", Enumerable.Range(0, a.Count).Select(i => S(a, i)))); return DynValue.Nil; });
            Fn("Enqueue", a => { ctx.Enqueued.Add(Urls.GetRooted(S(a, 0), ctx.CurrentUrl)); return DynValue.Nil; });
            Fn("Fail", a => throw new ModuleFailedException(S(a, 0) ?? "Module failed."));
            Fn("FailIfLoginRequired", a => DynValue.Nil);

            // Modules are registered when created; RegisterModule only has to be callable.
            Fn("RegisterModule", a => DynValue.Nil);

            Fn("EncodeUriComponent", a => DynValue.NewString(Uri.EscapeDataString(S(a, 0) ?? "")));
            Fn("DecodeUriComponent", a => DynValue.NewString(Uri.UnescapeDataString(S(a, 0) ?? "")));
            Fn("Unescape", a => DynValue.NewString(Uri.UnescapeDataString(S(a, 0) ?? "")));
            Fn("EscapeRegexString", a => DynValue.NewString(System.Text.RegularExpressions.Regex.Escape(S(a, 0) ?? "")));
            Fn("GetLanguageName", a => DynValue.NewString(Languages.GetName(S(a, 0))));
            Fn("GetLanguageId", a => DynValue.NewString(Languages.GetId(S(a, 0))));

            // RegexMatches(input, pattern): every match as a list of its groups (index 0 is the whole match).
            Fn("RegexMatches", a => UserData.Create(new ListUserData(
                System.Text.RegularExpressions.Regex.Matches(S(a, 0) ?? "", S(a, 1) ?? "")
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => (object)new ListUserData(m.Groups.Cast<System.Text.RegularExpressions.Group>().Select(g => (object)g.Value))))));
            Fn("DoEncryptedString", a => throw new EncryptedModuleException());
        }

        private static string DecodeBase64(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            text = text.Trim().Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
    }

    /// <summary>Language code/name lookups for <c>GetLanguageName</c>/<c>GetLanguageId</c> (inferred; the original table is not recoverable).</summary>
    internal static class Languages
    {
        private static readonly System.Collections.Generic.Dictionary<string, string> Overrides =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["all"] = "All",
                ["vn"] = "Vietnamese",
                ["pt-br"] = "Portuguese (Brazil)",
                ["es-la"] = "Spanish (Latin America)",
                ["zh"] = "Chinese (Simplified)",
                ["zh-hk"] = "Chinese (Traditional)",
                ["jp"] = "Japanese",
                ["kr"] = "Korean",
            };

        public static string GetName(string code)
        {
            if (string.IsNullOrEmpty(code))
                return code;

            if (Overrides.TryGetValue(code, out var name))
                return name;

            try
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(code);
                return culture.IsNeutralCulture ? culture.EnglishName : culture.Parent.EnglishName;
            }
            catch (System.Globalization.CultureNotFoundException)
            {
                return code;
            }
        }

        public static string GetId(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            foreach (var pair in Overrides)
                if (pair.Value.Equals(name, StringComparison.OrdinalIgnoreCase) || pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return pair.Key;

            foreach (var culture in System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.NeutralCultures))
                if (culture.EnglishName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return culture.TwoLetterISOLanguageName;

            return name.ToLowerInvariant();
        }
    }
}
