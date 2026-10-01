namespace Mercury;

public static class CopyShape
{
    public static CopyMapping Resolve(string sourcePath, string destPath, bool includeSourceFolderName = true)
    {
        var source = PathNormalizer.Normalize(sourcePath);
        var dest = PathNormalizer.DirectoryPath(destPath);

        if (File.Exists(source))
        {
            var fileName = Path.GetFileName(source);
            var destNormalized = PathNormalizer.Normalize(destPath);
            if (Directory.Exists(destNormalized) || destNormalized.EndsWith('\\') || destNormalized.EndsWith('/'))
            {
                return new CopyMapping
                {
                    Kind = SourceKind.File,
                    SourceRoot = Path.GetDirectoryName(source) ?? source,
                    DestRoot = dest,
                    SingleFile = true,
                    SingleFileName = fileName
                };
            }

            var destParent = Path.GetDirectoryName(destNormalized);
            if (string.IsNullOrEmpty(destParent))
            {
                throw new InvalidOperationException($"Destination is not a writable path: {destNormalized}");
            }

            return new CopyMapping
            {
                Kind = SourceKind.File,
                SourceRoot = Path.GetDirectoryName(source) ?? source,
                DestRoot = PathNormalizer.DirectoryPath(destParent),
                SingleFile = true,
                SingleFileName = Path.GetFileName(destNormalized)
            };
        }

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Source not found: {source}");
        }

        if (PathNormalizer.IsDriveRoot(source))
        {
            return new CopyMapping
            {
                Kind = SourceKind.DriveRoot,
                SourceRoot = source,
                DestRoot = dest,
                SingleFile = false
            };
        }

        var destRoot = dest;
        if (includeSourceFolderName)
        {
            var folderName = Path.GetFileName(source.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(folderName))
            {
                destRoot = Path.Combine(dest, folderName);
            }
        }

        return new CopyMapping
        {
            Kind = SourceKind.Folder,
            SourceRoot = source,
            DestRoot = destRoot,
            SingleFile = false
        };
    }

    public static SourceKind DetectKind(string sourcePath)
    {
        var source = PathNormalizer.Normalize(sourcePath);
        if (File.Exists(source))
        {
            return SourceKind.File;
        }

        if (Directory.Exists(source) && PathNormalizer.IsDriveRoot(source))
        {
            return SourceKind.DriveRoot;
        }

        return SourceKind.Folder;
    }

    /// <summary>
    /// Live dest preview (does not require the source to exist). Folder sources land in dest\FolderName when
    /// <paramref name="includeSourceFolderName"/> is on; drive roots dump contents into dest; files land as dest\file.
    /// </summary>
    public static string PreviewLandingPath(string sourcePath, string destPath, bool includeSourceFolderName = true)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destPath))
        {
            return "";
        }

        try
        {
            var dest = PathNormalizer.DirectoryPath(destPath);
            string source;
            try
            {
                source = PathNormalizer.Normalize(sourcePath);
            }
            catch (Exception)
            {
                return "";
            }

            if (File.Exists(source))
            {
                var destNormalized = PathNormalizer.Normalize(destPath);
                if (Directory.Exists(destNormalized) || destNormalized.EndsWith('\\') || destNormalized.EndsWith('/'))
                {
                    return Path.Combine(dest, Path.GetFileName(source));
                }

                return destNormalized;
            }

            if (PathNormalizer.IsDriveRoot(source))
            {
                return dest;
            }

            if (!includeSourceFolderName)
            {
                return dest;
            }

            var folderName = Path.GetFileName(source.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(folderName))
            {
                return dest;
            }

            return Path.Combine(dest, folderName);
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static string LandingPath(CopyMapping mapping) =>
        mapping.SingleFile
            ? Path.Combine(mapping.DestRoot, mapping.SingleFileName ?? "")
            : mapping.DestRoot;

    /// <summary>
    /// Path shown in Progress File: relative to the destination the user picked, so Include-on
    /// jobs read as Anchor Span\compressed\file.zip rather than compressed\file.zip.
    /// </summary>
    public static string ProgressRelative(Job job, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return current ?? "";
        }

        job.Options ??= new JobOptions();
        var dests = JobDestinations.Roots(job);
        if (dests.Count == 0 && !string.IsNullOrWhiteSpace(job.DestinationPath))
        {
            dests = [job.DestinationPath];
        }

        if (dests.Count > 1 && Path.IsPathRooted(current))
        {
            foreach (var destPath in dests)
            {
                var relative = ProgressRelativeToDest(job, destPath, current);
                if (!string.Equals(relative, current, StringComparison.OrdinalIgnoreCase)
                    && !Path.IsPathRooted(relative))
                {
                    return relative;
                }
            }

            return current;
        }

        if (dests.Count > 1)
        {
            return current;
        }

        return ProgressRelativeToDest(job, dests.Count == 1 ? dests[0] : job.DestinationPath, current);
    }

    private static string ProgressRelativeToDest(Job job, string destPath, string current)
    {
        string dest;
        try
        {
            dest = PathNormalizer.DirectoryPath(destPath);
        }
        catch
        {
            return current;
        }

        var landing = PreviewLandingPath(job.SourcePath, destPath, job.Options.IncludeSourceFolderName);
        if (string.IsNullOrEmpty(landing))
        {
            landing = dest;
        }

        try
        {
            if (Path.IsPathRooted(current))
            {
                var fromDest = Path.GetRelativePath(dest, current);
                if (IsRelativeInside(fromDest))
                {
                    return fromDest;
                }

                var fromLanding = Path.GetRelativePath(landing, current);
                if (IsRelativeInside(fromLanding))
                {
                    return Path.GetRelativePath(dest, PathNormalizer.Combine(landing, fromLanding));
                }

                current = Path.GetFileName(current);
            }

            var underLanding = PathNormalizer.Combine(landing, current);
            var rel = Path.GetRelativePath(dest, underLanding);
            if (IsRelativeInside(rel))
            {
                return rel;
            }
        }
        catch
        {
            // keep the raw name
        }

        return current;
    }

    public static string DestPathFor(CopyMapping mapping, string relativePath)
    {
        var relative = StripUniquePrefix(relativePath, mapping.UniqueRelativePrefix);
        if (mapping.SingleFile)
        {
            return Path.Combine(mapping.DestRoot, mapping.SingleFileName ?? Path.GetFileName(relative));
        }

        return PathNormalizer.Combine(mapping.DestRoot, relative);
    }

    public static CopyMapping FindMapping(FileRecord file, IReadOnlyList<CopyMapping> mappings)
    {
        if (mappings.Count == 0)
        {
            throw new ArgumentException("At least one mapping is required.", nameof(mappings));
        }

        // Source-path fallback is for one dest (or multi-source under one dest). Two+ dests share
        // the same source, so prefix / dest-root decide; source must not steal dest2 onto dest1.
        var prefixed = mappings.Any(m => !string.IsNullOrEmpty(m.UniqueRelativePrefix));
        CopyMapping? best = null;
        var bestScore = -1;
        var bestLen = -1;
        foreach (var mapping in mappings)
        {
            try
            {
                var dest = Path.GetFullPath(file.DestPath);
                var root = Path.GetFullPath(mapping.DestRoot).TrimEnd('\\');
                var underDest = dest.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(dest.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase);
                var underPrefix = !string.IsNullOrEmpty(mapping.UniqueRelativePrefix)
                    && (file.RelativePath.StartsWith(mapping.UniqueRelativePrefix + "\\", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(file.RelativePath, mapping.UniqueRelativePrefix, StringComparison.OrdinalIgnoreCase));
                var underSource = !prefixed
                    && (file.SourcePath.StartsWith(mapping.SourceRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(file.SourcePath, mapping.SourceRoot, StringComparison.OrdinalIgnoreCase));
                var score = underDest ? 3 : underPrefix ? 2 : underSource ? 1 : 0;
                if (score == 0)
                {
                    continue;
                }

                if (score > bestScore || (score == bestScore && mapping.DestRoot.Length > bestLen))
                {
                    best = mapping;
                    bestScore = score;
                    bestLen = mapping.DestRoot.Length;
                }
            }
            catch
            {
                // try next
            }
        }

        return best ?? mappings[0];
    }

    public static bool BindJournalToLanding(JobJournal journal, IReadOnlyList<CopyMapping> mappings)
    {
        if (mappings.Count == 0)
        {
            return false;
        }

        var changed = false;
        foreach (var file in journal.GetFiles())
        {
            var mapping = FindMapping(file, mappings);
            var dest = DestPathFor(mapping, file.RelativePath);
            if (string.Equals(dest, file.DestPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SameParent(dest, file.DestPath))
            {
                continue;
            }

            file.DestPath = dest;
            journal.UpsertFile(file);
            changed = true;
        }

        return changed;
    }

    private static bool SameParent(string left, string right)
    {
        var a = ParentOf(left);
        var b = ParentOf(right);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string ParentOf(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        var slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return slash < 0 ? "" : path[..slash].TrimEnd('\\', '/');
    }

    private static string StripUniquePrefix(string relativePath, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(relativePath))
        {
            return relativePath ?? "";
        }

        var head = prefix.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (relativePath.StartsWith(head, StringComparison.OrdinalIgnoreCase))
        {
            return relativePath[head.Length..];
        }

        if (string.Equals(relativePath, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return relativePath;
    }

    private static bool IsRelativeInside(string? relative) =>
        !string.IsNullOrWhiteSpace(relative)
        && relative != "."
        && !relative.StartsWith("..", StringComparison.Ordinal);

    /// <summary>
    /// Resolve every source root against one destination. Two+ folders always land in dest\FolderName
    /// (even if include is off) so trees do not smash. Duplicate folder names get " (2)", " (3)".
    /// </summary>
    public static IReadOnlyList<CopyMapping> ResolveAll(
        IReadOnlyList<string> sourcePaths,
        string destPath,
        bool includeSourceFolderName = true)
    {
        if (sourcePaths is null || sourcePaths.Count == 0)
        {
            throw new ArgumentException("At least one source is required.", nameof(sourcePaths));
        }

        var wrap = includeSourceFolderName || sourcePaths.Count > 1;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<CopyMapping>(sourcePaths.Count);
        var prefix = sourcePaths.Count > 1;
        foreach (var source in sourcePaths)
        {
            var mapping = Resolve(source, destPath, wrap);
            var destRoot = mapping.DestRoot;
            if (prefix && mapping.Kind == SourceKind.DriveRoot)
            {
                var letter = DriveLabel(mapping.SourceRoot);
                destRoot = Path.Combine(PathNormalizer.DirectoryPath(destPath), letter);
            }

            destRoot = UniqueDestRoot(destRoot, used);
            var uniquePrefix = prefix ? UniquePrefixName(destRoot, destPath, mapping) : "";
            list.Add(new CopyMapping
            {
                Kind = mapping.Kind,
                SourceRoot = mapping.SourceRoot,
                DestRoot = destRoot,
                SingleFile = mapping.SingleFile,
                SingleFileName = mapping.SingleFileName,
                UniqueRelativePrefix = uniquePrefix,
                TransportZipPath = mapping.TransportZipPath
            });
        }

        return list;
    }

    /// <summary>
    /// Resolve every source root against every destination. One dest keeps existing journal keys.
    /// Two+ dests prefix relative paths with a unique dest label so dest1 and dest2 do not collide.
    /// Pack-as-zip still packs once per destination (separate transport zip under each landing).
    /// </summary>
    public static IReadOnlyList<CopyMapping> ResolveFanOut(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string> destPaths,
        bool includeSourceFolderName = true)
    {
        if (destPaths is null || destPaths.Count == 0)
        {
            throw new ArgumentException("At least one destination is required.", nameof(destPaths));
        }

        if (destPaths.Count == 1)
        {
            return WithUserDest(ResolveAll(sourcePaths, destPaths[0], includeSourceFolderName), destPaths[0]);
        }

        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<CopyMapping>();
        foreach (var dest in destPaths)
        {
            var destKey = UniqueDestLabel(dest, usedKeys);
            string userDest;
            try
            {
                userDest = PathNormalizer.DirectoryPath(dest);
            }
            catch
            {
                userDest = dest;
            }

            foreach (var mapping in ResolveAll(sourcePaths, dest, includeSourceFolderName))
            {
                var prefix = string.IsNullOrEmpty(mapping.UniqueRelativePrefix)
                    ? destKey
                    : Path.Combine(destKey, mapping.UniqueRelativePrefix);
                list.Add(CloneMapping(mapping, userDest, prefix));
            }
        }

        return list;
    }

    public static string PreviewLandingSummary(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string> destPaths,
        bool includeSourceFolderName = true)
    {
        if (destPaths.Count == 0)
        {
            return "";
        }

        if (destPaths.Count == 1)
        {
            return PreviewLandingSummary(sourcePaths, destPaths[0], includeSourceFolderName);
        }

        var parts = new List<string>();
        foreach (var dest in destPaths)
        {
            var one = PreviewLandingSummary(sourcePaths, dest, includeSourceFolderName);
            if (string.IsNullOrEmpty(one))
            {
                continue;
            }

            const string prefix = "Will land in: ";
            parts.Add(one.StartsWith(prefix, StringComparison.Ordinal) ? one[prefix.Length..] : one);
        }

        return parts.Count == 0 ? "" : "Will land in: " + string.Join(" · ", parts);
    }

    public static string PreviewLandingSummary(
        IReadOnlyList<string> sourcePaths,
        string destPath,
        bool includeSourceFolderName = true)
    {
        if (sourcePaths.Count == 0 || string.IsNullOrWhiteSpace(destPath))
        {
            return "";
        }

        if (sourcePaths.Count == 1)
        {
            var one = PreviewLandingPath(sourcePaths[0], destPath, includeSourceFolderName);
            return string.IsNullOrEmpty(one) ? "" : "Will land in: " + one;
        }

        var wrap = includeSourceFolderName || sourcePaths.Count > 1;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (var source in sourcePaths)
        {
            var preview = PreviewLandingPath(source, destPath, wrap);
            if (string.IsNullOrEmpty(preview))
            {
                continue;
            }

            try
            {
                if (PathNormalizer.IsDriveRoot(source))
                {
                    preview = Path.Combine(PathNormalizer.DirectoryPath(destPath), DriveLabel(source));
                }
            }
            catch
            {
                // keep preview
            }

            preview = UniqueDestRoot(preview, used);
            parts.Add(preview);
        }

        if (parts.Count == 0)
        {
            return "";
        }

        var note = includeSourceFolderName
            ? ""
            : " (multiple folders keep their names so they do not smash)";
        return "Will land in: " + string.Join("; ", parts) + note;
    }

    public static FileRecord WithUniqueRelative(FileRecord record, CopyMapping mapping)
    {
        if (string.IsNullOrEmpty(mapping.UniqueRelativePrefix))
        {
            return record;
        }

        record.RelativePath = Path.Combine(mapping.UniqueRelativePrefix, record.RelativePath);
        return record;
    }

    private static IReadOnlyList<CopyMapping> WithUserDest(IReadOnlyList<CopyMapping> mappings, string destPath)
    {
        var dest = destPath;
        try
        {
            dest = PathNormalizer.DirectoryPath(destPath);
        }
        catch
        {
            // keep destPath
        }

        return mappings.Select(m => CloneMapping(m, dest, m.UniqueRelativePrefix)).ToList();
    }

    private static CopyMapping CloneMapping(CopyMapping mapping, string userDest, string uniquePrefix) =>
        new()
        {
            Kind = mapping.Kind,
            SourceRoot = mapping.SourceRoot,
            DestRoot = mapping.DestRoot,
            SingleFile = mapping.SingleFile,
            SingleFileName = mapping.SingleFileName,
            UniqueRelativePrefix = uniquePrefix,
            TransportZipPath = mapping.TransportZipPath,
            UserDestPath = userDest
        };

    private static string UniqueDestLabel(string destPath, HashSet<string> used)
    {
        string name;
        try
        {
            var dest = PathNormalizer.DirectoryPath(destPath);
            if (PathNormalizer.IsDriveRoot(dest))
            {
                name = DriveLabel(dest);
            }
            else
            {
                name = Path.GetFileName(dest.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name))
                {
                    name = dest;
                }
            }
        }
        catch
        {
            name = destPath;
        }

        return UniqueDestRoot(name, used);
    }

    private static string UniqueDestRoot(string destRoot, HashSet<string> used)
    {
        if (used.Add(destRoot))
        {
            return destRoot;
        }

        var n = 2;
        while (true)
        {
            var candidate = destRoot + " (" + n + ")";
            if (used.Add(candidate))
            {
                return candidate;
            }

            n++;
        }
    }

    private static string UniquePrefixName(string destRoot, string destPath, CopyMapping mapping)
    {
        if (mapping.SingleFile)
        {
            return mapping.SingleFileName ?? Path.GetFileName(destRoot);
        }

        try
        {
            var dest = PathNormalizer.DirectoryPath(destPath);
            var rel = Path.GetRelativePath(dest, destRoot);
            if (!string.IsNullOrWhiteSpace(rel) && rel != "." && !rel.StartsWith(".."))
            {
                return rel;
            }
        }
        catch
        {
            // fall through
        }

        var name = Path.GetFileName(destRoot.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? destRoot : name;
    }

    private static string DriveLabel(string sourceRoot)
    {
        var root = Path.GetPathRoot(sourceRoot) ?? "drive";
        var letter = root.TrimEnd('\\', '/', ':');
        return string.IsNullOrWhiteSpace(letter) ? "drive" : letter;
    }
}
