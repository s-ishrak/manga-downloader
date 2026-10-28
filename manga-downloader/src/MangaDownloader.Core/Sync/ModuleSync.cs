using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MangaDownloader.Net;

namespace MangaDownloader.Core.Sync
{
    /// <summary>One entry of HDoujin's <c>repositories.json</c>.</summary>
    public sealed class RepositorySource
    {
        public string Name { get; set; }
        public string RemotePath { get; set; }
        public int Priority { get; set; }
        public bool SynchronizeAutomatically { get; set; } = true;
        public List<string> DisabledModules { get; } = new List<string>();

        /// <summary>Entries in priority order (lower number wins); "Internal"-style entries without a remote path are dropped.</summary>
        public static IList<RepositorySource> Load(string path)
        {
            var result = new List<RepositorySource>();

            using (var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿')))
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string Str(string name) => item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

                    var source = new RepositorySource
                    {
                        Name = Str("name"),
                        RemotePath = Str("remotePath"),
                        Priority = item.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : 0,
                    };

                    if (item.TryGetProperty("settings", out var settings) && settings.TryGetProperty("synchronizeAutomatically", out var auto) &&
                        (auto.ValueKind == JsonValueKind.True || auto.ValueKind == JsonValueKind.False))
                        source.SynchronizeAutomatically = auto.GetBoolean();

                    if (item.TryGetProperty("disabledModules", out var disabled) && disabled.ValueKind == JsonValueKind.Array)
                        source.DisabledModules.AddRange(disabled.EnumerateArray().Select(d => d.GetString()));

                    if (!string.IsNullOrEmpty(source.RemotePath))
                        result.Add(source);
                }
            }

            return result.OrderBy(r => r.Priority).ToList();
        }
    }

    /// <summary>A folder inside a GitHub repository: <c>https://github.com/{owner}/{repo}/tree/{ref}/{path}</c>.</summary>
    public sealed class GitHubLocation
    {
        public string Owner { get; set; }
        public string Repo { get; set; }
        public string Ref { get; set; }
        public string Path { get; set; } = "";

        public static GitHubLocation Parse(string url)
        {
            var m = Regex.Match(url ?? "", @"^https://github\.com/(?<o>[^/]+)/(?<r>[^/]+?)(?:\.git)?(?:/tree/(?<ref>[^/]+)(?:/(?<p>.*?))?)?/?$");

            if (!m.Success)
                throw new FormatException("Not a GitHub repository folder URL: " + url);

            return new GitHubLocation
            {
                Owner = m.Groups["o"].Value,
                Repo = m.Groups["r"].Value,
                Ref = m.Groups["ref"].Success ? m.Groups["ref"].Value : null,
                Path = m.Groups["p"].Success ? m.Groups["p"].Value.Trim('/') : "",
            };
        }

        public string ContentsApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/contents/{Path}" + (Ref != null ? "?ref=" + Uri.EscapeDataString(Ref) : "");

        public override string ToString() => $"{Owner}/{Repo}@{Ref ?? "default"}:{Path}";
    }

    public sealed class SyncResult
    {
        public List<string> Added { get; } = new List<string>();
        public List<string> Updated { get; } = new List<string>();
        public List<string> Removed { get; } = new List<string>();
        public List<string> Failed { get; } = new List<string>();
        public int Unchanged { get; set; }

        public bool Changed => Added.Count + Updated.Count + Removed.Count > 0;
    }

    /// <summary>
    /// Mirrors the <c>*.lua</c> files of a GitHub folder into a local directory. Only changed files are downloaded (compared by git blob SHA),
    /// every download is verified against the SHA GitHub reported, and only files this tool put there earlier are ever deleted.
    /// </summary>
    public sealed class ModuleSync
    {
        private const string ManifestName = ".sync.json";
        private static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_.\- ]*\.lua$");

        private readonly IHttpBackend http;
        private readonly Action<string> log;

        public ModuleSync(IHttpBackend http, Action<string> log = null)
        {
            this.http = http;
            this.log = log ?? (_ => { });
        }

        public SyncResult Sync(GitHubLocation source, string localDirectory)
        {
            Directory.CreateDirectory(localDirectory);

            var remote = ListRemote(source);
            var manifest = LoadManifest(localDirectory);
            var result = new SyncResult();
            var next = new Dictionary<string, string>();

            foreach (var file in remote)
            {
                var local = Path.Combine(localDirectory, file.Name);
                var exists = File.Exists(local);

                if (exists && GitBlobSha(File.ReadAllBytes(local)) == file.Sha)
                {
                    result.Unchanged++;
                    next[file.Name] = file.Sha;
                    continue;
                }

                try
                {
                    Fetch(file, local);
                    next[file.Name] = file.Sha;
                    (exists ? result.Updated : result.Added).Add(file.Name);
                    log((exists ? "updated " : "added   ") + file.Name);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    result.Failed.Add(file.Name + ": " + e.Message);

                    // Keep tracking what is already on disk so a failed update never orphans it.
                    if (exists && manifest.ContainsKey(file.Name))
                        next[file.Name] = manifest[file.Name];
                }
            }

            // Delete only what an earlier sync created and upstream has since dropped.
            var remoteNames = new HashSet<string>(remote.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var name in manifest.Keys.Where(k => !remoteNames.Contains(k)))
            {
                var local = Path.Combine(localDirectory, name);

                if (SafeName.IsMatch(name) && File.Exists(local))
                {
                    File.Delete(local);
                    result.Removed.Add(name);
                    log("removed " + name);
                }
            }

            SaveManifest(localDirectory, source, next);
            return result;
        }

        private sealed class RemoteFile
        {
            public string Name;
            public string Sha;
            public string DownloadUrl;
        }

        private List<RemoteFile> ListRemote(GitHubLocation source)
        {
            var request = new HttpRequest { Url = source.ContentsApiUrl };
            request.Headers["Accept"] = "application/vnd.github+json";
            request.Headers["User-Agent"] = "manga-downloader";

            var response = http.Send(request);

            if (response.StatusCode == 403 || response.StatusCode == 429)
                throw new IOException("GitHub rate limit reached listing " + source + "; try again later.");

            if (response.StatusCode != 200)
                throw new IOException($"GitHub returned HTTP {response.StatusCode} listing {source}.");

            var files = new List<RemoteFile>();

            using (var doc = JsonDocument.Parse(response.Body))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException(source + " is not a folder.");

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string Str(string name) => item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

                    var name = Str("name");

                    if (Str("type") != "file" || name == null || !SafeName.IsMatch(name))
                        continue;

                    var url = Str("download_url");

                    // Only ever download from GitHub's raw host, whatever the listing says.
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "raw.githubusercontent.com")
                        continue;

                    files.Add(new RemoteFile { Name = name, Sha = Str("sha"), DownloadUrl = url });
                }
            }

            return files;
        }

        private void Fetch(RemoteFile file, string destination)
        {
            var temp = destination + ".download";

            try
            {
                HttpResult result;

                using (var stream = File.Create(temp))
                    result = http.Download(new HttpRequest { Url = file.DownloadUrl }, stream);

                if (result.StatusCode != 200)
                    throw new IOException("HTTP " + result.StatusCode);

                if (GitBlobSha(File.ReadAllBytes(temp)) != file.Sha)
                    throw new InvalidDataException("checksum mismatch (download corrupted or changed mid-sync)");

                if (File.Exists(destination))
                    File.Delete(destination);

                File.Move(temp, destination);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        /// <summary>The SHA-1 git assigns a file: <c>sha1("blob " + length + "\0" + content)</c>.</summary>
        public static string GitBlobSha(byte[] content)
        {
            var header = Encoding.ASCII.GetBytes("blob " + content.Length + "\0");

            using (var sha = SHA1.Create())
            {
                sha.TransformBlock(header, 0, header.Length, null, 0);
                sha.TransformFinalBlock(content, 0, content.Length);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }

        private static Dictionary<string, string> LoadManifest(string directory)
        {
            var path = Path.Combine(directory, ManifestName);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(path))
                return files;

            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                    if (doc.RootElement.TryGetProperty("files", out var f))
                        foreach (var p in f.EnumerateObject())
                            files[p.Name] = p.Value.GetString();
            }
            catch (JsonException)
            {
                // A damaged manifest only costs us the ability to delete stale files.
            }

            return files;
        }

        private static void SaveManifest(string directory, GitHubLocation source, Dictionary<string, string> files)
        {
            using (var stream = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteString("source", source.ToString());
                    w.WriteString("lastSynced", DateTime.UtcNow.ToString("o"));
                    w.WriteStartObject("files");

                    foreach (var f in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                        w.WriteString(f.Key, f.Value);

                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                File.WriteAllBytes(Path.Combine(directory, ManifestName), stream.ToArray());
            }
        }
    }
}
