using System.Globalization;
using System.Text.Json;

namespace Mercury;

public sealed record JobHeartbeatState
{
    public string JobId { get; init; } = "";
    public bool Dirty { get; init; }
    public double Percent { get; init; }
    public string? File { get; init; }
    public DateTimeOffset Utc { get; init; }
}

/// <summary>
/// Running-job dirty flag so a kill, crash, or reboot can offer resume on next launch.
/// Written to journal meta and a sidecar file flushed with write-through.
/// </summary>
public static class JobHeartbeat
{
    public const string SidecarName = "heartbeat.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string SidecarPath(string jobDirectory) =>
        Path.Combine(jobDirectory, SidecarName);

    public static JobHeartbeatState Create(string jobId, double percent, string? file) =>
        new()
        {
            JobId = jobId,
            Dirty = true,
            Percent = Math.Clamp(percent, 0, 100),
            File = file,
            Utc = DateTimeOffset.UtcNow
        };

    public static void Write(JobJournal journal, string jobId, double percent, string? file)
    {
        var state = Create(jobId, percent, file);
        journal.SetHeartbeat(state);
        WriteSidecar(journal.Directory, state);
    }

    public static void Clear(JobJournal journal)
    {
        journal.ClearHeartbeat();
        TryDelete(SidecarPath(journal.Directory));
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

    public static string UnscheduledStopMessage(JobHeartbeatState state)
    {
        var local = state.Utc.ToLocalTime();
        var pct = state.Percent.ToString("0", CultureInfo.CurrentCulture);
        return $"Unscheduled stop at {pct}% at {local:d MMM yyyy HH:mm}. Resume transfer from there?";
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
            // best effort
        }
    }
}
