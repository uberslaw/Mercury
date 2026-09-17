using Microsoft.Data.Sqlite;

namespace Mercury;

public sealed class TransferHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    public JobStatus Status { get; set; }
    public string? ResultMessage { get; set; }
    public int SourceFiles { get; set; }
    public int DestFiles { get; set; }
    public int SourceFolders { get; set; }
    public int DestFolders { get; set; }
    public long BytesCopied { get; set; }
    public double AverageBytesPerSecond { get; set; }
    public int IssueCount { get; set; }

    public Job ToJob() =>
        new()
        {
            Id = JobId,
            Name = Name,
            SourcePath = SourcePath,
            DestinationPath = DestinationPath,
            Status = Status,
            ResultMessage = ResultMessage,
            StartedUtc = StartedUtc,
            EndedUtc = EndedUtc,
            SourceFiles = SourceFiles,
            DestFiles = DestFiles,
            SourceFolders = SourceFolders,
            DestFolders = DestFolders,
            BytesCopied = BytesCopied,
            AverageBytesPerSecond = AverageBytesPerSecond,
            IssueCount = IssueCount
        };

    public static TransferHistoryEntry FromJob(Job job) =>
        new()
        {
            JobId = job.Id,
            Name = string.IsNullOrWhiteSpace(job.Name) ? job.Id : job.Name,
            SourcePath = job.SourcePath,
            DestinationPath = job.DestinationPath,
            StartedUtc = job.StartedUtc,
            EndedUtc = job.EndedUtc,
            Status = job.Status,
            ResultMessage = job.ResultMessage,
            SourceFiles = job.SourceFiles,
            DestFiles = job.DestFiles,
            SourceFolders = job.SourceFolders,
            DestFolders = job.DestFolders,
            BytesCopied = job.BytesCopied,
            AverageBytesPerSecond = job.AverageBytesPerSecond,
            IssueCount = job.IssueCount
        };
}

public static class HistoryStore
{
    public static IReadOnlyList<TransferHistoryEntry> Load(AppPaths paths)
    {
        using var db = Open(paths);
        InitSchema(db);
        ImportFromJobJournals(paths, db);
        return Query(db);
    }

    public static void Record(AppPaths paths, Job job)
    {
        if (job.Status is not (JobStatus.Completed or JobStatus.Incomplete or JobStatus.Cancelled or JobStatus.Failed))
        {
            return;
        }

        if (job.StartedUtc is null)
        {
            return;
        }

        using var db = Open(paths);
        InitSchema(db);
        Insert(db, TransferHistoryEntry.FromJob(job));
    }

    private static SqliteConnection Open(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DataRoot);
        var cs = new SqliteConnectionStringBuilder { DataSource = paths.HistoryFile }.ToString();
        var connection = new SqliteConnection(cs);
        connection.Open();
        return connection;
    }

    private static void InitSchema(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS history (
              id TEXT PRIMARY KEY,
              job_id TEXT NOT NULL,
              name TEXT NOT NULL,
              source_path TEXT NOT NULL,
              destination_path TEXT NOT NULL,
              started_utc TEXT,
              ended_utc TEXT,
              status TEXT NOT NULL,
              result_message TEXT,
              source_files INTEGER NOT NULL DEFAULT 0,
              dest_files INTEGER NOT NULL DEFAULT 0,
              source_folders INTEGER NOT NULL DEFAULT 0,
              dest_folders INTEGER NOT NULL DEFAULT 0,
              bytes_copied INTEGER NOT NULL DEFAULT 0,
              average_bytes_per_second REAL NOT NULL DEFAULT 0,
              issue_count INTEGER NOT NULL DEFAULT 0,
              recorded_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection db, TransferHistoryEntry entry)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (
              id, job_id, name, source_path, destination_path,
              started_utc, ended_utc, status, result_message,
              source_files, dest_files, source_folders, dest_folders,
              bytes_copied, average_bytes_per_second, issue_count, recorded_utc)
            VALUES (
              $id, $job, $name, $src, $dst,
              $started, $ended, $status, $result,
              $sf, $df, $sfo, $dfo,
              $bytes, $avg, $issues, $recorded);
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$job", entry.JobId);
        cmd.Parameters.AddWithValue("$name", entry.Name);
        cmd.Parameters.AddWithValue("$src", entry.SourcePath);
        cmd.Parameters.AddWithValue("$dst", entry.DestinationPath);
        cmd.Parameters.AddWithValue("$started", (object?)entry.StartedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ended", (object?)entry.EndedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", entry.Status.ToString());
        cmd.Parameters.AddWithValue("$result", (object?)entry.ResultMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sf", entry.SourceFiles);
        cmd.Parameters.AddWithValue("$df", entry.DestFiles);
        cmd.Parameters.AddWithValue("$sfo", entry.SourceFolders);
        cmd.Parameters.AddWithValue("$dfo", entry.DestFolders);
        cmd.Parameters.AddWithValue("$bytes", entry.BytesCopied);
        cmd.Parameters.AddWithValue("$avg", entry.AverageBytesPerSecond);
        cmd.Parameters.AddWithValue("$issues", entry.IssueCount);
        cmd.Parameters.AddWithValue("$recorded", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static List<TransferHistoryEntry> Query(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, job_id, name, source_path, destination_path,
                   started_utc, ended_utc, status, result_message,
                   source_files, dest_files, source_folders, dest_folders,
                   bytes_copied, average_bytes_per_second, issue_count
            FROM history
            ORDER BY COALESCE(ended_utc, started_utc, recorded_utc) DESC, recorded_utc DESC
            """;
        var list = new List<TransferHistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TransferHistoryEntry
            {
                Id = reader.GetString(0),
                JobId = reader.GetString(1),
                Name = reader.GetString(2),
                SourcePath = reader.GetString(3),
                DestinationPath = reader.GetString(4),
                StartedUtc = ParseOffset(reader, 5),
                EndedUtc = ParseOffset(reader, 6),
                Status = Enum.Parse<JobStatus>(reader.GetString(7)),
                ResultMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                SourceFiles = reader.GetInt32(9),
                DestFiles = reader.GetInt32(10),
                SourceFolders = reader.GetInt32(11),
                DestFolders = reader.GetInt32(12),
                BytesCopied = reader.GetInt64(13),
                AverageBytesPerSecond = reader.GetDouble(14),
                IssueCount = reader.GetInt32(15)
            });
        }

        return list;
    }

    private static void ImportFromJobJournals(AppPaths paths, SqliteConnection db)
    {
        if (!Directory.Exists(paths.Jobs))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(paths.Jobs))
        {
            if (!JobJournal.Exists(dir))
            {
                continue;
            }

            try
            {
                using var journal = JobJournal.Open(dir);
                var job = journal.LoadJob();
                if (job.StartedUtc is null)
                {
                    continue;
                }

                if (job.Status is not (JobStatus.Completed or JobStatus.Incomplete
                    or JobStatus.Cancelled or JobStatus.Failed))
                {
                    continue;
                }

                if (Exists(db, job.Id, job.StartedUtc))
                {
                    continue;
                }

                Insert(db, TransferHistoryEntry.FromJob(job));
            }
            catch
            {
                // skip unreadable journals
            }
        }
    }

    private static bool Exists(SqliteConnection db, string jobId, DateTimeOffset? startedUtc)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = startedUtc is null
            ? "SELECT COUNT(*) FROM history WHERE job_id = $job AND started_utc IS NULL"
            : "SELECT COUNT(*) FROM history WHERE job_id = $job AND started_utc = $started";
        cmd.Parameters.AddWithValue("$job", jobId);
        if (startedUtc is not null)
        {
            cmd.Parameters.AddWithValue("$started", startedUtc.Value.ToString("O"));
        }

        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static DateTimeOffset? ParseOffset(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.Parse(reader.GetString(ordinal), null,
            System.Globalization.DateTimeStyles.RoundtripKind);
    }
}
