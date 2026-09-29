namespace Mercury;

/// <summary>
/// Before a copy starts, spot destination names that Windows would reject or that collide
/// inside the job, and optionally suffix them so nothing is overwritten.
/// Checks are string-length only so they behave the same on Linux test hosts.
/// </summary>
public static class DestNameAdjuster
{
    public const string TransferSuffix = " - file-transfer";
    public const int MaxSegmentLength = 255;
    public const int MaxFullPathLength = 259;

    public sealed class Note
    {
        public string OriginalRelative { get; init; } = "";
        public string AdjustedRelative { get; init; } = "";
        public string Reason { get; init; } = "";
        public bool Skipped { get; init; }
        public string Message { get; init; } = "";
    }

    public static List<Note> Resolve(
        IList<FileRecord> files,
        bool fix,
        IEnumerable<string>? reservedDestPaths = null,
        IEnumerable<string>? reservedRelativePaths = null)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ClaimAll(taken, reservedDestPaths);
        ClaimAll(taken, reservedRelativePaths);

        var notes = new List<Note>();
        foreach (var file in files)
        {
            var originalRelative = file.RelativePath ?? "";
            var originalDest = string.IsNullOrEmpty(file.DestPath) ? originalRelative : file.DestPath;
            var fileName = FileNameOf(originalDest.Length > 0 ? originalDest : originalRelative);
            var length = HasLengthProblem(originalDest, originalRelative);
            var destKey = KeyOf(originalDest);
            var relKey = KeyOf(originalRelative);
            var duplicate = Taken(taken, destKey) || (relKey.Length > 0 && !string.Equals(relKey, destKey, StringComparison.Ordinal) && Taken(taken, relKey));
            if (!duplicate)
            {
                Claim(taken, destKey);
                Claim(taken, relKey);
            }

            if (!length && !duplicate)
            {
                continue;
            }

            var reason = length && duplicate ? "length, duplicate" : length ? "length" : "duplicate";
            if (!fix)
            {
                Skip(file, notes, originalRelative, reason, OffMessage(length, duplicate));
                continue;
            }

            if (ParentSegmentTooLong(originalDest) || ParentSegmentTooLong(originalRelative))
            {
                Skip(file, notes, originalRelative, "length",
                    "A folder name is longer than 255 characters. Mercury only renames the file, so this one was skipped.");
                continue;
            }

            var maxLen = MaxFileNameLength(originalDest, fileName);
            var adjusted = Allocate(fileName, originalDest, originalRelative, maxLen, taken);
            if (adjusted is null)
            {
                Skip(file, notes, originalRelative, reason,
                    "The destination name is too long or already used, and a file-transfer suffix would not fit. Skipped.");
                continue;
            }

            var newRelative = ReplaceFileName(originalRelative, adjusted);
            var newDest = string.IsNullOrEmpty(file.DestPath)
                ? ReplaceFileName(originalDest, adjusted)
                : ReplaceFileName(file.DestPath, adjusted);
            file.RelativePath = newRelative;
            file.DestPath = newDest;
            Claim(taken, KeyOf(newDest));
            Claim(taken, KeyOf(newRelative));
            notes.Add(new Note
            {
                OriginalRelative = originalRelative,
                AdjustedRelative = file.RelativePath,
                Reason = reason,
                Skipped = false,
                Message = $"{reason}: {originalRelative} → {file.RelativePath}"
            });
        }

        return notes;
    }

    public static void Publish(Job job, JobJournal journal, IJobLog log, string name, IReadOnlyList<Note> notes)
    {
        if (notes.Count == 0)
        {
            return;
        }

        job.NameNotices ??= [];
        var adjusted = 0;
        var skipped = 0;
        foreach (var note in notes)
        {
            var line = note.Skipped
                ? $"Name skipped ({note.Reason}): {note.OriginalRelative} — {note.Message}"
                : $"Name adjusted ({note.Reason}): {note.OriginalRelative} → {note.AdjustedRelative}";
            job.NameNotices.Add(line);
            if (note.Skipped)
            {
                skipped++;
                log.Error(job.Id, name, line);
                journal.AddIssue(new TransferIssue
                {
                    RelativePath = note.OriginalRelative,
                    Kind = IssueKind.CopyError,
                    Message = note.Message
                });
            }
            else
            {
                adjusted++;
                log.Info(job.Id, name, line);
            }
        }

        log.Info(job.Id, name,
            $"Destination names: {notes.Count} long or duplicate ({adjusted} adjusted, {skipped} skipped).");
        journal.TrySaveJob(job);
    }

    public static bool IsLongPathPrefixed(string? path) =>
        !string.IsNullOrEmpty(path)
        && (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith("//?/", StringComparison.Ordinal));

    public static bool HasLengthProblem(string? destPath, string? relativePath)
    {
        if (SegmentTooLong(relativePath) || SegmentTooLong(destPath))
        {
            return true;
        }

        if (string.IsNullOrEmpty(destPath) || IsLongPathPrefixed(destPath))
        {
            return false;
        }

        return destPath.Length > MaxFullPathLength;
    }

    private static void Skip(FileRecord file, List<Note> notes, string originalRelative, string reason, string message)
    {
        file.Status = FileCopyStatus.Skipped;
        file.Error = message;
        notes.Add(new Note
        {
            OriginalRelative = originalRelative,
            AdjustedRelative = originalRelative,
            Reason = reason,
            Skipped = true,
            Message = message
        });
    }

    private static string OffMessage(bool length, bool duplicate)
    {
        if (length && duplicate)
        {
            return "Destination name is too long and duplicates another file in this job. Fix long or duplicate names is off, so this file was skipped.";
        }

        if (duplicate)
        {
            return "Destination name duplicates another file in this job. Fix long or duplicate names is off, so this file was skipped.";
        }

        return "Destination path is too long for Windows. Fix long or duplicate names is off, so this file was skipped.";
    }

    private static string? Allocate(
        string fileName,
        string destPath,
        string relativePath,
        int maxLen,
        HashSet<string> taken)
    {
        if (maxLen < 1)
        {
            return null;
        }

        var destParent = ParentOf(destPath);
        var relParent = ParentOf(relativePath);
        for (var i = 0; i < 1000; i++)
        {
            var candidate = BuildName(fileName, i, maxLen);
            if (candidate.Length == 0 || candidate.Length > MaxSegmentLength || candidate.Length > maxLen)
            {
                continue;
            }

            var full = destParent.Length == 0 ? candidate : destParent + Separator(destPath) + candidate;
            if (!IsLongPathPrefixed(full) && full.Length > MaxFullPathLength)
            {
                continue;
            }

            var relative = relParent.Length == 0 ? candidate : relParent + Separator(relativePath.Length == 0 ? destPath : relativePath) + candidate;
            if (Taken(taken, KeyOf(full)) || Taken(taken, KeyOf(relative)))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static void ClaimAll(HashSet<string> taken, IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return;
        }

        foreach (var path in paths)
        {
            Claim(taken, KeyOf(path));
        }
    }

    private static void Claim(HashSet<string> taken, string key)
    {
        if (key.Length > 0)
        {
            taken.Add(key);
        }
    }

    private static bool Taken(HashSet<string> taken, string key) =>
        key.Length > 0 && taken.Contains(key);

    internal static string BuildName(string fileName, int collisionIndex, int maxLen)
    {
        var ext = ExtensionOf(fileName);
        var baseName = ext.Length > 0 && fileName.Length >= ext.Length
            ? fileName[..^ext.Length]
            : fileName;
        var suffix = collisionIndex <= 0
            ? TransferSuffix
            : TransferSuffix + " (" + (collisionIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        var tail = suffix + ext;
        if (tail.Length >= maxLen)
        {
            var hard = baseName + tail;
            return hard.Length <= maxLen ? hard : hard[..maxLen];
        }

        if (baseName.Length + tail.Length <= maxLen)
        {
            return baseName + tail;
        }

        var room = maxLen - tail.Length;
        var trimmed = baseName[..Math.Min(baseName.Length, room)].TrimEnd();
        if (trimmed.Length == 0)
        {
            trimmed = baseName[..Math.Min(baseName.Length, room)];
        }

        return trimmed + tail;
    }

    private static int MaxFileNameLength(string destPath, string fileName)
    {
        var max = MaxSegmentLength;
        if (string.IsNullOrEmpty(destPath) || IsLongPathPrefixed(destPath))
        {
            return max;
        }

        var parentLen = Math.Max(0, destPath.Length - fileName.Length);
        var pathBudget = MaxFullPathLength - parentLen;
        if (pathBudget < max)
        {
            max = pathBudget;
        }

        return max;
    }

    private static bool SegmentTooLong(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var part in path.Split('\\', '/'))
        {
            if (part.Length > MaxSegmentLength)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ParentSegmentTooLong(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var parts = path.Split('\\', '/');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Length > MaxSegmentLength)
            {
                return true;
            }
        }

        return false;
    }

    private static string FileNameOf(string path)
    {
        var slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string ParentOf(string path)
    {
        var slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return slash < 0 ? "" : path[..slash];
    }

    private static char Separator(string path)
    {
        var slash = path.LastIndexOf('/');
        var back = path.LastIndexOf('\\');
        if (slash > back)
        {
            return '/';
        }

        return '\\';
    }

    private static string ReplaceFileName(string path, string newName)
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

    private static string ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1)
        {
            return "";
        }

        return fileName[dot..];
    }

    private static string KeyOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    }
}
