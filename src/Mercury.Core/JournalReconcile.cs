namespace Mercury;

/// <summary>
/// Optional resume pass: walk source vs journal (new / changed / gone) without a full re-copy.
/// Cancellable so a 3 TB check can be skipped with No, or aborted mid-scan.
/// </summary>
public static class JournalReconcile
{
    public readonly record struct Result(int Added, int Changed, int Removed, int Unchanged);

    public static Result Scan(
        CopyMapping mapping,
        JobJournal journal,
        JobOptions options,
        IEnumerable<string>? extraExcludeRoots,
        Action<int, string>? onProgress,
        CancellationToken cancellationToken)
    {
        var existing = journal.GetFiles().ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var changed = 0;
        var unchanged = 0;
        var found = 0;
        var tolerance = FileMetadata.ComparisonTolerance(options);

        foreach (var record in SourceWalker.Walk(mapping, extraExcludeRoots, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen.Add(record.RelativePath);
            found++;
            if (found == 1 || found % 500 == 0)
            {
                onProgress?.Invoke(found, record.RelativePath);
            }

            if (!existing.TryGetValue(record.RelativePath, out var prior))
            {
                journal.UpsertFile(record);
                added++;
                continue;
            }

            if (SamePayload(prior, record, tolerance))
            {
                unchanged++;
                continue;
            }

            record.Status = FileCopyStatus.Pending;
            record.Hash = null;
            record.Error = null;
            record.RetryCount = 0;
            journal.UpsertFile(record);
            changed++;
        }

        var removed = 0;
        foreach (var prior in existing.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.Contains(prior.RelativePath))
            {
                continue;
            }

            removed++;
            if (prior.Status is FileCopyStatus.Pending or FileCopyStatus.Failed or FileCopyStatus.Deferred)
            {
                journal.MarkSkipped(prior.RelativePath, "Source no longer exists");
            }
        }

        return new Result(added, changed, removed, unchanged);
    }

    private static bool SamePayload(FileRecord prior, FileRecord current, TimeSpan tolerance)
    {
        if (prior.Size != current.Size)
        {
            return false;
        }

        var delta = prior.LastWriteUtc - current.LastWriteUtc;
        if (delta < TimeSpan.Zero)
        {
            delta = -delta;
        }

        return delta <= tolerance;
    }
}
