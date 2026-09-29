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
        CancellationToken cancellationToken) =>
        Scan([mapping], journal, options, extraExcludeRoots, onProgress, cancellationToken, uniquePrefix: false);

    public static Result Scan(
        IReadOnlyList<CopyMapping> mappings,
        JobJournal journal,
        JobOptions options,
        IEnumerable<string>? extraExcludeRoots,
        Action<int, string>? onProgress,
        CancellationToken cancellationToken,
        bool uniquePrefix,
        List<DestNameAdjuster.Note>? nameNotes = null)
    {
        var existing = journal.GetFiles().ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var bySource = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var priorFile in existing.Values)
        {
            if (!string.IsNullOrWhiteSpace(priorFile.SourcePath))
            {
                bySource.TryAdd(priorFile.SourcePath, priorFile);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var changed = 0;
        var unchanged = 0;
        var found = 0;
        var tolerance = FileMetadata.ComparisonTolerance(options);
        var fresh = new List<FileRecord>();

        foreach (var record in SourceWalker.WalkAll(mappings, extraExcludeRoots, options, uniquePrefix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            found++;
            if (found == 1 || found % 500 == 0)
            {
                onProgress?.Invoke(found, record.RelativePath);
            }

            if (!existing.TryGetValue(record.RelativePath, out var prior)
                && !string.IsNullOrWhiteSpace(record.SourcePath))
            {
                bySource.TryGetValue(record.SourcePath, out prior);
            }

            if (prior is null)
            {
                fresh.Add(record);
                continue;
            }

            seen.Add(prior.RelativePath);
            seen.Add(record.RelativePath);
            if (SamePayload(prior, record, tolerance))
            {
                unchanged++;
                continue;
            }

            var renamed = !string.Equals(prior.RelativePath, record.RelativePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(prior.DestPath, record.DestPath, StringComparison.OrdinalIgnoreCase);
            if (renamed)
            {
                prior.Size = record.Size;
                prior.LastWriteUtc = record.LastWriteUtc;
                prior.Status = FileCopyStatus.Pending;
                prior.Hash = null;
                prior.Error = null;
                prior.RetryCount = 0;
                prior.BytesCopied = 0;
                prior.PayloadKind = record.PayloadKind;
                journal.UpsertFile(prior);
            }
            else
            {
                record.Status = FileCopyStatus.Pending;
                record.Hash = null;
                record.Error = null;
                record.RetryCount = 0;
                journal.UpsertFile(record);
            }

            changed++;
        }

        if (fresh.Count > 0)
        {
            var reservedDest = existing.Values.Select(f => f.DestPath);
            var reservedRel = existing.Values.Select(f => f.RelativePath);
            var notes = DestNameAdjuster.Resolve(fresh, options.FixLongOrDuplicateNames, reservedDest, reservedRel);
            foreach (var record in fresh)
            {
                journal.UpsertFile(record);
                seen.Add(record.RelativePath);
            }

            nameNotes?.AddRange(notes);
            added += fresh.Count;
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
