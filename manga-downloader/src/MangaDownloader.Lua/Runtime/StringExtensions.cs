using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MangaDownloader.Lua.Api;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Runtime
{
    /// <summary>
    /// The string methods HDoujin adds to Lua strings (<c>("s"):after(":")</c>). Not-found cases return the input
    /// unchanged for after/before/between, matching Gsemac.Text.
    /// </summary>
    internal static class StringExtensions
    {
        public static void Register(Script script)
        {
            var table = script.Globals.Get("string").Table;

            Add(table, "regex", (s, a) => Regex(s, a.Str(1), a.Has(2) ? a.Int(2) : (int?)null));
            Add(table, "regexmany", (s, a) => RegexMany(s, a.Str(1), a.Has(2) ? a.Int(2) : (int?)null));
            Add(table, "contains", (s, a) => DynValue.NewBoolean(s.IndexOf(a.Str(1) ?? "", StringComparison.Ordinal) >= 0));
            Add(table, "startswith", (s, a) => DynValue.NewBoolean(s.StartsWith(a.Str(1) ?? "", StringComparison.Ordinal)));
            Add(table, "endswith", (s, a) => DynValue.NewBoolean(s.EndsWith(a.Str(1) ?? "", StringComparison.Ordinal)));
            Add(table, "trim", (s, a) => DynValue.NewString(a.Has(1) ? s.Trim(a.Str(1).ToCharArray()) : s.Trim()));
            Add(table, "after", (s, a) => DynValue.NewString(After(s, a.Str(1), last: false)));
            Add(table, "afterlast", (s, a) => DynValue.NewString(After(s, a.Str(1), last: true)));
            Add(table, "before", (s, a) => DynValue.NewString(Before(s, a.Str(1), last: false)));
            Add(table, "beforelast", (s, a) => DynValue.NewString(Before(s, a.Str(1), last: true)));
            Add(table, "between", (s, a) => DynValue.NewString(Between(s, a.Str(1), a.Str(2))));
            Add(table, "replace", (s, a) => DynValue.NewString(string.IsNullOrEmpty(a.Str(1)) ? s : s.Replace(a.Str(1), a.Str(2) ?? "")));
            Add(table, "split", (s, a) => DynValue.FromObject(script, Split(s, a.Str(1))));
            Add(table, "title", (s, a) => DynValue.NewString(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s)));
            Add(table, "first", (s, a) => DynValue.NewString(s.Substring(0, Math.Min(s.Length, a.Has(1) ? a.Int(1) : 1))));
            Add(table, "last", (s, a) => DynValue.NewString(s.Substring(Math.Max(0, s.Length - (a.Has(1) ? a.Int(1) : 1)))));
        }

        private static void Add(Table table, string name, Func<string, Args, DynValue> body)
        {
            table[name] = DynValue.NewCallback((ctx, args) =>
            {
                var self = args[0];
                var s = self.Type == DataType.String ? self.String : LuaConv.Str(self.ToObject()) ?? "";
                return body(s, new Args(args));
            });
        }

        private static DynValue Regex(string input, string pattern, int? group)
        {
            var m = System.Text.RegularExpressions.Regex.Match(input, pattern);

            if (!m.Success)
                return DynValue.NewString("");

            // Without an explicit group: first capture group if the pattern has one, else the whole match.
            var g = group ?? (m.Groups.Count > 1 ? 1 : 0);
            return DynValue.NewString(m.Groups[g].Value);
        }

        private static DynValue RegexMany(string input, string pattern, int? group)
        {
            var list = new ListUserData();

            foreach (Match m in System.Text.RegularExpressions.Regex.Matches(input, pattern))
                list.Add(m.Groups[group ?? (m.Groups.Count > 1 ? 1 : 0)].Value);

            return UserData.Create(list);
        }

        private static string After(string s, string sub, bool last)
        {
            if (string.IsNullOrEmpty(sub))
                return s;

            var i = last ? s.LastIndexOf(sub, StringComparison.Ordinal) : s.IndexOf(sub, StringComparison.Ordinal);
            return i < 0 ? s : s.Substring(i + sub.Length);
        }

        private static string Before(string s, string sub, bool last)
        {
            if (string.IsNullOrEmpty(sub))
                return s;

            var i = last ? s.LastIndexOf(sub, StringComparison.Ordinal) : s.IndexOf(sub, StringComparison.Ordinal);
            return i < 0 ? s : s.Substring(0, i);
        }

        private static string Between(string s, string start, string end)
        {
            var from = s.IndexOf(start ?? "", StringComparison.Ordinal);

            if (from < 0)
                return s;

            from += (start ?? "").Length;
            var to = s.IndexOf(end ?? "", from, StringComparison.Ordinal);
            return to < 0 ? s : s.Substring(from, to - from);
        }

        private static ListUserData Split(string s, string separator) =>
            new ListUserData((string.IsNullOrEmpty(separator) ? new[] { s } : s.Split(new[] { separator }, StringSplitOptions.None)).Cast<object>());

        /// <summary>Typed view of the callback arguments (index 0 is the string itself).</summary>
        private readonly struct Args
        {
            private readonly CallbackArguments args;

            public Args(CallbackArguments args)
            {
                this.args = args;
            }

            public bool Has(int i) => args.Count > i && args[i].Type != DataType.Nil && args[i].Type != DataType.Void;

            public string Str(int i) => Has(i) ? (args[i].Type == DataType.String ? args[i].String : LuaConv.Str(args[i].ToObject())) : null;

            public int Int(int i) => (int)args[i].CastToNumber().GetValueOrDefault();
        }
    }
}
