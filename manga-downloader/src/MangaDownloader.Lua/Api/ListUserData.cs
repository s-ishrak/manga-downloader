using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    /// <summary>
    /// The list type behind <c>List.New()</c>, <c>SelectValues</c>, <c>split</c> and friends.
    /// Items are 0-indexed and directly iterable from Lua (<c>for x in list do</c>), as in the original host.
    /// </summary>
    [MoonSharpUserData]
    public class ListUserData : IEnumerable<object>, ICountable
    {
        protected readonly List<object> Items = new List<object>();

        public ListUserData()
        {
        }

        public ListUserData(IEnumerable<object> items)
        {
            Items.AddRange(items);
        }

        public static ListUserData New() => new ListUserData();

        public virtual void Add(object item) => Items.Add(item);

        public virtual void Add(object item, object extra) => Add(item);

        public virtual void AddRange(object items)
        {
            foreach (var item in LuaConv.Items(items).ToList())
                Add(item);
        }

        public virtual bool Contains(object item) => Items.Any(i => Equals(i, item));

        public virtual void Clear() => Items.Clear();

        public virtual void Reverse() => Items.Reverse();

        public virtual void Sort() => Items.Sort((a, b) => string.CompareOrdinal(LuaConv.Str(a), LuaConv.Str(b)));

        public int Count() => Items.Count;

        public object First() => Items.Count > 0 ? Items[0] : null;

        public object Last() => Items.Count > 0 ? Items[Items.Count - 1] : null;

        public string join(object separator = null) => string.Join(LuaConv.Str(separator) ?? ",", Items.Select(LuaConv.Str));

        /// <summary>0-based element access; nil when out of range.</summary>
        public object this[object index]
        {
            get
            {
                var i = LuaConv.ToInt(index);
                return i >= 0 && i < Items.Count ? Items[i] : null;
            }
        }

        [MoonSharpUserDataMetamethod("__len")]
        public int Length() => Items.Count;

        public IEnumerator<object> GetEnumerator() => Items.ToList().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public IReadOnlyList<object> Snapshot() => Items.ToList();

        public override string ToString() => string.Join(", ", Items.Select(LuaConv.Str));
    }

    /// <summary>String -> value map used for headers, POST data, cookies and per-module/per-page data.</summary>
    [MoonSharpUserData]
    public class DictUserData : ICountable
    {
        private readonly Dictionary<string, object> items = new Dictionary<string, object>();

        public static DictUserData New() => new DictUserData();

        public void Add(string key, object value) => items[key] = value;

        public void Clear() => items.Clear();

        public bool Contains(string key) => items.ContainsKey(key);

        public int Count() => items.Count;

        public object this[string key]
        {
            get => items.TryGetValue(key, out var v) ? v : null;
            set => items[key] = value;
        }

        public IEnumerable<KeyValuePair<string, object>> Entries => items.ToList();

        public override string ToString() => string.Join("; ", items.Select(i => i.Key + "=" + LuaConv.Str(i.Value)));
    }
}
