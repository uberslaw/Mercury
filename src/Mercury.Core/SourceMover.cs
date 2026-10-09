namespace Mercury;

/// <summary>
/// After copy+verify, delete source files for Move jobs. Source is removed only when
/// every destination in the job has a verified copy of that file.
/// </summary>
public static class SourceMover
{
    public static int DeleteVerifiedSources(
        Job job,
        JobJournal journal,
        IJobLog log,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (job.Options?.Move != true || job.Options.DryRun)
        {
            return 0;
        }

        if (job.Status is JobStatus.Cancelled or JobStatus.Failed)
        {
            return 0;
        }

        var destCount = job.Catcher is not null ? 1 : Math.Max(1, JobDestinations.Roots(job).Count);
        var files = journal.GetFiles();
        var issuePaths = journal.GetIssues()
            .Select(i => i.RelativePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;

        foreach (var group in files
            .Where(f => !string.IsNullOrWhiteSpace(f.SourcePath))
            .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = group.Key;
            var rows = group.ToList();
            if (!ShouldDeleteSource(job, source, rows, destCount, issuePaths))
            {
                continue;
            }

            if (!TryDeleteFile(source, out var error))
            {
                var relative = rows[0].RelativePath;
                journal.AddIssue(new TransferIssue
                {
                    RelativePath = relative,
                    Kind = IssueKind.CopyError,
                    Message = "Could not delete source after verify: " + source + ". " + error
                });
                log.Error(job.Id, name, $"Move delete failed {relative}: {error}");
                continue;
            }

            deleted++;
            log.Info(job.Id, name, $"Moved {rows[0].RelativePath} — source deleted.");
        }

        return deleted;
    }

    internal static bool ShouldDeleteSource(
        Job job,
        string source,
        IReadOnlyList<FileRecord> rows,
        int destCount,
        IReadOnlySet<string> issuePaths)
    {
        if (string.IsNullOrWhiteSpace(source) || rows.Count < destCount)
        {
            return false;
        }

        if (!IsJobSourceFile(job, source) || IsUnderDestination(job, source))
        {
            return false;
        }

        foreach (var file in rows)
        {
            if (file.Status is FileCopyStatus.Failed or FileCopyStatus.Deferred or FileCopyStatus.Pending)
            {
                return false;
            }

            if (issuePaths.Contains(file.RelativePath))
            {
                return false;
            }

            if (string.Equals(source, file.DestPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (job.Catcher is not null)
            {
                if (file.Status is not (FileCopyStatus.Copied or FileCopyStatus.Unpacked))
                {
                    return false;
                }

                continue;
            }

            if (!File.Exists(file.DestPath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsJobSourceFile(Job job, string path)
    {
        foreach (var root in JobSources.Roots(job))
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || JobDestinations.BelongsTo(path, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderDestination(Job job, string path)
    {
        foreach (var dest in JobDestinations.Roots(job))
        {
            if (JobDestinations.BelongsTo(path, dest))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryDeleteFile(string path, out string error)
    {
        error = "";
        try
        {
            if (Directory.Exists(path) && !File.Exists(path))
            {
                error = "Source path is a directory; folders are left in place.";
                return false;
            }

            if (!File.Exists(path))
            {
                return true;
            }

            var info = new FileInfo(path);
            if (info.IsReadOnly)
            {
                info.IsReadOnly = false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
