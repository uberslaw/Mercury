using System.Globalization;
using System.Text.Json;

namespace Mercury;

public sealed record JobHeartbeatState
{
    public string JobId { get; init; } = "";
    public bool Dirty { get; init; }
    public double Percent { get; init; }
    public string? File { get; init; }
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public DateTimeOffset Utc { get; init; }
}

public enum DirtyResumeDecision
{
    Offer,
    SkipKeep,
    SkipClear
}

/// <summary>
/// Running-job dirty flag so a kill, crash, or reboot can offer resume on next launch.
/// Written to journal meta and a sidecar file flushed with write-through.
/// </summary>
public static class JobHeartbeat
{
    public const string SidecarName = "heartbeat.json";
    public static readonly TimeSpan LivePulseAge = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string SidecarPath(string jobDirectory) =>
        Path.Combine(jobDirectory, SidecarName);

    public static JobHeartbeatState Create(
        string jobId,
        double percent,
        string? file,
        string? sourcePath = null,
        string? destinationPath = null) =>
        new()
        {
            JobId = jobId,
            Dirty = true,
            Percent = Math.Clamp(percent, 0, 100),
            File = file,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            Utc = DateTimeOffset.UtcNow
        };

    public static void Write(
        JobJournal journal,
        string jobId,
        double percent,
        string? file,
        string? sourcePath = null,
        string? destinationPath = null)
    {
        var state = Create(jobId, percent, file, sourcePath, destinationPath);
        journal.SetHeartbeat(state);
        WriteSidecar(journal.Directory, state);
    }

    public static void Write(JobJournal journal, Job job, double percent, string? file) =>
        Write(journal, job.Id, percent, file, JobSources.Display(job), job.DestinationPath);

    public static bool HasCopyProgress(double percent, string? file, Job? job) =>
        percent >= 0.05
        || !string.IsNullOrWhiteSpace(file)
        || job is { BytesCopied: > 0 }
        || job is { DestFiles: > 0 };

    public static bool HasCopyProgress(JobHeartbeatState state, Job? job) =>
        HasCopyProgress(state.Percent, state.File, job);

    public static bool IsAbandonedZero(JobHeartbeatState state, Job? job)
    {
        if (HasCopyProgress(state, job))
        {
            return false;
        }

        if (job is null)
        {
            return true;
        }

        return job.Status is JobStatus.Incomplete or JobStatus.Cancelled or JobStatus.Failed
            or JobStatus.Pending;
    }

    public static bool IsFreshPulse(JobHeartbeatState state, DateTimeOffset now) =>
        now - state.Utc <= LivePulseAge && state.Utc <= now + TimeSpan.FromSeconds(5);

    public static Job? FindOtherRealWork(IEnumerable<Job> queue, string dirtyJobId)
    {
        foreach (var job in queue)
        {
            if (string.Equals(job.Id, dirtyJobId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (job.Status is JobStatus.Preparing or JobStatus.Enumerating or JobStatus.Copying
                or JobStatus.Verifying or JobStatus.Paused or JobStatus.PausedOutsideHours)
            {
                return job;
            }

            if (job.BytesCopied > 0 || job.DestFiles > 0 || job.SourceFiles > 0)
            {
                return job;
            }
        }

        return null;
    }

    /// <summary>
    /// Offer only a crash/kill mid-copy. Skip a leftover 0% start, a stopped job with
    /// no files, or a heartbeat that is not the queue's real work. Keep a heartbeat that
    /// another process is still pulsing.
    /// </summary>
    public static DirtyResumeDecision Decide(
        JobHeartbeatState dirty,
        Job? job,
        IEnumerable<Job>? queue,
        DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        if (IsFreshPulse(dirty, clock) && HasCopyProgress(dirty, job))
        {
            return DirtyResumeDecision.SkipKeep;
        }

        if (queue is not null && FindOtherRealWork(queue, dirty.JobId) is not null)
        {
            return DirtyResumeDecision.SkipClear;
        }

        if (IsAbandonedZero(dirty, job))
        {
            return DirtyResumeDecision.SkipClear;
        }

        return DirtyResumeDecision.Offer;
    }

    public static void Clear(JobJournal journal)
    {
        journal.ClearHeartbeat();
        ClearSidecar(journal.Directory, journal.Directory.Length > 0
            ? Path.GetFileName(journal.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : "");
    }

    /// <summary>
    /// Drop the launch-resume flag. Sidecar is deleted, or overwritten Dirty=false if
    /// delete is denied, so the next launch cannot keep offering the same leftover.
    /// </summary>
    public static void ClearSidecar(string jobDirectory, string? jobId = null)
    {
        var path = SidecarPath(jobDirectory);
        TryDelete(path);
        TryDelete(path + ".tmp");
        if (!File.Exists(path))
        {
            return;
        }

        WriteSidecar(jobDirectory, new JobHeartbeatState
        {
            JobId = jobId ?? Path.GetFileName(jobDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Dirty = false,
            Utc = DateTimeOffset.UtcNow
        });
    }

    public static JobHeartbeatState? Read(JobJournal journal, string jobId)
    {
        var fromFile = ReadSidecar(journal.Directory);
        if (fromFile is { Dirty: true })
        {
            return fromFile with { JobId = string.IsNullOrEmpty(fromFile.JobId) ? jobId : fromFile.JobId };
        }

        return journal.ReadHeartbeat(jobId);
    }

    public static JobHeartbeatState? FindDirty(AppPaths paths)
    {
        if (!Directory.Exists(paths.Jobs))
        {
            return null;
        }

        JobHeartbeatState? best = null;
        foreach (var dir in Directory.EnumerateDirectories(paths.Jobs))
        {
            var jobId = Path.GetFileName(dir);
            JobHeartbeatState? state = null;
            try
            {
                state = ReadSidecar(dir);
                if (state is not { Dirty: true } && JobJournal.Exists(dir))
                {
                    using var journal = JobJournal.Open(dir);
                    state = journal.ReadHeartbeat(jobId);
                }
            }
            catch
            {
                continue;
            }

            if (state is not { Dirty: true })
            {
                continue;
            }

            if (string.IsNullOrEmpty(state.JobId))
            {
                state = state with { JobId = jobId };
            }

            if (best is null || state.Utc > best.Utc)
            {
                best = state;
            }
        }

        return best;
    }

    public static string RouteLabel(JobHeartbeatState state, Job? job)
    {
        var source = job is not null
            ? JobSources.Display(job)
            : state.SourcePath;
        var dest = job is not null
            ? job.DestinationPath
            : state.DestinationPath;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = "unknown source";
        }

        if (string.IsNullOrWhiteSpace(dest))
        {
            dest = "unknown destination";
        }

        return source + "  →  " + dest;
    }

    public static string UnscheduledStopMessage(JobHeartbeatState state, Job? job = null)
    {
        var local = state.Utc.ToLocalTime();
        var pct = state.Percent.ToString("0", CultureInfo.CurrentCulture);
        var kind = job?.Kind == JobKind.Compare ? "compare" : "transfer";
        return $"Unscheduled stop of {RouteLabel(state, job)} at {pct}% at {local:d MMM yyyy HH:mm}. Resume {kind} from there?";
    }

    public static string WaitForThisFileLabel(TimeSpan? fileEta)
    {
        if (fileEta is null || fileEta.Value <= TimeSpan.Zero || fileEta.Value.TotalSeconds < 10)
        {
            return "Wait for this file";
        }

        return $"Wait for this file ({ByteFormatter.CompactEta(fileEta.Value)})";
    }

    public static string CloseWhileRunningMessage(TimeSpan? fileEta)
    {
        if (fileEta is null || fileEta.Value.TotalSeconds >= 10)
        {
            var wait = fileEta is null || fileEta.Value <= TimeSpan.Zero
                ? "until the current file finishes"
                : ByteFormatter.AboutDuration(fileEta.Value);
            return $"You have transfers going. Wait {wait} for the current file to finish, or close right now?";
        }

        return "You have transfers going. The current file should finish in a few seconds. Wait for it, or close right now?";
    }

    private static void WriteSidecar(string jobDirectory, JobHeartbeatState state)
    {
        Directory.CreateDirectory(jobDirectory);
        var path = SidecarPath(jobDirectory);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(state, Json);
        using (var stream = new FileStream(
                   tmp,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   256,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Copy(tmp, path, overwrite: true);
                File.Delete(tmp);
            }
            catch
            {
                // journal meta still has the flag
            }
        }
    }

    private static JobHeartbeatState? ReadSidecar(string jobDirectory)
    {
        var path = SidecarPath(jobDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JobHeartbeatState>(File.ReadAllText(path), Json);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort; caller may overwrite Dirty=false
        }
    }
}
