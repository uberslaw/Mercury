namespace Mercury;

public static class PathNormalizer
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.", nameof(path));
        }

        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A path is required.", nameof(path));
        }

        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            trimmed += "\\";
        }

        return Path.GetFullPath(trimmed);
    }

    public static bool IsUnc(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var p = path.Trim().Trim('"');
        return p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal);
    }

    public static bool IsNetwork(string path)
    {
        var p = path.Trim().Trim('"');
        if (IsUnc(p))
        {
            return true;
        }

        if (p.Length >= 2 && p[1] == ':')
        {
            try
            {
                var drive = new DriveInfo(p[..2]);
                return drive.DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public static bool IsDriveRoot(string path)
    {
        var full = Normalize(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        return string.Equals(
            full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnder(string child, string parent)
    {
        var p = Normalize(parent).TrimEnd('\\') + "\\";
        var c = Normalize(child).TrimEnd('\\') + "\\";
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static string Combine(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return root;
        }

        var rel = relative.Replace('/', '\\').TrimStart('\\');
        return Path.GetFullPath(Path.Combine(root, rel));
    }
}
