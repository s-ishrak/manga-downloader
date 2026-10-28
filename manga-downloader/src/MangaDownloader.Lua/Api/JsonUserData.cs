using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MoonSharp.Interpreter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MangaDownloader.Lua.Api
{
    /// <summary>JSON value with JSONPath selection; arrays and objects are iterable.</summary>
    [MoonSharpUserData]
    public class JsonUserData : IEnumerable<object>, ICountable
    {
        private readonly JToken token;

        public JsonUserData(JToken token)
        {
            this.token = token;
        }

        public static JsonUserData New(object source)
        {
            if (source is Table table)
                return new JsonUserData(FromTable(table));

            var text = LuaConv.Str(source);

            if (string.IsNullOrWhiteSpace(text))
                return null;

            return new JsonUserData(JToken.Parse(text));
        }

        public static string Escape(string text)
        {
            var quoted = JsonConvert.ToString(text ?? "");
            return quoted.Substring(1, quoted.Length - 2);
        }

        public static string ConvertTableToJson(Table table) => FromTable(table).ToString(Formatting.None);

        private static JToken FromTable(Table table)
        {
            // A table whose keys are exactly 1..n is an array; anything else is an object.
            var keys = table.Keys.ToList();
            var isArray = keys.Count > 0 && keys.All(k => k.Type == DataType.Number) &&
                          Enumerable.Range(1, keys.Count).All(i => table.Get(i).Type != DataType.Nil);

            if (isArray)
                return new JArray(table.Values.Select(v => FromValue(v)));

            var obj = new JObject();
            foreach (var pair in table.Pairs)
                obj[LuaConv.Str(pair.Key.ToObject())] = FromValue(pair.Value);
            return obj;
        }

        private static JToken FromValue(DynValue v)
        {
            switch (v.Type)
            {
                case DataType.Table: return FromTable(v.Table);
                case DataType.Number: return new JValue(v.Number == Math.Floor(v.Number) ? (object)(long)v.Number : v.Number);
                case DataType.Boolean: return new JValue(v.Boolean);
                case DataType.String: return new JValue(v.String);
                default: return JValue.CreateNull();
            }
        }

        private static string Text(JToken t)
        {
            switch (t)
            {
                case null: return null;
                case JValue v when v.Type == JTokenType.Null: return null;
                case JValue v when v.Type == JTokenType.Boolean: return (bool)v.Value ? "true" : "false";
                case JValue v when v.Value is double || v.Value is float || v.Value is decimal:
                    return Convert.ToDouble(v.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                case JValue v: return Convert.ToString(v.Value, CultureInfo.InvariantCulture);
                case JProperty p: return Text(p.Value);
                default: return t.ToString(Formatting.None);
            }
        }

        private JToken Inner => token is JProperty p ? p.Value : token;

        private static JsonUserData Wrap(JToken t) => t == null ? null : new JsonUserData(t);

        public JsonUserData SelectToken(string path) => Wrap(Inner.SelectToken(path));

        public JsonUserData SelectNode(string path) => SelectToken(path);

        public ListUserData SelectTokens(string path) => new ListUserData(Inner.SelectTokens(path).Select(t => (object)new JsonUserData(t)));

        public ListUserData SelectNodes(string path) => SelectTokens(path);

        public string SelectValue(string path) => Text(Inner.SelectToken(path));

        public ListUserData SelectValues(string path) => new ListUserData(Inner.SelectTokens(path).Select(t => (object)Text(t)));

        public string Key => token is JProperty p ? p.Name : (token.Parent is JProperty parent ? parent.Name : null);

        public string Value => Text(Inner);

        /// <summary>Property names of an object (empty for anything else).</summary>
        public ListUserData Keys => new ListUserData(Inner is JObject o ? o.Properties().Select(p => (object)p.Name) : Enumerable.Empty<object>());

        public int Count() => Inner is JContainer c ? c.Count : 0;

        public void Clear() => (Inner as JContainer)?.RemoveAll();

        public void Reverse()
        {
            if (Inner is JArray a)
            {
                var items = a.Reverse().ToList();
                a.Clear();
                foreach (var i in items)
                    a.Add(i);
            }
        }

        /// <summary><c>json["name"]</c> or 0-based <c>json[0]</c>; nil when missing.</summary>
        public object this[object key]
        {
            get
            {
                var v = Inner;

                if (key is string name)
                    return v is JObject o ? Wrap(o.Property(name)?.Value) : null;

                var i = LuaConv.ToInt(key);
                return v is JArray a && i >= 0 && i < a.Count ? Wrap(a[i]) : null;
            }
        }

        public IEnumerator<object> GetEnumerator()
        {
            switch (Inner)
            {
                case JObject o: return o.Properties().Select(p => (object)new JsonUserData(p)).ToList().GetEnumerator();
                case JArray a: return a.Select(t => (object)new JsonUserData(t)).ToList().GetEnumerator();
                default: return Enumerable.Empty<object>().GetEnumerator();
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        [MoonSharpUserDataMetamethod("__len")]
        public int Length() => Count();

        public override string ToString() => Text(Inner) ?? "";
    }
}
