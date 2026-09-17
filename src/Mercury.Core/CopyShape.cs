namespace Mercury;

public static class CopyShape
{
    public static CopyMapping Resolve(string sourcePath, string destPath)
    {
        var source = PathNormalizer.Normalize(sourcePath);
        var dest = PathNormalizer.Normalize(destPath);

        if (File.Exists(source))
        {
            var fileName = Path.GetFileName(source);
            if (Directory.Exists(dest) || dest.EndsWith('\\') || dest.EndsWith('/'))
            {
                var destDir = dest.TrimEnd('\\', '/');
                return new CopyMapping
                {
                    Kind = SourceKind.File,
                    SourceRoot = Path.GetDirectoryName(source) ?? source,
                    DestRoot = destDir,
                    SingleFile = true,
                    SingleFileName = fileName
                };
            }

            var destParent = Path.GetDirectoryName(dest);
            if (string.IsNullOrEmpty(destParent))
            {
                throw new InvalidOperationException($"Destination is not a writable path: {dest}");
            }

            return new CopyMapping
            {
                Kind = SourceKind.File,
                SourceRoot = Path.GetDirectoryName(source) ?? source,
                DestRoot = destParent,
                SingleFile = true,
                SingleFileName = Path.GetFileName(dest)
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
                DestRoot = dest.TrimEnd('\\', '/'),
                SingleFile = false
            };
        }

        var folderName = Path.GetFileName(source.TrimEnd('\\', '/'));
        return new CopyMapping
        {
            Kind = SourceKind.Folder,
            SourceRoot = source,
            DestRoot = Path.Combine(dest.TrimEnd('\\', '/'), folderName),
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
}
