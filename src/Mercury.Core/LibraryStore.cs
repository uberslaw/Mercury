using System.Text.Json;

namespace Mercury;

public sealed class RecentLocations
{
    public const int Limit = 10;

    public List<string> Sources { get; set; } = [];
    public List<string> Destinations { get; set; } = [];
    /// <summary>Last folder opened by Browse Source (independent of destination).</summary>
    public string? LastSourceDir { get; set; }
    /// <summary>Last folder opened by Browse Destination (independent of source).</summary>
    public string? LastDestDir { get; set; }
}

public sealed class SavedJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public JobOptions Options { get; set; } = new();
}

public static class LibraryStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static RecentLocations LoadRecents(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.RecentsFile))
            {
                return JsonSerializer.Deserialize<RecentLocations>(File.ReadAllText(paths.RecentsFile), Json)
                       ?? new RecentLocations();
            }
        }
        catch
        {
            // defaults
        }

        return new RecentLocations();
    }

    public static void SaveRecents(AppPaths paths, RecentLocations recents)
    {
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.RecentsFile, JsonSerializer.Serialize(recents, Json));
    }

    public static void Remember(List<string> list, string path, int limit = RecentLocations.Limit)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var trimmed = path.Trim().Trim('"');
        list.RemoveAll(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, trimmed);
        while (list.Count > limit)
        {
            list.RemoveAt(list.Count - 1);
        }
    }

    public static void RememberBrowseDir(RecentLocations recents, bool isSource, string path)
    {
        var dir = ExistingDirectoryOf(path);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        if (isSource)
        {
            recents.LastSourceDir = dir;
        }
        else
        {
            recents.LastDestDir = dir;
        }
    }

    /// <summary>
    /// Folder the browse dialog should open. Source never falls back to last destination.
    /// </summary>
    public static string? BrowseStartDir(bool isSource, RecentLocations recents, string? currentPath)
    {
        var last = isSource ? recents.LastSourceDir : recents.LastDestDir;
        return ExistingDirectoryOf(last) ?? ExistingDirectoryOf(currentPath);
    }

    public static string? ExistingDirectoryOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('"');
        try
        {
            if (Directory.Exists(trimmed))
            {
                return trimmed;
            }

            if (File.Exists(trimmed))
            {
                var parent = Path.GetDirectoryName(trimmed);
                return Directory.Exists(parent) ? parent : null;
            }

            var ancestor = Path.GetDirectoryName(trimmed);
            return Directory.Exists(ancestor) ? ancestor : null;
        }
        catch
        {
            return null;
        }
    }

    public static List<SavedJob> LoadSavedJobs(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.SavedJobsFile))
            {
                return JsonSerializer.Deserialize<List<SavedJob>>(File.ReadAllText(paths.SavedJobsFile), Json)
                       ?? [];
            }
        }
        catch
        {
            // defaults
        }

        return [];
    }

    public static void SaveSavedJobs(AppPaths paths, IReadOnlyList<SavedJob> jobs)
    {
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.SavedJobsFile, JsonSerializer.Serialize(jobs, Json));
    }
}
