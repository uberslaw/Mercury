namespace Mercury;

public static class CloudPath
{
    public static bool LooksLikeCloudFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var p = path.Replace('/', '\\');
        if (p.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("SharePoint", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("Google Drive", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("Dropbox", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            if (Directory.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                return IsCloudPlaceholder(attrs);
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static bool IsCloudPlaceholder(FileAttributes attributes)
    {
        const FileAttributes recallOnDataAccess = (FileAttributes)0x400000;
        const FileAttributes recallOnOpen = (FileAttributes)0x40000;
        return attributes.HasFlag(recallOnDataAccess) || attributes.HasFlag(recallOnOpen);
    }
}

public static class FreeSpace
{
    public static string ShortageMessage(long needed, long available) =>
        $"Not enough free space at destination. Need {ByteFormatter.ToString(needed)}, available {ByteFormatter.ToString(available)}.";

    public static void CheckOrWarn(Job job, long needed, long? available, IJobLog log, string name)
    {
        if (job.Options.DryRun || available is null || needed <= available.Value)
        {
            return;
        }

        var message = ShortageMessage(needed, available.Value);
        if (job.Options.IgnoreFreeSpaceCheck)
        {
            log.Info(job.Id, name, "Warning: " + message + " Continuing because destination can expand.");
            return;
        }

        throw new IOException(message);
    }

    public static long? GetAvailableBytes(string path)
    {
        try
        {
            var target = path;
            if (!Directory.Exists(target))
            {
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    target = parent;
                }
            }

            if (NativeMethods.GetDiskFreeSpaceEx(target, out var available, out _, out _))
            {
                return (long)available;
            }

            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    return drive.AvailableFreeSpace;
                }
            }
        }
        catch
        {
            // unknown
        }

        return null;
    }
}
