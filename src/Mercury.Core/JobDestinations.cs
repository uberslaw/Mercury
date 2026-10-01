namespace Mercury;

/// <summary>
/// Multi-folder destinations on a job. <see cref="Job.DestinationPath"/> stays the first/display path for older journals.
/// One logical job copies source → dest1, then dest2, … with one journal and per-dest verify.
/// </summary>
public static class JobDestinations
{
    public static IReadOnlyList<string> Roots(Job job)
    {
        if (job.DestinationPaths is { Count: > 0 })
        {
            return job.DestinationPaths;
        }

        return string.IsNullOrWhiteSpace(job.DestinationPath) ? [] : [job.DestinationPath];
    }

    public static void Set(Job job, IEnumerable<string>? paths)
    {
        var list = NormalizeList(paths);
        job.DestinationPaths = list;
        job.DestinationPath = list.Count > 0 ? list[0] : "";
    }

    public static string Display(Job job)
    {
        var roots = Roots(job);
        if (roots.Count <= 1)
        {
            return job.DestinationPath;
        }

        return $"{job.DestinationPath} + {roots.Count - 1} more";
    }

    public static bool Same(Job job, IReadOnlyList<string> destinations)
    {
        var left = Roots(job);
        if (left.Count != destinations.Count)
        {
            return false;
        }

        var right = new HashSet<string>(destinations.Select(NormalizeLoose), StringComparer.OrdinalIgnoreCase);
        foreach (var root in left)
        {
            if (!right.Contains(root) && !right.Contains(NormalizeLoose(root)))
            {
                return false;
            }
        }

        return true;
    }

    public static void EnsureList(Job job)
    {
        if (job.DestinationPaths is { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(job.DestinationPath))
            {
                job.DestinationPath = job.DestinationPaths[0];
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(job.DestinationPath))
        {
            job.DestinationPaths = [job.DestinationPath];
        }
    }

    /// <summary>
    /// Resolve source landings for every destination. <paramref name="destPath"/> overrides the list when it is
    /// not one of the stored dests (Catcher uses the journal folder as the write root).
    /// </summary>
    public static IReadOnlyList<CopyMapping> Resolve(
        Job job,
        string destPath,
        bool includeSourceFolderName)
    {
        var dests = Roots(job);
        if (dests.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(destPath))
            {
                return [];
            }

            dests = [destPath];
        }
        else if (!string.IsNullOrWhiteSpace(destPath) && !Contains(dests, destPath))
        {
            dests = [destPath];
        }

        return CopyShape.ResolveFanOut(JobSources.Roots(job), dests, includeSourceFolderName);
    }

    public static int IndexOf(string? destPath, IReadOnlyList<string> dests)
    {
        if (string.IsNullOrWhiteSpace(destPath) || dests.Count == 0)
        {
            return 1;
        }

        for (var i = 0; i < dests.Count; i++)
        {
            if (BelongsTo(destPath, dests[i]))
            {
                return i + 1;
            }
        }

        return 1;
    }

    public static bool BelongsTo(string? path, string destRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(destRoot))
        {
            return false;
        }

        try
        {
            var dest = PathNormalizer.DirectoryPath(destRoot);
            var full = Path.GetFullPath(path);
            if (string.Equals(full.TrimEnd('\\', '/'), dest.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = dest.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string UserDest(CopyMapping mapping) =>
        string.IsNullOrWhiteSpace(mapping.UserDestPath) ? mapping.DestRoot : mapping.UserDestPath;

    public static string VerifyMessage(
        Job job,
        JobJournal journal,
        int issues,
        string singleComplete,
        string singleIncomplete)
    {
        var dests = Roots(job);
        if (dests.Count <= 1)
        {
            return issues == 0 ? singleComplete : singleIncomplete;
        }

        if (issues == 0)
        {
            return $"Verified complete on {dests.Count} destinations.";
        }

        var files = journal.GetFiles();
        var issueRows = journal.GetIssues();
        var anyCopied = files.Count > 0;
        var complete = new List<string>();
        var incomplete = new List<string>();
        foreach (var dest in dests)
        {
            var destFiles = files.Where(f => BelongsTo(f.DestPath, dest)).ToList();
            var destFailed = destFiles.Any(f => f.Status is FileCopyStatus.Failed or FileCopyStatus.Deferred)
                             || issueRows.Any(i => IssueTouches(i, dest, destFiles))
                             || (destFiles.Count == 0 && anyCopied);
            if (destFailed)
            {
                incomplete.Add(ShortName(dest));
            }
            else
            {
                complete.Add(ShortName(dest));
            }
        }

        var parts = new List<string>();
        if (complete.Count > 0)
        {
            parts.Add("Verified: " + string.Join(", ", complete));
        }

        if (incomplete.Count > 0)
        {
            parts.Add("Incomplete: " + string.Join(", ", incomplete));
        }

        parts.Add($"{issues} issue(s). Open the log for details.");
        return string.Join(". ", parts);
    }

    private static bool IssueTouches(TransferIssue issue, string dest, IReadOnlyList<FileRecord> destFiles)
    {
        if (!string.IsNullOrWhiteSpace(issue.RelativePath)
            && (string.Equals(issue.RelativePath, dest, StringComparison.OrdinalIgnoreCase)
                || BelongsTo(issue.RelativePath, dest)))
        {
            return true;
        }

        return destFiles.Any(f =>
            string.Equals(f.RelativePath, issue.RelativePath, StringComparison.OrdinalIgnoreCase));
    }

    private static string ShortName(string dest)
    {
        try
        {
            var path = PathNormalizer.DirectoryPath(dest);
            if (PathNormalizer.IsDriveRoot(path))
            {
                return path;
            }

            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrWhiteSpace(name) ? dest : name;
        }
        catch
        {
            return dest;
        }
    }

    private static bool Contains(IReadOnlyList<string> dests, string candidate)
    {
        foreach (var dest in dests)
        {
            if (SamePath(dest, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                PathNormalizer.DirectoryPath(left),
                PathNormalizer.DirectoryPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(
                left.TrimEnd('\\', '/'),
                right.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static List<string> NormalizeList(IEnumerable<string>? paths)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths is null)
        {
            return list;
        }

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = PathNormalizer.Normalize(raw).TrimEnd('\\', '/');
                if (normalized.Length == 2 && normalized[1] == ':')
                {
                    normalized += "\\";
                }
            }
            catch
            {
                continue;
            }

            if (!seen.Add(normalized))
            {
                continue;
            }

            list.Add(normalized);
        }

        return list;
    }

    private static string NormalizeLoose(string path)
    {
        try
        {
            var normalized = PathNormalizer.Normalize(path).TrimEnd('\\', '/');
            if (normalized.Length == 2 && normalized[1] == ':')
            {
                normalized += "\\";
            }

            return normalized;
        }
        catch
        {
            return path;
        }
    }
}
