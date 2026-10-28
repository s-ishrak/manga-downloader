using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MoonSharp.Interpreter;
using MangaDownloader.Lua.Api;

namespace MangaDownloader.Lua
{
    /// <summary>Anything that can answer "how many items" so <c>isempty</c> works on it.</summary>
    public interface ICountable
    {
        int Count();
    }

    /// <summary>Conversions between Lua values (as MoonSharp hands them over: string/double/bool/Table/userdata) and host values.</summary>
    internal static class LuaConv
    {
        public static string Num(double d) =>
            d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Text form of a Lua value. Lists are joined with ", " (this is what assigning a list to <c>info.Tags</c> stores).</summary>
        public static string Str(object o)
        {
            switch (o)
            {
                case null: return null;
                case string s: return s;
                case double d: return Num(d);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                case Table t: return string.Join(", ", t.Values.Select(v => Str(v.ToObject())));
                default: return o.ToString();
            }
        }

        public static bool IsEmpty(DynValue v)
        {
            switch (v.Type)
            {
                case DataType.Nil:
                case DataType.Void:
                    return true;
                case DataType.String:
                    return v.String.Length == 0;
                case DataType.Table:
                    return !v.Table.Keys.Any();
                case DataType.UserData:
                    return v.UserData.Object is ICountable c ? c.Count() == 0 : false;
                default:
                    return false;
            }
        }

        public static bool ToBool(DynValue v)
        {
            switch (v.Type)
            {
                case DataType.Nil:
                case DataType.Void:
                    return false;
                case DataType.Boolean:
                    return v.Boolean;
                case DataType.Number:
                    return v.Number != 0;
                case DataType.String:
                    var s = v.String.Trim().ToLowerInvariant();
                    return s == "true" || s == "1" || s == "yes" || s == "on";
                default:
                    return true;
            }
        }

        /// <summary>Flattens a single value, list or Lua array table into its items.</summary>
        public static IEnumerable<object> Items(object o)
        {
            switch (o)
            {
                case null: return Enumerable.Empty<object>();
                case string s: return new object[] { s };
                case Table t: return t.Values.Select(v => v.ToObject()).ToList();
                case IEnumerable<object> e: return e;
                default: return new[] { o };
            }
        }

        public static int ToInt(object o) => o is double d ? (int)d : o is int i ? i : int.Parse(Str(o), CultureInfo.InvariantCulture);
    }
}
