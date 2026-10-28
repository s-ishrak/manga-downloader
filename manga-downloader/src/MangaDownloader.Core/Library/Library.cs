using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MangaDownloader.Core.Library
{
    public enum QueueStatus
    {
        Queued,
        Downloading,
        Completed,
        Failed,
        Stopped,
    }

    public sealed class QueueItem
    {
        public long Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public QueueStatus Status { get; set; }
        public int PagesDone { get; set; }
        public int PagesTotal { get; set; }

        /// <summary>Overall completion in permille (0-1000).</summary>
        public int Progress { get; set; }

        /// <summary>Chapters finished / found so far, summed over every gallery the item turned into (0 before the download starts).</summary>
        public int ChaptersDone { get; set; }
        public int ChaptersTotal { get; set; }
        public string Error { get; set; }
        public List<string> Outputs { get; set; } = new List<string>();
        public DateTime AddedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public long Position { get; set; }

        /// <summary>Where the item came from: "cli", "web", "import", "tracker".</summary>
        public string Source { get; set; }
    }

    public sealed class HistoryEntry
    {
        public long Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public QueueStatus Status { get; set; }
        public int Pages { get; set; }
        public List<string> Outputs { get; set; } = new List<string>();
        public string Error { get; set; }
        public DateTime FinishedAt { get; set; }
    }

    public sealed class TrackedSeries
    {
        public long Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public List<string> KnownChapters { get; set; } = new List<string>();
        public DateTime? LastChecked { get; set; }

        /// <summary>New chapters found by the last check.</summary>
        public int NewCount { get; set; }

        /// <summary>Queue the series again whenever new chapters appear.</summary>
        public bool AutoDownload { get; set; }

        public string LastError { get; set; }
    }

    /// <summary>
    /// The persistent state: download queue, history and tracked series, in one SQLite file (WAL, so the web UI and a CLI
    /// can read while the runner writes). One connection guarded by a lock; every operation is short.
    /// </summary>
    public sealed class LibraryDb : IDisposable
    {
        private const int SchemaVersion = 2;

        private readonly SqliteConnection db;
        private readonly object gate = new object();

        public LibraryDb(string path)
        {
            if (path != ":memory:")
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));

            db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            db.Open();

            Exec("PRAGMA journal_mode=WAL;");
            Migrate();
        }

        public void Dispose() => db.Dispose();

        private void Migrate()
        {
            Exec(@"CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);");
            var version = Scalar<long?>("SELECT CAST(value AS INTEGER) FROM meta WHERE key='schema_version'") ?? 0;

            if (version < 1)
            {
                Exec(@"
CREATE TABLE queue (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    url TEXT NOT NULL,
    url_key TEXT NOT NULL UNIQUE,
    title TEXT,
    status TEXT NOT NULL,
    pages_done INTEGER NOT NULL DEFAULT 0,
    pages_total INTEGER NOT NULL DEFAULT 0,
    error TEXT,
    outputs TEXT,
    added_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    position INTEGER NOT NULL,
    source TEXT
);
CREATE INDEX queue_status ON queue(status, position);
CREATE TABLE history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    url TEXT NOT NULL,
    title TEXT,
    status TEXT NOT NULL,
    pages INTEGER NOT NULL DEFAULT 0,
    outputs TEXT,
    error TEXT,
    finished_at TEXT NOT NULL
);
CREATE TABLE tracked (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    url TEXT NOT NULL,
    url_key TEXT NOT NULL UNIQUE,
    title TEXT,
    chapters TEXT,
    last_checked TEXT,
    new_count INTEGER NOT NULL DEFAULT 0,
    auto_download INTEGER NOT NULL DEFAULT 0,
    last_error TEXT
);
INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', '1');");
            }

            if (version < 2)
            {
                // overall completion in permille (0-1000), so a multi-chapter download does not look finished after its first chapter
                Exec("ALTER TABLE queue ADD COLUMN progress INTEGER NOT NULL DEFAULT 0;");
                Exec("INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', '2');");
            }

            if (version < 3)
            {
                // a series downloads chapter by chapter, and its page total only grows as each chapter starts, so chapters say more
                Exec("ALTER TABLE queue ADD COLUMN chapters_done INTEGER NOT NULL DEFAULT 0;");
                Exec("ALTER TABLE queue ADD COLUMN chapters_total INTEGER NOT NULL DEFAULT 0;");
                Exec("INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', '3');");
            }
        }

        // ---- queue ---------------------------------------------------------------------------------------------

        /// <summary>URLs are compared without scheme, <c>www.</c>, fragment and trailing slash (HDoujin's IgnoreSchemeWhenComparingUrls).</summary>
        public static string UrlKey(string url)
        {
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
                return (url ?? "").Trim().ToLowerInvariant();

            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www."))
                host = host.Substring(4);

            return host + uri.PathAndQuery.TrimEnd('/');
        }

        /// <summary>
        /// Adds a URL to the queue. A URL already queued or downloading is left alone; one that finished, failed or was stopped is queued again.
        /// </summary>
        public QueueItem Enqueue(string url, string title = null, string source = null, QueueStatus status = QueueStatus.Queued, int pagesDone = 0, int pagesTotal = 0)
        {
            var key = UrlKey(url);

            lock (gate)
            {
                var existing = Query("SELECT * FROM queue WHERE url_key=$k", Map(QueueFrom), ("$k", key)).FirstOrDefault();

                if (existing != null)
                {
                    if (existing.Status == QueueStatus.Queued || existing.Status == QueueStatus.Downloading)
                        return existing;

                    Exec("UPDATE queue SET status=$s, error=NULL, pages_done=0, pages_total=0, progress=0, chapters_done=0, chapters_total=0, updated_at=$t, position=$p WHERE id=$id",
                        ("$s", status.ToString()), ("$t", Now()), ("$p", NextPosition()), ("$id", existing.Id));
                    return Get(existing.Id);
                }

                Exec(@"INSERT INTO queue(url, url_key, title, status, pages_done, pages_total, added_at, updated_at, position, source)
                       VALUES ($u, $k, $title, $s, $d, $n, $t, $t, $p, $src)",
                    ("$u", url.Trim()), ("$k", key), ("$title", title), ("$s", status.ToString()), ("$d", pagesDone), ("$n", pagesTotal),
                    ("$t", Now()), ("$p", NextPosition()), ("$src", source));

                return Get(Scalar<long>("SELECT last_insert_rowid()"));
            }
        }

        public QueueItem Get(long id)
        {
            lock (gate)
                return Query("SELECT * FROM queue WHERE id=$id", Map(QueueFrom), ("$id", id)).FirstOrDefault();
        }

        public IList<QueueItem> ListQueue()
        {
            lock (gate)
                return Query("SELECT * FROM queue ORDER BY position, id", Map(QueueFrom));
        }

        /// <summary>The oldest queued item, or null.</summary>
        public QueueItem NextQueued()
        {
            lock (gate)
                return Query("SELECT * FROM queue WHERE status='Queued' ORDER BY position, id LIMIT 1", Map(QueueFrom)).FirstOrDefault();
        }

        public void SetStatus(long id, QueueStatus status, string error = null, IEnumerable<string> outputs = null)
        {
            lock (gate)
                Exec("UPDATE queue SET status=$s, error=$e, outputs=COALESCE($o, outputs), updated_at=$t WHERE id=$id",
                    ("$s", status.ToString()), ("$e", error), ("$o", outputs == null ? null : JsonSerializer.Serialize(outputs)), ("$t", Now()), ("$id", id));
        }

        public void SetProgress(long id, int done, int total, string title = null, int? permille = null, int? chaptersDone = null, int? chaptersTotal = null)
        {
            lock (gate)
                Exec("UPDATE queue SET pages_done=$d, pages_total=$n, title=COALESCE($title, title), progress=COALESCE($p, progress), " +
                     "chapters_done=COALESCE($cd, chapters_done), chapters_total=COALESCE($ct, chapters_total), updated_at=$t WHERE id=$id",
                    ("$d", done), ("$n", total), ("$title", title), ("$p", permille), ("$cd", chaptersDone), ("$ct", chaptersTotal), ("$t", Now()), ("$id", id));
        }

        public bool RemoveFromQueue(long id)
        {
            lock (gate)
                return Exec("DELETE FROM queue WHERE id=$id", ("$id", id)) > 0;
        }

        public int ClearCompleted()
        {
            lock (gate)
                return Exec("DELETE FROM queue WHERE status='Completed'");
        }

        /// <summary>After a crash or kill, anything marked Downloading goes back to Queued so the next run resumes it.</summary>
        public int ResetInterrupted()
        {
            lock (gate)
                return Exec("UPDATE queue SET status='Queued', updated_at=$t WHERE status='Downloading'", ("$t", Now()));
        }

        // ---- history -------------------------------------------------------------------------------------------

        public void AddHistory(string url, string title, QueueStatus status, int pages, IEnumerable<string> outputs, string error)
        {
            lock (gate)
                Exec("INSERT INTO history(url, title, status, pages, outputs, error, finished_at) VALUES ($u, $title, $s, $p, $o, $e, $t)",
                    ("$u", url), ("$title", title), ("$s", status.ToString()), ("$p", pages),
                    ("$o", JsonSerializer.Serialize(outputs ?? new string[0])), ("$e", error), ("$t", Now()));
        }

        public IList<HistoryEntry> ListHistory(int limit = 200)
        {
            lock (gate)
                return Query("SELECT * FROM history ORDER BY id DESC LIMIT $n", Map(HistoryFrom), ("$n", limit));
        }

        public int ClearHistory()
        {
            lock (gate)
                return Exec("DELETE FROM history");
        }

        // ---- tracked series ------------------------------------------------------------------------------------

        public TrackedSeries AddTracked(string url, string title, IEnumerable<string> knownChapters, bool autoDownload = false)
        {
            var key = UrlKey(url);

            lock (gate)
            {
                var existing = Query("SELECT * FROM tracked WHERE url_key=$k", Map(TrackedFrom), ("$k", key)).FirstOrDefault();

                if (existing != null)
                    return existing;

                Exec("INSERT INTO tracked(url, url_key, title, chapters, last_checked, auto_download) VALUES ($u, $k, $title, $c, $t, $a)",
                    ("$u", url.Trim()), ("$k", key), ("$title", title), ("$c", JsonSerializer.Serialize(knownChapters.ToList())), ("$t", Now()), ("$a", autoDownload ? 1 : 0));
                return GetTracked(Scalar<long>("SELECT last_insert_rowid()"));
            }
        }

        public TrackedSeries GetTracked(long id)
        {
            lock (gate)
                return Query("SELECT * FROM tracked WHERE id=$id", Map(TrackedFrom), ("$id", id)).FirstOrDefault();
        }

        public IList<TrackedSeries> ListTracked()
        {
            lock (gate)
                return Query("SELECT * FROM tracked ORDER BY title COLLATE NOCASE, id", Map(TrackedFrom));
        }

        public void UpdateTracked(long id, IEnumerable<string> knownChapters, int newCount, string error, string title = null)
        {
            lock (gate)
                Exec("UPDATE tracked SET chapters=COALESCE($c, chapters), new_count=$n, last_checked=$t, last_error=$e, title=COALESCE($title, title) WHERE id=$id",
                    ("$c", knownChapters == null ? null : JsonSerializer.Serialize(knownChapters.ToList())), ("$n", newCount), ("$t", Now()), ("$e", error), ("$title", title), ("$id", id));
        }

        public void SetAutoDownload(long id, bool enabled)
        {
            lock (gate)
                Exec("UPDATE tracked SET auto_download=$a WHERE id=$id", ("$a", enabled ? 1 : 0), ("$id", id));
        }

        public bool RemoveTracked(long id)
        {
            lock (gate)
                return Exec("DELETE FROM tracked WHERE id=$id", ("$id", id)) > 0;
        }

        // ---- plumbing ------------------------------------------------------------------------------------------

        private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        private long NextPosition() => (Scalar<long?>("SELECT MAX(position) FROM queue") ?? 0) + 1;

        private int Exec(string sql, params (string Name, object Value)[] args)
        {
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = sql;
                Bind(cmd, args);
                return cmd.ExecuteNonQuery();
            }
        }

        private T Scalar<T>(string sql, params (string Name, object Value)[] args)
        {
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = sql;
                Bind(cmd, args);
                var value = cmd.ExecuteScalar();
                return value == null || value is DBNull ? default(T) : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
            }
        }

        private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object Value)[] args)
        {
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = sql;
                Bind(cmd, args);

                var result = new List<T>();

                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        result.Add(map(r));

                return result;
            }
        }

        private static void Bind(SqliteCommand cmd, (string Name, object Value)[] args)
        {
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        private static Func<SqliteDataReader, T> Map<T>(Func<SqliteDataReader, T> f) => f;

        private static string Str(SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetString(r.GetOrdinal(column));

        private static DateTime Date(SqliteDataReader r, string column) =>
            DateTime.Parse(r.GetString(r.GetOrdinal(column)), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        private static List<string> StringList(string json) => string.IsNullOrEmpty(json) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(json);

        private static QueueItem QueueFrom(SqliteDataReader r) => new QueueItem
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            Url = Str(r, "url"),
            Title = Str(r, "title"),
            Status = (QueueStatus)Enum.Parse(typeof(QueueStatus), Str(r, "status")),
            PagesDone = r.GetInt32(r.GetOrdinal("pages_done")),
            PagesTotal = r.GetInt32(r.GetOrdinal("pages_total")),
            Progress = r.GetInt32(r.GetOrdinal("progress")),
            ChaptersDone = r.GetInt32(r.GetOrdinal("chapters_done")),
            ChaptersTotal = r.GetInt32(r.GetOrdinal("chapters_total")),
            Error = Str(r, "error"),
            Outputs = StringList(Str(r, "outputs")),
            AddedAt = Date(r, "added_at"),
            UpdatedAt = Date(r, "updated_at"),
            Position = r.GetInt64(r.GetOrdinal("position")),
            Source = Str(r, "source"),
        };

        private static HistoryEntry HistoryFrom(SqliteDataReader r) => new HistoryEntry
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            Url = Str(r, "url"),
            Title = Str(r, "title"),
            Status = (QueueStatus)Enum.Parse(typeof(QueueStatus), Str(r, "status")),
            Pages = r.GetInt32(r.GetOrdinal("pages")),
            Outputs = StringList(Str(r, "outputs")),
            Error = Str(r, "error"),
            FinishedAt = Date(r, "finished_at"),
        };

        private static TrackedSeries TrackedFrom(SqliteDataReader r) => new TrackedSeries
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            Url = Str(r, "url"),
            Title = Str(r, "title"),
            KnownChapters = StringList(Str(r, "chapters")),
            LastChecked = Str(r, "last_checked") == null ? (DateTime?)null : Date(r, "last_checked"),
            NewCount = r.GetInt32(r.GetOrdinal("new_count")),
            AutoDownload = r.GetInt32(r.GetOrdinal("auto_download")) != 0,
            LastError = Str(r, "last_error"),
        };
    }
}
