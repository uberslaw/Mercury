namespace Mercury;

public enum JobStatus
{
    Pending,
    Preparing,
    Enumerating,
    Copying,
    Verifying,
    Paused,
    PausedOutsideHours,
    Completed,
    Incomplete,
    Cancelled,
    Failed
}

public enum VerifyLevel
{
    Quick,
    Thorough
}

public enum OverwritePolicy
{
    SkipIfNewerOrEqual,
    Always,
    NeverIfExists
}

public enum SourceKind
{
    File,
    Folder,
    DriveRoot
}

public enum FileCopyStatus
{
    Pending,
    Copied,
    Unpacked,
    Skipped,
    Failed,
    Deferred
}

public enum IssueKind
{
    CopyError,
    Missing,
    SizeMismatch,
    TimestampMismatch,
    HashMismatch
}

public sealed class JobOptions
{
    public double? MaxMegabytesPerSecond { get; set; }
    public bool HoursEnabled { get; set; }
    public TimeOnly HoursStart { get; set; } = new(8, 0);
    public TimeOnly HoursEnd { get; set; } = new(18, 0);
    public int RetryCount { get; set; } = 3;
    public int RetryWaitSeconds { get; set; } = 5;
    public OverwritePolicy Overwrite { get; set; } = OverwritePolicy.SkipIfNewerOrEqual;
    public VerifyLevel Verify { get; set; } = VerifyLevel.Quick;
    public bool DryRun { get; set; }
    public bool IgnoreFreeSpaceCheck { get; set; }
    /// <summary>
    /// Use a stored (no compression) zip as transport (USB / many small files / Catcher HTTPS).
    /// Destination is unpacked to a normal folder tree; the transport zip is deleted.
    /// </summary>
    public bool PackAsZip { get; set; }

    /// <summary>Creation + last-write from source after copy (/COPY:T). Default on — Windows copy otherwise sets dest CreationTime to now.</summary>
    public bool CopyTimestamps { get; set; } = true;
    /// <summary>File attributes from source (/COPY:A). Default on.</summary>
    public bool CopyAttributes { get; set; } = true;
    /// <summary>NTFS DACL (/COPY:S / /SEC). Default off. Fail-soft if denied.</summary>
    public bool CopySecurity { get; set; }
    /// <summary>Owner (/COPY:O). Default off. May need elevation; fail-soft + log.</summary>
    public bool CopyOwner { get; set; }
    /// <summary>Directory creation + last-write (/DCOPY:T). Default on with file timestamps.</summary>
    public bool CopyDirectoryTimestamps { get; set; } = true;
    /// <summary>Create empty source folders at dest (/E vs /S). Default on.</summary>
    public bool CopyEmptyDirectories { get; set; } = true;
    /// <summary>Write-through I/O cousin of /J. Default off. Useful for large video.</summary>
    public bool UnbufferedIo { get; set; }
    /// <summary>Copy symbolic links as links (/SL). Default off: skip reparse (current behaviour), do not follow.</summary>
    public bool CopySymbolicLinksAsLinks { get; set; }
    /// <summary>2-second timestamp compare (/FFT). Default off. Turn on for FAT dest.</summary>
    public bool FatTimestampTolerance { get; set; }
    /// <summary>Skip hidden and system files/folders (/XA:HS). Default off.</summary>
    public bool ExcludeHiddenSystem { get; set; }
    /// <summary>Delete dest files/folders not in source (/PURGE). Default off. Destructive — UI confirms.</summary>
    public bool PurgeExtraDestFiles { get; set; }

    public double? MaxBytesPerSecond =>
        MaxMegabytesPerSecond is > 0 ? MaxMegabytesPerSecond.Value * 1024 * 1024 : null;
}

public sealed class BandwidthSettings
{
    public double? GlobalMinMegabytesPerSecond { get; set; }
    public double? GlobalMaxMegabytesPerSecond { get; set; }
    /// <summary>Cap (MB/s) while the PC is in use. Null or ≤0 disables. Applies live.</summary>
    public double? IdleThrottleMegabytesPerSecond { get; set; }
    /// <summary>Other-process CPU % (Mercury’s own copy excluded when possible) that counts as “in use”.</summary>
    public double IdleCpuPercentThreshold { get; set; } = MachineLoadSampler.DefaultBusyPercent;

    public double? GlobalMinBytesPerSecond =>
        GlobalMinMegabytesPerSecond is > 0 ? GlobalMinMegabytesPerSecond.Value * 1024 * 1024 : null;

    public double? GlobalMaxBytesPerSecond =>
        GlobalMaxMegabytesPerSecond is > 0 ? GlobalMaxMegabytesPerSecond.Value * 1024 * 1024 : null;

    public double? IdleThrottleBytesPerSecond =>
        IdleThrottleMegabytesPerSecond is > 0 ? IdleThrottleMegabytesPerSecond.Value * 1024 * 1024 : null;
}

public sealed class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public SourceKind SourceKind { get; set; }
    public JobOptions Options { get; set; } = new();
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? VolumeSerial { get; set; }
    public string? ResultMessage { get; set; }
    public int IssueCount { get; set; }
    /// <summary>Calendar start. Combined with hours-of-operation if those are enabled.</summary>
    public DateTimeOffset? ScheduledStart { get; set; }
    /// <summary>Queued job stays in list order but is skipped by auto-start until Unhold or Resume.</summary>
    public bool OnHold { get; set; }

    /// <summary>
    /// When set, destination is a Catcher HTTPS listener instead of a folder.
    /// Passphrase is never stored here — see <see cref="CatcherSession"/>.
    /// </summary>
    public CatcherTarget? Catcher { get; set; }

    /// <summary>When the job actually started (including destination probe). Elapsed is wall-clock from this instant.</summary>
    public DateTimeOffset? StartedUtc { get; set; }
    /// <summary>When verify (or cancel/fail) finished.</summary>
    public DateTimeOffset? EndedUtc { get; set; }
    public int SourceFiles { get; set; }
    public int DestFiles { get; set; }
    public int SourceFolders { get; set; }
    public int DestFolders { get; set; }
    /// <summary>Copied + skipped payload accounted by the journal.</summary>
    public long BytesCopied { get; set; }
    public double AverageBytesPerSecond { get; set; }
}

public sealed class JobProgress
{
    public string JobId { get; init; } = "";
    public string JobName { get; init; } = "";
    public JobStatus Status { get; init; }
    public string? CurrentFile { get; init; }
    public long BytesCopied { get; init; }
    public long BytesTotal { get; init; }
    public int FilesCopied { get; init; }
    public int FilesTotal { get; init; }
    public int IssueCount { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan? Eta { get; init; }
    public string? Message { get; init; }
    public bool CloudDestination { get; init; }
    public int StageIndex { get; init; }
    public int StageCount { get; init; }
    public string? StageName { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? StageStartedUtc { get; init; }

    public double Percent =>
        BytesTotal > 0 ? Math.Clamp(100.0 * BytesCopied / BytesTotal, 0, 100) : 0;

    public TimeSpan ElapsedAt(DateTimeOffset now) =>
        Duration(StartedUtc, now);

    public TimeSpan StageElapsedAt(DateTimeOffset now) =>
        Duration(StageStartedUtc, now);

    private static TimeSpan Duration(DateTimeOffset? start, DateTimeOffset now)
    {
        if (start is null)
        {
            return TimeSpan.Zero;
        }

        var elapsed = now - start.Value;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
}

public sealed class FileRecord
{
    public string RelativePath { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string? Hash { get; set; }
    public FileCopyStatus Status { get; set; } = FileCopyStatus.Pending;
    public string? Error { get; set; }
    public int RetryCount { get; set; }
}

public sealed class TransferIssue
{
    public string? RelativePath { get; init; }
    public IssueKind Kind { get; init; }
    public string Message { get; init; } = "";
    public DateTime Utc { get; init; } = DateTime.UtcNow;
}

public sealed class CopyMapping
{
    public SourceKind Kind { get; init; }
    public string SourceRoot { get; init; } = "";
    public string DestRoot { get; init; } = "";
    public bool SingleFile { get; init; }
    public string? SingleFileName { get; init; }
}

public readonly record struct DirectoryRecord(
    string RelativePath,
    string SourcePath,
    string DestPath,
    bool IsSymbolicLink,
    string? LinkTarget);

public sealed class LogEvent
{
    public DateTime Utc { get; init; } = DateTime.UtcNow;
    public string JobId { get; init; } = "";
    public string JobName { get; init; } = "";
    public string Level { get; init; } = "Info";
    public string Message { get; init; } = "";

    public override string ToString() =>
        $"{Utc:HH:mm:ss} [{Level}] [{JobName}] {Message}";
}

public interface IJobLog
{
    void Info(string jobId, string jobName, string message);
    void Error(string jobId, string jobName, string message);
    event Action<LogEvent>? LineWritten;
}

public interface ICopyEngine
{
    Task RunAsync(
        Job job,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken);
}
