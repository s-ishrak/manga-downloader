using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MangaDownloader.Net
{
    public sealed class CookieEntry
    {
        public string Domain { get; set; }
        public string Path { get; set; } = "/";
        public string Name { get; set; }
        public string Value { get; set; }

        /// <summary>UTC expiry; null for session cookies.</summary>
        public DateTime? Expires { get; set; }

        public bool IsExpired(DateTime now) => Expires.HasValue && Expires.Value <= now;
    }

    /// <summary>
    /// Thread-safe cookie store with domain/path matching. Saved in HDoujin's <c>cookies.json</c> shape
    /// (<c>[{Domain, Expires, Name, Value, Path}]</c>) so files can be exchanged both ways. Session cookies are not saved.
    /// </summary>
    public sealed class CookieJar
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly object gate = new object();
        private readonly List<CookieEntry> cookies = new List<CookieEntry>();

        public int Count
        {
            get
            {
                lock (gate)
                    return cookies.Count;
            }
        }

        public IList<CookieEntry> Snapshot()
        {
            lock (gate)
                return cookies.Select(Clone).ToList();
        }

        public void Set(CookieEntry cookie)
        {
            if (string.IsNullOrEmpty(cookie.Name) || string.IsNullOrEmpty(cookie.Domain))
                return;

            var entry = Clone(cookie);
            entry.Domain = entry.Domain.ToLowerInvariant();
            entry.Path = string.IsNullOrEmpty(entry.Path) ? "/" : entry.Path;

            lock (gate)
            {
                cookies.RemoveAll(c => c.Name == entry.Name && c.Domain == entry.Domain && c.Path == entry.Path);

                // A Set-Cookie that is already expired is a deletion.
                if (!entry.IsExpired(DateTime.UtcNow))
                    cookies.Add(entry);
            }
        }

        public void Set(string domain, string name, string value) => Set(new CookieEntry { Domain = domain, Name = name, Value = value });

        public void Clear()
        {
            lock (gate)
                cookies.Clear();
        }

        public int RemoveDomain(string domain)
        {
            domain = domain.TrimStart('.').ToLowerInvariant();

            lock (gate)
                return cookies.RemoveAll(c => c.Domain.TrimStart('.') == domain);
        }

        /// <summary>Cookies to send to <paramref name="url"/>, more specific paths first as browsers do (later wins on a name clash).</summary>
        public IDictionary<string, string> CookiesFor(Uri url)
        {
            var now = DateTime.UtcNow;
            var host = url.Host.ToLowerInvariant();
            var path = string.IsNullOrEmpty(url.AbsolutePath) ? "/" : url.AbsolutePath;
            var result = new Dictionary<string, string>();

            lock (gate)
            {
                foreach (var c in cookies.Where(c => !c.IsExpired(now) && DomainMatches(host, c.Domain) && PathMatches(path, c.Path)).OrderBy(c => c.Path.Length))
                    result[c.Name] = c.Value;
            }

            return result;
        }

        public IDictionary<string, string> CookiesFor(string host) => CookiesFor(new Uri("https://" + host + "/"));

        private static bool DomainMatches(string host, string cookieDomain)
        {
            var d = cookieDomain.TrimStart('.');
            return host == d || host.EndsWith("." + d, StringComparison.Ordinal);
        }

        private static bool PathMatches(string requestPath, string cookiePath) =>
            cookiePath == "/" || requestPath == cookiePath || requestPath.StartsWith(cookiePath.TrimEnd('/') + "/", StringComparison.Ordinal);

        private static CookieEntry Clone(CookieEntry c) =>
            new CookieEntry { Domain = c.Domain, Path = c.Path, Name = c.Name, Value = c.Value, Expires = c.Expires };

        // ---- persistence -------------------------------------------------------------------------------------------

        /// <summary>Merges the cookies of a <c>cookies.json</c> file; returns how many were imported (expired ones are skipped).</summary>
        public int Load(string path)
        {
            if (!File.Exists(path))
                return 0;

            var imported = 0;

            using (var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿')))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException(path + " is not a cookie list.");

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string Str(string name) => item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

                    var cookie = new CookieEntry { Domain = Str("Domain"), Name = Str("Name"), Value = Str("Value") ?? "", Path = Str("Path") ?? "/" };
                    var expires = Str("Expires");

                    if (expires != null && DateTime.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var e) && e > Epoch)
                        cookie.Expires = e;

                    if (cookie.Name == null || cookie.Domain == null || cookie.IsExpired(DateTime.UtcNow))
                        continue;

                    Set(cookie);
                    imported++;
                }
            }

            return imported;
        }

        /// <summary>Writes the persistent (non-session) cookies. The file is replaced atomically.</summary>
        public void Save(string path)
        {
            List<CookieEntry> persistent;
            var now = DateTime.UtcNow;

            lock (gate)
                persistent = cookies.Where(c => c.Expires.HasValue && !c.IsExpired(now)).Select(Clone).ToList();

            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartArray();

                    foreach (var c in persistent.OrderBy(c => c.Domain).ThenBy(c => c.Name))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("Domain", c.Domain);
                        writer.WriteString("Expires", c.Expires.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                        writer.WriteString("Name", c.Name);
                        writer.WriteString("Value", c.Value);
                        writer.WriteString("Path", c.Path);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                Directory.CreateDirectory(directory);

                var temp = path + ".tmp";
                File.WriteAllBytes(temp, stream.ToArray());

                if (File.Exists(path))
                    File.Delete(path);

                File.Move(temp, path);
            }
        }
    }

    public static class SetCookieParser
    {
        /// <summary>Parses one <c>Set-Cookie</c> header received from <paramref name="requestUrl"/>; null when it has no usable name.</summary>
        public static CookieEntry Parse(string header, Uri requestUrl)
        {
            var parts = header.Split(';');
            var first = parts[0];
            var eq = first.IndexOf('=');

            if (eq <= 0)
                return null;

            var cookie = new CookieEntry
            {
                Name = first.Substring(0, eq).Trim(),
                Value = first.Substring(eq + 1).Trim(),
                Domain = requestUrl.Host,
                Path = DefaultPath(requestUrl),
            };

            DateTime? maxAgeExpiry = null;

            foreach (var attribute in parts.Skip(1))
            {
                var i = attribute.IndexOf('=');
                var key = (i < 0 ? attribute : attribute.Substring(0, i)).Trim().ToLowerInvariant();
                var value = i < 0 ? "" : attribute.Substring(i + 1).Trim();

                switch (key)
                {
                    case "domain" when value.Length > 0:
                        cookie.Domain = value.TrimStart('.');
                        break;
                    case "path" when value.StartsWith("/"):
                        cookie.Path = value;
                        break;
                    case "expires" when DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date):
                        cookie.Expires = date;
                        break;
                    case "max-age" when long.TryParse(value, out var seconds):
                        maxAgeExpiry = DateTime.UtcNow.AddSeconds(seconds);
                        break;
                }
            }

            if (maxAgeExpiry.HasValue)
                cookie.Expires = maxAgeExpiry; // Max-Age wins over Expires

            return cookie;
        }

        private static string DefaultPath(Uri url)
        {
            var path = url.AbsolutePath;
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? "/" : path.Substring(0, slash);
        }
    }
}
