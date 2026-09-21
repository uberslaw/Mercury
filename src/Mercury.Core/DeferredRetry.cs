namespace Mercury;

public static class DeferredRetry
{
    public const int FewFileLimit = 8;
    public const string QuartersMetaKey = "deferred_quarters";

    public static bool IsTransient(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
            {
                return true;
            }

            if (current is not IOException io)
            {
                continue;
            }

            var code = io.HResult & 0xFFFF;
            if (code is 5 or 19 or 32 or 33 or 1224)
            {
                return true;
            }

            var message = io.Message;
            if (message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("sharing violation", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("The process cannot access the file", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("lock", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>0 below 25%, then 1=25, 2=50, 3=75, 4=100. Totals unknown → 0.</summary>
    public static int Quarter(long copied, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        var percent = 100.0 * copied / total;
        if (percent >= 100)
        {
            return 4;
        }

        if (percent >= 75)
        {
            return 3;
        }

        if (percent >= 50)
        {
            return 2;
        }

        if (percent >= 25)
        {
            return 1;
        }

        return 0;
    }

    public static string QuarterLabel(int quarter) =>
        quarter switch
        {
            1 => "25% progress",
            2 => "50% progress",
            3 => "75% progress",
            4 => "100% progress",
            _ => "progress checkpoint"
        };

    public static bool PreferFileBoundary(int fileCount, long totalBytes, long largestRemainingBytes)
    {
        if (fileCount <= FewFileLimit)
        {
            return true;
        }

        return totalBytes > 0 && largestRemainingBytes >= totalBytes / 4.0;
    }
}

public sealed class DeferredRetrySession
{
    private readonly Job _job;
    private readonly JobJournal _journal;
    private readonly IJobLog _log;
    private readonly string _name;
    private readonly Dictionary<string, FileRecord> _items = new(StringComparer.OrdinalIgnoreCase);
    private int _firedQuarters;

    public DeferredRetrySession(Job job, JobJournal journal, IJobLog log, string name)
    {
        _job = job;
        _journal = journal;
        _log = log;
        _name = name;
        Load();
    }

    public IReadOnlyCollection<FileRecord> Items => _items.Values;

    public bool HasItems => _items.Count > 0;

    public int FiredQuarters => _firedQuarters;

    public void Load()
    {
        _items.Clear();
        foreach (var file in _journal.GetFiles(FileCopyStatus.Deferred))
        {
            _items[file.RelativePath] = file;
        }

        _firedQuarters = _journal.GetMetaInt(DeferredRetry.QuartersMetaKey);
    }

    public void Defer(FileRecord file, string error)
    {
        file.Status = FileCopyStatus.Deferred;
        file.Error = error;
        file.RetryCount = _job.Options.RetryCount;
        _items[file.RelativePath] = file;
        TryJournal(() => _journal.MarkDeferred(file.RelativePath, error, file.RetryCount),
            $"Could not journal deferred {file.RelativePath}");
        _log.Info(_job.Id, _name,
            $"Deferred {file.RelativePath} ({error}) — will retry at 25/50/75/100% progress, or at the end of this pass. Close or unlock the file if you can.");
    }

    public void Keep(FileRecord file, string error)
    {
        file.Status = FileCopyStatus.Deferred;
        file.Error = error;
        file.RetryCount = _job.Options.RetryCount;
        _items[file.RelativePath] = file;
        TryJournal(() => _journal.MarkDeferred(file.RelativePath, error, file.RetryCount),
            $"Could not journal deferred {file.RelativePath}");
    }

    public void Remove(string relativePath) => _items.Remove(relativePath);

    public bool ShouldRetry(long copied, long total, int fileCount, bool afterFile, bool endOfPass, long largestRemainingBytes)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        if (endOfPass)
        {
            return true;
        }

        if (DeferredRetry.PreferFileBoundary(fileCount, total, largestRemainingBytes) && afterFile)
        {
            return true;
        }

        if (total <= 0)
        {
            return false;
        }

        return DeferredRetry.Quarter(copied, total) > _firedQuarters;
    }

    public string Reason(long copied, long total, bool afterFile, bool endOfPass)
    {
        if (endOfPass)
        {
            return "end of pass";
        }

        if (afterFile && DeferredRetry.Quarter(copied, total) <= _firedQuarters)
        {
            return "after file";
        }

        return DeferredRetry.QuarterLabel(DeferredRetry.Quarter(copied, total));
    }

    public void NoteRetry(long copied, long total, bool endOfPass)
    {
        _firedQuarters = endOfPass ? 4 : Math.Max(_firedQuarters, DeferredRetry.Quarter(copied, total));
        TryJournal(() => _journal.SetMetaInt(DeferredRetry.QuartersMetaKey, _firedQuarters),
            "Could not save deferred-retry checkpoint");
    }

    public void FinalizeFailures(IssueKind kind = IssueKind.CopyError)
    {
        foreach (var file in _items.Values.ToList())
        {
            var error = file.Error ?? "Copy failed after deferred retries";
            TryJournal(() =>
            {
                _journal.MarkFailed(file.RelativePath, error, file.RetryCount);
                _journal.AddIssue(new TransferIssue
                {
                    RelativePath = file.RelativePath,
                    Kind = kind,
                    Message = error
                });
            }, $"Could not journal failure for {file.RelativePath}");
            _log.Error(_job.Id, _name, $"Deferred retry failed {file.RelativePath}: {error}");
        }

        _items.Clear();
        _firedQuarters = 4;
        TryJournal(() => _journal.SetMetaInt(DeferredRetry.QuartersMetaKey, _firedQuarters),
            "Could not save deferred-retry checkpoint");
    }

    private void TryJournal(Action action, string failed)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (JobJournal.IsJournalFault(ex))
        {
            _log.Error(_job.Id, _name, failed + ": " + ProgressHeader.DescribeFileError(ex));
        }
    }
}

public static class ProgressHeader
{
    /// <summary>Overall file counts in the stats table when two or more jobs are queued.</summary>
    public static bool ShowOverall(int queuedJobCount) => queuedJobCount >= 2;

    public static bool ShowOverallTab(int queuedJobCount) => ShowOverall(queuedJobCount);

    /// <summary>
    /// Bar label. Integer percents at 1% and above. Below 1% uses one decimal (0.1%)
    /// so a started job never shows 0% (911 MB / 826 GB would otherwise round down).
    /// </summary>
    public static readonly string[] LayoutKeys =
        ["Stage", "File", "Elapsed", "This stage", "Files", "Bytes", "Speed", "ETA"];

    public static bool IsExceptionDump(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("Object reference not set", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NullReferenceException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ObjectDisposedException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("at Mercury.", StringComparison.Ordinal)
            || text.Contains("at Microsoft.Data.Sqlite", StringComparison.Ordinal);
    }

    public static string HeaderStatus(string? message, JobStatus status)
    {
        if (IsExceptionDump(message))
        {
            return status switch
            {
                JobStatus.Failed => "Failed — see Console",
                JobStatus.Cancelled => "Stopped",
                JobStatus.Incomplete => "Incomplete — see Console",
                JobStatus.Paused or JobStatus.PausedOutsideHours => "Paused",
                _ => "See Console for details"
            };
        }

        return string.IsNullOrWhiteSpace(message) ? status.ToString() : message;
    }

    public static string HeaderResult(Job job) => HeaderStatus(job.ResultMessage, job.Status);

    public static string DescribeFileError(Exception ex)
    {
        if (ex is ObjectDisposedException or NullReferenceException)
        {
            return "Journal error while recording this file — deferred for retry";
        }

        return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
    }

    public static string DashOr(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    public static string CurrentFileDisplay(string? path, long copied, long total)
    {
        var name = DashOr(path);
        if (name == "—" || total <= 0)
        {
            return name;
        }

        var percent = 100.0 * Math.Clamp(copied, 0, total) / total;
        return name + "  " + PercentLabel(percent, copied > 0);
    }

    public static string PercentLabel(double percent, bool workStarted = false)
    {
        var p = Math.Clamp(percent, 0, 100);
        if (p <= 0)
        {
            return workStarted ? "<1%" : "0%";
        }

        if (p < 1)
        {
            var tenths = Math.Round(p, 1, MidpointRounding.AwayFromZero);
            return tenths < 0.1
                ? "<1%"
                : tenths.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        }

        return Math.Clamp((int)Math.Round(p, MidpointRounding.AwayFromZero), 0, 100)
            .ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
    }
}
