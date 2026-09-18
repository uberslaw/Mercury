using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mercury;

public sealed class JobJournal : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public string Directory { get; }
    public string DatabasePath { get; }

    private JobJournal(string directory, SqliteConnection connection)
    {
        Directory = directory;
        DatabasePath = Path.Combine(directory, "job.db");
        _connection = connection;
    }

    public static JobJournal Create(string directory, Job job)
    {
        System.IO.Directory.CreateDirectory(directory);
        var db = Path.Combine(directory, "job.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = db }.ToString();
        var connection = new SqliteConnection(cs);
        connection.Open();
        var journal = new JobJournal(directory, connection);
        journal.InitSchema();
        journal.SaveJob(job);
        return journal;
    }

    public static JobJournal Open(string directory)
    {
        var db = Path.Combine(directory, "job.db");
        if (!File.Exists(db))
        {
            throw new FileNotFoundException("Job journal not found.", db);
        }

        var cs = new SqliteConnectionStringBuilder { DataSource = db }.ToString();
        var connection = new SqliteConnection(cs);
        connection.Open();
        var journal = new JobJournal(directory, connection);
        journal.InitSchema();
        return journal;
    }

    public static bool Exists(string directory) =>
        File.Exists(Path.Combine(directory, "job.db"));

    private void InitSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS meta (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS files (
              relative_path TEXT PRIMARY KEY,
              source_path TEXT NOT NULL,
              dest_path TEXT NOT NULL,
              size INTEGER NOT NULL,
              last_write_utc TEXT NOT NULL,
              hash TEXT,
              status TEXT NOT NULL,
              error TEXT,
              retry_count INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS issues (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              relative_path TEXT,
              kind TEXT NOT NULL,
              message TEXT NOT NULL,
              utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void SaveJob(Job job)
    {
        SetMeta("job", JsonSerializer.Serialize(job, Json));
        SetMeta("status", job.Status.ToString());
    }

    public Job LoadJob()
    {
        var json = GetMeta("job") ?? throw new InvalidOperationException("Job metadata missing from journal.");
        return JsonSerializer.Deserialize<Job>(json, Json) ?? throw new InvalidOperationException("Job metadata is invalid.");
    }

    public void UpsertFile(FileRecord record)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO files (relative_path, source_path, dest_path, size, last_write_utc, hash, status, error, retry_count)
                VALUES ($rel, $src, $dst, $size, $lw, $hash, $status, $error, $retry)
                ON CONFLICT(relative_path) DO UPDATE SET
                  source_path = excluded.source_path,
                  dest_path = excluded.dest_path,
                  size = excluded.size,
                  last_write_utc = excluded.last_write_utc,
                  hash = COALESCE(excluded.hash, files.hash),
                  status = excluded.status,
                  error = excluded.error,
                  retry_count = excluded.retry_count;
                """;
            cmd.Parameters.AddWithValue("$rel", record.RelativePath);
            cmd.Parameters.AddWithValue("$src", record.SourcePath);
            cmd.Parameters.AddWithValue("$dst", record.DestPath);
            cmd.Parameters.AddWithValue("$size", record.Size);
            cmd.Parameters.AddWithValue("$lw", record.LastWriteUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$hash", (object?)record.Hash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", record.Status.ToString());
            cmd.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$retry", record.RetryCount);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkCopied(string relativePath, string? hash)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE files SET status = 'Copied', hash = COALESCE($hash, hash), error = NULL WHERE relative_path = $rel";
            cmd.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rel", relativePath);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkAllCopied(IReadOnlyDictionary<string, string?>? hashes = null)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET status = 'Copied', error = NULL WHERE status IN ('Pending','Failed')";
                cmd.ExecuteNonQuery();
            }

            if (hashes is { Count: > 0 })
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET hash = $hash WHERE relative_path = $rel";
                var hashP = cmd.Parameters.Add("$hash", SqliteType.Text);
                var relP = cmd.Parameters.Add("$rel", SqliteType.Text);
                foreach (var (rel, hash) in hashes)
                {
                    hashP.Value = (object?)hash ?? DBNull.Value;
                    relP.Value = rel;
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    public void MarkUnpacked(string relativePath)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE files SET status = 'Unpacked', error = NULL WHERE relative_path = $rel";
            cmd.Parameters.AddWithValue("$rel", relativePath);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkSkipped(string relativePath, string reason)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE files SET status = 'Skipped', error = $err WHERE relative_path = $rel";
            cmd.Parameters.AddWithValue("$err", reason);
            cmd.Parameters.AddWithValue("$rel", relativePath);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkFailed(string relativePath, string error, int retryCount)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE files SET status = 'Failed', error = $err, retry_count = $retry WHERE relative_path = $rel";
            cmd.Parameters.AddWithValue("$err", error);
            cmd.Parameters.AddWithValue("$retry", retryCount);
            cmd.Parameters.AddWithValue("$rel", relativePath);
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkDeferred(string relativePath, string error, int retryCount)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE files SET status = 'Deferred', error = $err, retry_count = $retry WHERE relative_path = $rel";
            cmd.Parameters.AddWithValue("$err", error);
            cmd.Parameters.AddWithValue("$retry", retryCount);
            cmd.Parameters.AddWithValue("$rel", relativePath);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetMetaInt(string key, int value) => SetMeta(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void SetHeartbeat(JobHeartbeatState state)
    {
        SetMeta("heartbeat_dirty", state.Dirty ? "1" : "0");
        SetMeta("heartbeat_percent", state.Percent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        SetMeta("heartbeat_file", state.File ?? "");
        SetMeta("heartbeat_utc", state.Utc.ToString("O"));
        Checkpoint();
    }

    public void ClearHeartbeat()
    {
        SetMeta("heartbeat_dirty", "0");
        SetMeta("heartbeat_percent", "0");
        SetMeta("heartbeat_file", "");
        SetMeta("heartbeat_utc", "");
        Checkpoint();
    }

    public JobHeartbeatState? ReadHeartbeat(string jobId)
    {
        var dirty = GetMeta("heartbeat_dirty");
        if (dirty != "1")
        {
            return null;
        }

        var percentText = GetMeta("heartbeat_percent");
        _ = double.TryParse(
            percentText,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var percent);
        var utcText = GetMeta("heartbeat_utc");
        var utc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(utcText) &&
            DateTimeOffset.TryParse(utcText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            utc = parsed;
        }

        var file = GetMeta("heartbeat_file");
        return new JobHeartbeatState
        {
            JobId = jobId,
            Dirty = true,
            Percent = percent,
            File = string.IsNullOrWhiteSpace(file) ? null : file,
            Utc = utc
        };
    }

    private void Checkpoint()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // WAL checkpoint is best-effort; sidecar is the durable dirty flag
        }
    }

    public int GetMetaInt(string key)
    {
        var text = GetMeta(key);
        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : 0;
    }

    public void AddIssue(TransferIssue issue)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO issues (relative_path, kind, message, utc) VALUES ($rel, $kind, $msg, $utc)";
            cmd.Parameters.AddWithValue("$rel", (object?)issue.RelativePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", issue.Kind.ToString());
            cmd.Parameters.AddWithValue("$msg", issue.Message);
            cmd.Parameters.AddWithValue("$utc", issue.Utc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<FileRecord> GetFiles(FileCopyStatus? status = null)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = status is null
                ? "SELECT relative_path, source_path, dest_path, size, last_write_utc, hash, status, error, retry_count FROM files"
                : "SELECT relative_path, source_path, dest_path, size, last_write_utc, hash, status, error, retry_count FROM files WHERE status = $status";
            if (status is not null)
            {
                cmd.Parameters.AddWithValue("$status", status.ToString());
            }

            var list = new List<FileRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadFile(reader));
            }

            return list;
        }
    }

    public FileTotals Totals()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                  COUNT(*) as n,
                  COALESCE(SUM(size), 0) as bytes,
                  COALESCE(SUM(CASE WHEN status IN ('Copied','Unpacked','Skipped') THEN 1 ELSE 0 END), 0) as done,
                  COALESCE(SUM(CASE WHEN status IN ('Copied','Unpacked','Skipped') THEN size ELSE 0 END), 0) as doneBytes,
                  COALESCE(SUM(CASE WHEN status = 'Failed' THEN 1 ELSE 0 END), 0) as failed
                FROM files
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new FileTotals(
                reader.GetInt32(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt32(4));
        }
    }

    public int IssueCount()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM issues";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public IReadOnlyList<TransferIssue> GetIssues()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT relative_path, kind, message, utc FROM issues ORDER BY id";
            var list = new List<TransferIssue>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TransferIssue
                {
                    RelativePath = reader.IsDBNull(0) ? null : reader.GetString(0),
                    Kind = Enum.Parse<IssueKind>(reader.GetString(1)),
                    Message = reader.GetString(2),
                    Utc = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)
                });
            }

            return list;
        }
    }

    private void SetMeta(string key, string value)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    private string? GetMeta(string key)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    private static FileRecord ReadFile(SqliteDataReader reader) =>
        new()
        {
            RelativePath = reader.GetString(0),
            SourcePath = reader.GetString(1),
            DestPath = reader.GetString(2),
            Size = reader.GetInt64(3),
            LastWriteUtc = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Hash = reader.IsDBNull(5) ? null : reader.GetString(5),
            Status = Enum.Parse<FileCopyStatus>(reader.GetString(6)),
            Error = reader.IsDBNull(7) ? null : reader.GetString(7),
            RetryCount = reader.GetInt32(8)
        };

    public void Dispose() => _connection.Dispose();
}

public readonly record struct FileTotals(int Files, long Bytes, int DoneFiles, long DoneBytes, int Failed);
