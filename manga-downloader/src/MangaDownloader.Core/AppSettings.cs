using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MangaDownloader.Core.Download;

namespace MangaDownloader.Core
{
    /// <summary>
    /// Settings changed in the web UI, kept in <c>settings.json</c> in the config folder so they survive a restart. Every field is optional:
    /// a missing one falls back to the built-in default, and a command-line flag overrides the file for that run.
    /// </summary>
    public sealed class AppSettings
    {
        public const int MaxParallel = 32;
        public const int MaxRetry = 20;
        public const int MaxDelayMilliseconds = 60000;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        public string OutputDirectory { get; set; }

        /// <summary>cbz, zip, pdf or folder.</summary>
        public string Format { get; set; }

        /// <summary>Pages of one gallery downloaded at the same time.</summary>
        public int? Parallel { get; set; }
        public int? Retry { get; set; }
        public int? DelayMilliseconds { get; set; }
        public bool? KeepImages { get; set; }
        public bool? Overwrite { get; set; }

        /// <summary>Multi-chapter series: one archive for the whole series instead of one per chapter.</summary>
        public bool? CombineChapters { get; set; }

        /// <summary>Galleries downloaded at the same time.</summary>
        public int? Concurrent { get; set; }

        /// <summary>Minutes between automatic checks of tracked series; 0 = never.</summary>
        public int? CheckEveryMinutes { get; set; }

        public static AppSettings Load(string path)
        {
            if (!File.Exists(path))
                return new AppSettings();

            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'), Json) ?? new AppSettings();
            }
            catch (JsonException e)
            {
                throw new FormatException($"{path} is not valid settings JSON ({e.Message}). Fix or delete it.");
            }
        }

        /// <summary>Writes to a temporary file first so a crash never leaves a half-written settings file.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }

        public static SaveFormat ParseFormat(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "cbz": return SaveFormat.Cbz;
                case "zip": return SaveFormat.Zip;
                case "pdf": return SaveFormat.Pdf;
                case "folder": return SaveFormat.Folder;
                default: throw new FormatException("format must be cbz, zip, pdf or folder.");
            }
        }
    }
}
