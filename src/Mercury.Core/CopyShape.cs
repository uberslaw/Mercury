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
}
