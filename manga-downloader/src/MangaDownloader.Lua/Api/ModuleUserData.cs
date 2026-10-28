using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    /// <summary><c>module.Domains</c>: <c>Add(domain[, name])</c>, iterable, <c>First()</c>.</summary>
    [MoonSharpUserData]
    public class DomainListUserData : ListUserData
    {
        private readonly Dictionary<string, string> names = new Dictionary<string, string>();

        public override void Add(object domain) => Add(domain, null);

        /// <summary>The second argument is a display name for that domain (used when one file serves several sites).</summary>
        public override void Add(object domain, object name)
        {
            var d = LuaConv.Str(domain);

            if (d == null || Items.Contains(d))
                return;

            Items.Add(d);

            if (name != null)
                names[d] = LuaConv.Str(name);
        }

        public string NameOf(string domain) => names.TryGetValue(domain, out var n) ? n : null;

        public IEnumerable<string> All => Items.Cast<string>().ToList();
    }

    [MoonSharpUserData]
    public class SettingUserData
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string DefaultValue { get; set; }
        public string Value { get; set; }
        public string ToolTip { get; set; }
        public bool MultiSelect { get; set; }
        public ListUserData Options { get; } = new ListUserData();

        public SettingUserData WithToolTip(string tip)
        {
            ToolTip = tip;
            return this;
        }

        public SettingUserData WithMultiSelect()
        {
            MultiSelect = true;
            return this;
        }
    }

    [MoonSharpUserData]
    public class SettingListUserData : ICountable
    {
        private readonly List<SettingUserData> settings = new List<SettingUserData>();

        public IReadOnlyList<SettingUserData> All => settings;

        private SettingUserData AddSetting(string kind, string name, string value)
        {
            settings.RemoveAll(s => s.Name == name);

            var setting = new SettingUserData { Name = name, Kind = kind, DefaultValue = value, Value = value };
            settings.Add(setting);
            return setting;
        }

        public SettingUserData AddCheck(string name, bool value) => AddSetting("check", name, value ? "true" : "false");

        public SettingUserData AddText(string name, string value) => AddSetting("text", name, value);

        public SettingUserData AddNumeric(string name, double value) => AddSetting("numeric", name, LuaConv.Num(value));

        public SettingUserData AddChoice(string name, string defaultValue, Table options)
        {
            var setting = AddSetting("choice", name, defaultValue);

            if (options != null)
                setting.Options.AddRange(options);

            return setting;
        }

        public bool Contains(string name) => settings.Any(s => s.Name == name);

        public string GetValue(string name) => settings.FirstOrDefault(s => s.Name == name)?.Value;

        public int Count() => settings.Count;

        /// <summary>Values are strings ("true", "5"); modules wrap them with <c>toboolean</c>/<c>tonumber</c>.</summary>
        public string this[string name]
        {
            get => GetValue(name);
            set
            {
                var s = settings.FirstOrDefault(x => x.Name == name);
                if (s != null)
                    s.Value = value;
            }
        }
    }

    [MoonSharpUserData]
    public class RateLimitListUserData
    {
        public List<(string Domain, int Requests, int Milliseconds)> Rules { get; } = new List<(string, int, int)>();

        public void Add(string domain, int requests, int milliseconds) => Rules.Add((domain, requests, milliseconds));
    }

    [MoonSharpUserData]
    public class ModuleUserData
    {
        private string domain;
        private string name;

        internal Func<ModuleUserData> Factory { get; set; }

        /// <summary>An additional module in the same file (<c>module.New()</c>, same as <c>Module.New()</c>).</summary>
        public ModuleUserData New() => Factory();

        public object Name { get => name; set => name = LuaConv.Str(value); }

        /// <summary>The domain the current URL matched; defaults to the first registered one.</summary>
        public object Domain
        {
            get => domain ?? Domains.First();
            set => domain = LuaConv.Str(value);
        }

        public DomainListUserData Domains { get; } = new DomainListUserData();

        public string Language { get; set; }

        public bool Adult { get; set; }

        public string Type { get; set; }

        public bool Strict { get; set; }

        public bool IsGeneric { get; set; }

        public bool DeferHttpRequests { get; set; }

        public SettingListUserData Settings { get; } = new SettingListUserData();

        public RateLimitListUserData RateLimits { get; } = new RateLimitListUserData();

        public DictUserData Cookies { get; } = new DictUserData();

        public DictUserData Data { get; } = new DictUserData();

        public string GetName() => name ?? Domains.First() as string;

        public string GetName(string forDomain) => Domains.NameOf(forDomain) ?? GetName();

        public override string ToString() => GetName() ?? "";
    }
}
