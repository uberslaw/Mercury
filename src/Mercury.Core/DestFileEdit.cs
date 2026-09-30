namespace Mercury;

/// <summary>
/// After a copy, rename a journalled destination file and set its timestamps without copying again.
/// The new name is only the last path segment and must stay inside the job destination.
/// </summary>
public static class DestFileEdit
{
    public readonly record struct EditResult(bool Ok, string? Error);

    public static string? ValidateFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a file name.";
        }

        var trimmed = name.Trim();
        if (trimmed is "." or "..")
        {
            return "Edit the file name only, not a path.";
        }

        if (trimmed.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            return "Edit the file name only, not a path.";
        }

        foreach (var c in trimmed)
        {
            if (char.IsControl(c) || c is '"' or '<' or '>' or '|' or '*' or '?')
            {
                return "That file name contains characters Windows would reject.";
            }
        }

        if (trimmed.EndsWith('.') || trimmed.EndsWith(' '))
        {
            return "Windows rejects a file name that ends with a space or a dot.";
        }

        if (trimmed.Length > DestNameAdjuster.MaxSegmentLength)
        {
            return "File name is longer than 255 characters.";
        }

        return null;
    }

    public static bool IsInsideDestination(string destRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(destRoot) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(destRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(candidate);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var prefix = root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static EditResult Apply(
        Job job,
        JobJournal journal,
        string relativePath,
        string newFileName,
        DateTime newLastWrite)
    {
        var nameError = ValidateFileName(newFileName);
        if (nameError is not null)
        {
            return new EditResult(false, nameError);
        }

        var file = journal.GetFiles()
            .FirstOrDefault(f => string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return new EditResult(false, "That file is not in the job journal.");
        }

        if (string.IsNullOrWhiteSpace(file.DestPath) || !File.Exists(file.DestPath))
        {
            return new EditResult(false, "Destination file is missing.");
        }

        var newName = newFileName.Trim();
        var directory = Path.GetDirectoryName(file.DestPath);
        if (string.IsNullOrEmpty(directory))
        {
            return new EditResult(false, "Destination file is missing.");
        }

        var newDest = Path.Combine(directory, newName);
        if (!JobDestinations.Roots(job).Any(d => IsInsideDestination(d, newDest)))
        {
            return new EditResult(false, "The new name would leave the job destination.");
        }

        var sameFile = string.Equals(
            Path.GetFullPath(file.DestPath),
            Path.GetFullPath(newDest),
            StringComparison.OrdinalIgnoreCase);
        if (!sameFile && File.Exists(newDest))
        {
            return new EditResult(false, "A destination file with that name already exists.");
        }

        var newRelative = ReplaceLastSegment(file.RelativePath, newName);
        var others = journal.GetFiles();
        if (others.Any(f =>
                !string.Equals(f.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.RelativePath, newRelative, StringComparison.OrdinalIgnoreCase)))
        {
            return new EditResult(false, "A destination file with that name already exists.");
        }

        try
        {
            if (!sameFile)
            {
                File.Move(file.DestPath, newDest);
            }

            var utc = ToUtc(newLastWrite);
            FileMetadata.SetDestTimestamps(newDest, utc);
            var stored = File.GetLastWriteTimeUtc(newDest);
            journal.UpdateDestIdentity(file.RelativePath, newRelative, newDest, stored);
        }
        catch (Exception ex)
        {
            return new EditResult(false, ex.Message);
        }

        return new EditResult(true, null);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
    };

    private static string ReplaceLastSegment(string path, string newName)
    {
        if (string.IsNullOrEmpty(path))
        {
            return newName;
        }

        var slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        if (slash < 0)
        {
            return newName;
        }

        return path[..(slash + 1)] + newName;
    }
}
