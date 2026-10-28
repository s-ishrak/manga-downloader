using System;
using System.Linq;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    /// <summary>A JavaScript engine for modules that must evaluate page scripts. Results come back as text (objects as JSON).</summary>
    [MoonSharpUserData]
    public class JavaScriptUserData
    {
        private readonly Engine engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(10)));

        public JavaScriptUserData(string source)
        {
            // Page scripts expect a browser-ish global object.
            engine.Execute("var window = this, self = this, globalThis = this;");

            if (!string.IsNullOrEmpty(source))
                engine.Execute(source);
        }

        public static JavaScriptUserData New() => new JavaScriptUserData(null);

        public static JavaScriptUserData New(string source) => new JavaScriptUserData(source);

        public string Execute(string code) => Stringify(engine.Evaluate(code));

        public string GetObject(string name) => Stringify(engine.Evaluate(name));

        private string Stringify(JsValue value)
        {
            if (value.IsNull() || value.IsUndefined())
                return null;

            if (value.IsObject())
                return new Jint.Native.Json.JsonSerializer(engine).Serialize(value, JsValue.Undefined, JsValue.Undefined)?.ToString();

            return value.ToString();
        }

        /// <summary>Unpacks Dean Edwards "p,a,c,k,e,d" scripts; anything else is returned unchanged.</summary>
        public static string Deobfuscate(string source)
        {
            var m = Regex.Match(source ?? "", @"\}\('(?<payload>(?:[^'\\]|\\.)*)',\s*(?<radix>\d+),\s*(?<count>\d+),\s*'(?<keys>(?:[^'\\]|\\.)*)'\.split\('\|'\)");

            if (!m.Success)
                return source;

            var payload = m.Groups["payload"].Value.Replace("\\'", "'").Replace("\\\\", "\\");
            var radix = int.Parse(m.Groups["radix"].Value);
            var keys = m.Groups["keys"].Value.Split('|');

            return Regex.Replace(payload, @"\b\w+\b", w =>
            {
                var index = FromBase(w.Value, radix);
                return index >= 0 && index < keys.Length && keys[index].Length > 0 ? keys[index] : w.Value;
            });
        }

        private static int FromBase(string word, int radix)
        {
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var value = 0;

            foreach (var c in word)
            {
                var d = digits.IndexOf(c);

                if (d < 0 || d >= radix)
                    return -1;

                value = value * radix + d;
            }

            return value;
        }

        public override string ToString() => "javascript";
    }
}
