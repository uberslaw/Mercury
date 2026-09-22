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
