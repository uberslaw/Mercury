namespace Mercury;

/// <summary>
/// Multi-folder sources on a job. <see cref="Job.SourcePath"/> stays the first/display path for older journals.
/// </summary>
public static class JobSources
{
    public static IReadOnlyList<string> Roots(Job job)
    {
        if (job.SourcePaths is { Count: > 0 })
        {
            return job.SourcePaths;
        }

        return string.IsNullOrWhiteSpace(job.SourcePath) ? [] : [job.SourcePath];
    }

    public static void Set(Job job, IEnumerable<string>? paths)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths is not null)
        {
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
        }

        job.SourcePaths = list;
        job.SourcePath = list.Count > 0 ? list[0] : "";
    }

    public static string Display(Job job)
    {
        var roots = Roots(job);
        if (roots.Count <= 1)
        {
            return job.SourcePath;
        }

        return $"{job.SourcePath} + {roots.Count - 1} more";
    }

    public static string DefaultName(Job job)
    {
        var roots = Roots(job);
        if (roots.Count == 0)
        {
            return "";
        }

        var first = Path.GetFileName(roots[0].TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(first))
        {
            first = roots[0];
        }

        return roots.Count == 1 ? first : $"{first} + {roots.Count - 1} more";
    }

    public static bool Same(Job job, IReadOnlyList<string> sources)
    {
        var left = Roots(job);
        if (left.Count != sources.Count)
        {
            return false;
        }

        var right = new HashSet<string>(sources.Select(s =>
        {
            try
            {
                return PathNormalizer.Normalize(s);
            }
            catch
            {
                return s;
            }
        }), StringComparer.OrdinalIgnoreCase);

        foreach (var root in left)
        {
            if (!right.Contains(root))
            {
                return false;
            }
        }

        return true;
    }

    public static void RemapVolumes(Job job)
    {
        var roots = Roots(job).ToList();
        var changed = false;
        for (var i = 0; i < roots.Count; i++)
        {
            var remapped = VolumeInfo.RemapIfMissing(roots[i], job.VolumeSerial);
            if (string.IsNullOrEmpty(remapped) ||
                string.Equals(remapped, roots[i], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            roots[i] = remapped;
            changed = true;
        }

        if (changed)
        {
            Set(job, roots);
        }
        else if (roots.Count > 0 && string.IsNullOrWhiteSpace(job.SourcePath))
        {
            job.SourcePath = roots[0];
        }
    }

    public static IReadOnlyList<CopyMapping> Resolve(
        Job job,
        string destPath,
        bool includeSourceFolderName)
    {
        return CopyShape.ResolveAll(Roots(job), destPath, includeSourceFolderName);
    }
}
