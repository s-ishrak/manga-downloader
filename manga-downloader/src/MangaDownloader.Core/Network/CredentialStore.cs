using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MangaDownloader.Lua.Runtime;

namespace MangaDownloader.Core.Network
{
    /// <summary>
    /// Per-site logins for modules that define <c>Login()</c>, kept in a local JSON file
    /// (<c>{"sites": {"domain": {"username": "...", "password": "..."}}}</c>).
    /// Passwords are encrypted at rest using Windows DPAPI where available.
    /// </summary>
    public sealed class CredentialStore : ICredentialProvider
    {
        private const string DpapiPrefix = "dpapi:";
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

        public string PasswordFor(string domain) => sites.TryGetValue(Normalize(domain), out var c) ? c.Password : null;

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

        private static string EncryptPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
                return password ?? "";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(password);
                    var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                    return DpapiPrefix + Convert.ToBase64String(encrypted);
                }
                catch
                {
                    return password;
                }
            }

            return password;
        }

        private static string DecryptPassword(string storedPassword)
        {
            if (string.IsNullOrEmpty(storedPassword))
                return "";

            if (storedPassword.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var cipherBytes = Convert.FromBase64String(storedPassword.Substring(DpapiPrefix.Length));
                        var decrypted = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
                        return Encoding.UTF8.GetString(decrypted);
                    }
                    catch
                    {
                        return "";
                    }
                }

                return "";
            }

            return storedPassword; // Plaintext legacy format
        }

        private void Load()
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿')))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return;

                if (!doc.RootElement.TryGetProperty("sites", out var s) || s.ValueKind != JsonValueKind.Object)
                    return;

                foreach (var site in s.EnumerateObject())
                {
                    string Str(string name) => site.Value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    var user = Str("username") ?? "";
                    var rawPassword = Str("password") ?? "";
                    sites[Normalize(site.Name)] = (user, DecryptPassword(rawPassword));
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
                        w.WriteString("password", EncryptPassword(site.Value.Password));
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
