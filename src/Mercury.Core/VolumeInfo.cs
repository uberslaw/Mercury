using System.Runtime.InteropServices;
using System.Text;

namespace Mercury;

public static class VolumeInfo
{
    public static string? GetSerial(string path)
    {
        try
        {
            var root = Path.GetPathRoot(PathNormalizer.Normalize(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            if (!root.EndsWith('\\'))
            {
                root += "\\";
            }

            if (!NativeMethods.GetVolumeInformation(
                    root,
                    null,
                    0,
                    out var serial,
                    out _,
                    out _,
                    null,
                    0))
            {
                return null;
            }

            return serial.ToString("X8");
        }
        catch
        {
            return null;
        }
    }

    public static string? RemapIfMissing(string originalPath, string? serial)
    {
        try
        {
            if (File.Exists(originalPath) || Directory.Exists(originalPath))
            {
                return originalPath;
            }
        }
        catch
        {
            // try remap
        }

        if (string.IsNullOrWhiteSpace(serial) || originalPath.Length < 2 || originalPath[1] != ':')
        {
            return null;
        }

        var rest = originalPath[2..];
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                if (string.Equals(GetSerial(drive.Name), serial, StringComparison.OrdinalIgnoreCase))
                {
                    return drive.Name.TrimEnd('\\') + rest;
                }
            }
            catch
            {
                // skip
            }
        }

        return null;
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}
