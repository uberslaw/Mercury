using System.Diagnostics;

namespace Mercury;

public static class Verifier
{
    public static int Verify(
        Job job,
        JobJournal journal,
        CopyMapping mapping,
        IJobLog log,
        string name,
        CancellationToken cancellationToken = default,
        Action<RundownProgress>? progress = null,
        IReadOnlySet<string>? onlyThese = null)
    {
        var issuesBefore = journal.IssueCount();
        var files = journal.GetFiles();
        if (onlyThese is not null)
        {
            files = files.Where(f => onlyThese.Contains(f.RelativePath)).ToList();
        }

        var total = Math.Max(1, files.Count);
        var done = 0;
        var clock = Stopwatch.StartNew();

        void Pulse(string? file)
        {
            var rate = clock.Elapsed.TotalSeconds >= 0.25 ? done / clock.Elapsed.TotalSeconds : 0;
            TimeSpan? eta = null;
            if (total <= done)
            {
                eta = TimeSpan.Zero;
            }
            else if (rate >= 0.5)
            {
                eta = TimeSpan.FromSeconds((total - done) / rate);
            }

            progress?.Invoke(new RundownProgress(done, total, rate, eta, file ?? "destination"));
        }

        Pulse(null);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Status is FileCopyStatus.Failed or FileCopyStatus.Deferred)
            {
                Add(journal, log, job, name, file.RelativePath, IssueKind.CopyError,
                    file.Error ?? "Copy failed after retries.");
                done++;
                Pulse(file.RelativePath);
                continue;
            }

            if (file.Status == FileCopyStatus.Skipped)
            {
                done++;
                Pulse(file.RelativePath);
                continue;
            }

            if (onlyThese is null && AdaptiveVerifyCache.Contains(job.Id, file.RelativePath))
            {
                done++;
                Pulse(file.RelativePath);
                continue;
            }

            if (!File.Exists(file.DestPath))
            {
                Add(journal, log, job, name, file.RelativePath, IssueKind.Missing,
                    "Destination file is missing.");
                done++;
                Pulse(file.RelativePath);
                continue;
            }

            FileInfo dest;
            try
            {
                dest = new FileInfo(file.DestPath);
            }
            catch (Exception ex)
            {
                Add(journal, log, job, name, file.RelativePath, IssueKind.CopyError, ex.Message);
                done++;
                Pulse(file.RelativePath);
                continue;
            }

            if (dest.Length != file.Size)
            {
                Add(journal, log, job, name, file.RelativePath, IssueKind.SizeMismatch,
                    $"Size mismatch: source {file.Size} bytes, dest {dest.Length} bytes.");
                done++;
                Pulse(file.RelativePath);
                continue;
            }

            if (job.Options.Verify == VerifyLevel.Thorough)
            {
                try
                {
                    var destHash = HashUtil.HashFile(file.DestPath);
                    var expected = file.Hash;
                    if (string.IsNullOrEmpty(expected) && File.Exists(file.SourcePath))
                    {
                        expected = HashUtil.HashFile(file.SourcePath);
                    }

                    if (!string.IsNullOrEmpty(expected) &&
                        !string.Equals(expected, destHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Add(journal, log, job, name, file.RelativePath, IssueKind.HashMismatch,
                            $"Hash mismatch: source {expected}, dest {destHash}.");
                    }
                }
                catch (Exception ex)
                {
                    Add(journal, log, job, name, file.RelativePath, IssueKind.HashMismatch, ex.Message);
                }

                done++;
                Pulse(file.RelativePath);
                continue;
            }

            if (job.Options.CopyTimestamps)
            {
                var delta = dest.LastWriteTimeUtc - file.LastWriteUtc;
                if (delta < TimeSpan.Zero)
                {
                    delta = -delta;
                }

                if (delta > FileMetadata.ComparisonTolerance(job.Options))
                {
                    Add(journal, log, job, name, file.RelativePath, IssueKind.TimestampMismatch,
                        $"Timestamp mismatch: source {file.LastWriteUtc:O}, dest {dest.LastWriteTimeUtc:O}.");
                }
            }

            done++;
            Pulse(file.RelativePath);
        }

        if (onlyThese is null)
        {
        try
        {
            var journalFiles = journal.GetFiles();
            var known = journalFiles.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var knownSources = journalFiles
                .Select(f => f.SourcePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var record in SourceWalker.Walk(mapping, extraExcludeRoots: null, job.Options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (known.Contains(record.RelativePath) || knownSources.Contains(record.SourcePath))
                {
                    continue;
                }

                if (!File.Exists(record.DestPath))
                {
                    Add(journal, log, job, name, record.RelativePath, IssueKind.Missing,
                        "Source file was not in the job journal and is missing at destination.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Error(job.Id, name, $"Inventory walk failed: {ex.Message}");
        }
        }

        if (onlyThese is null)
        {
            AdaptiveVerifyCache.Clear(job.Id);
        }

        Pulse(null);
        return journal.IssueCount() - issuesBefore + CountFailed(files);
    }

    private static int CountFailed(IReadOnlyList<FileRecord> files)
    {
        // Failed files already added as issues in the loop; don't double-count here.
        return 0;
    }

    private static void Add(
        JobJournal journal,
        IJobLog log,
        Job job,
        string name,
        string relative,
        IssueKind kind,
        string message)
    {
        journal.AddIssue(new TransferIssue
        {
            RelativePath = relative,
            Kind = kind,
            Message = message
        });
        log.Error(job.Id, name, $"{kind}: {relative} — {message}");
    }
}
