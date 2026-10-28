using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MangaDownloader.Lua.Runtime;

namespace MangaDownloader.Core.Network
{
    /// <summary>
    /// Per-site logins for modules that define <c>Login()</c>, kept in a local JSON file
    /// (<c>{"sites": {"domain": {"username": "...", "password": "..."}}}</c>). The password is stored in plain text, so keep the file private.
    /// </summary>
    public sealed class CredentialStore : ICredentialProvider
    {
        private readonly string path;
        private readonly Dictionary<string, (string User, string Password)> sites = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        public CredentialStore(string path)
        {
            this.path = path;

            if (File.Exists(path))
                Load();
        }

        public IEnumerable<string> Domains => sites.Keys.OrderBy(k => k);

        public string UserFor(string domain) => sites.TryGetValue(Normalize(domain), out var c) ? c.User : null;

        public void Set(string domain, string username, string password)
        {
            sites[Normalize(domain)] = (username, password);
            Save();
        }

        public bool Remove(string domain)
        {
            var removed = sites.Remove(Normalize(domain));

            if (removed)
                Save();

            return removed;
        }

        /// <summary>Exact domain first, then its parent domains (credentials for example.com also serve m.example.com).</summary>
        public bool TryGet(string domain, string moduleName, out string username, out string password)
        {
            var labels = Normalize(domain).Split('.');

            for (var i = 0; i < labels.Length - 1; i++)
            {
                if (sites.TryGetValue(string.Join(".", labels.Skip(i)), out var c))
                {
                    username = c.User;
                    password = c.Password;
                    return true;
                }
            }

            username = password = null;
            return false;
        }

        private static string Normalize(string domain) => (domain ?? "").Trim().ToLowerInvariant().TrimStart('.');

        private void Load()
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿')))
            {
                if (!doc.RootElement.TryGetProperty("sites", out var s) || s.ValueKind != JsonValueKind.Object)
                    return;

                foreach (var site in s.EnumerateObject())
                {
                    string Str(string name) => site.Value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    sites[Normalize(site.Name)] = (Str("username") ?? "", Str("password") ?? "");
                }
            }
        }

        private void Save()
        {
            using (var stream = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteStartObject("sites");

                    foreach (var site in sites.OrderBy(k => k.Key))
                    {
                        w.WriteStartObject(site.Key);
                        w.WriteString("username", site.Value.User);
                        w.WriteString("password", site.Value.Password);
                        w.WriteEndObject();
                    }

                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllBytes(path, stream.ToArray());
            }
        }
    }
}
