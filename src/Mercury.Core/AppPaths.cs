using System.Globalization;

namespace Mercury;

public sealed class DataPathRow
{
    public string Label { get; init; } = "";
    public string FullPath { get; init; } = "";

    public string Folder
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FullPath))
            {
                return "";
            }

            if (Directory.Exists(FullPath))
            {
                return FullPath;
            }

            return System.IO.Path.GetDirectoryName(FullPath) ?? FullPath;
        }
    }
}

public sealed class AppPaths
{
    public const string PortableFlagName = "portable.flag";

    public string DataRoot { get; }
    public string Logs { get; }
    public string Jobs { get; }
    public string SettingsFile { get; }
    public string LastJobFile { get; }
    public string RecentsFile { get; }
    public string SavedJobsFile { get; }
    public string QueueFile { get; }
    public string HistoryFile { get; }
    public string ThemesFile { get; }
    public string CatcherTemplatesFile { get; }
    public string CatcherSettingsFile { get; }
    public string PortableFlagFile { get; }
    public string ExeDirectory { get; }
    public string PortableDataRoot { get; }
    public string RoamingDataRoot { get; }
    public bool IsPortable { get; }
    public bool UsedLegacyPortable { get; }
    public bool UsedLocalAppDataFallback { get; }

    public AppPaths(string? exeDirectory = null)
        : this(exeDirectory, roamingRoot: null, forceExplicit: exeDirectory is not null)
    {
    }

    public AppPaths(string? exeDirectory, string? roamingRoot, bool forceExplicit)
    {
        ExeDirectory = string.IsNullOrWhiteSpace(exeDirectory)
            ? AppContext.BaseDirectory
            : exeDirectory;
        PortableDataRoot = Path.Combine(ExeDirectory, "data");
        RoamingDataRoot = string.IsNullOrWhiteSpace(roamingRoot)
            ? DefaultRoamingRoot()
            : roamingRoot;
        PortableFlagFile = Path.Combine(PortableDataRoot, PortableFlagName);

        if (forceExplicit)
        {
            DataRoot = PortableDataRoot;
            IsPortable = true;
            UsedLegacyPortable = false;
            UsedLocalAppDataFallback = false;
            Directory.CreateDirectory(DataRoot);
        }
        else
        {
            var resolved = ResolveProduction(ExeDirectory, RoamingDataRoot);
            DataRoot = resolved.Root;
            IsPortable = resolved.Portable;
            UsedLegacyPortable = resolved.Legacy;
            UsedLocalAppDataFallback = !resolved.Portable;
            Directory.CreateDirectory(DataRoot);
        }

        Logs = Path.Combine(DataRoot, "logs");
        Jobs = Path.Combine(DataRoot, "jobs");
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Jobs);
        SettingsFile = Path.Combine(DataRoot, "settings.json");
        LastJobFile = Path.Combine(DataRoot, "last-job.json");
        RecentsFile = Path.Combine(DataRoot, "recents.json");
        SavedJobsFile = Path.Combine(DataRoot, "saved-jobs.json");
        QueueFile = Path.Combine(DataRoot, "queue.json");
        HistoryFile = Path.Combine(DataRoot, "history.db");
        ThemesFile = Path.Combine(DataRoot, "themes.json");
        CatcherTemplatesFile = Path.Combine(DataRoot, "catcher-templates.json");
        CatcherSettingsFile = Path.Combine(DataRoot, "catcher-settings.json");
    }

    public IReadOnlyList<string> ThemeFileCandidates()
    {
        var files = new List<string> { ThemesFile };
        AddThemeCandidate(files, Path.Combine(RoamingDataRoot, "themes.json"));
        AddThemeCandidate(files, Path.Combine(PortableDataRoot, "themes.json"));
        return files;
    }

    public IReadOnlyList<string> ExistingAlternateThemeFiles()
    {
        var extras = new List<string>();
        foreach (var file in ThemeFileCandidates())
        {
            if (PathsEqual(file, ThemesFile) || !File.Exists(file))
            {
                continue;
            }

            extras.Add(file);
        }

        return extras;
    }

    private static void AddThemeCandidate(List<string> files, string path)
    {
        if (files.Any(existing => PathsEqual(existing, path)))
        {
            return;
        }

        files.Add(path);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string DefaultRoamingRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mercury");

    public string JobDirectory(string jobId) => Path.Combine(Jobs, jobId);

    public string JobLogFile(string jobId) => Path.Combine(Logs, $"job-{jobId}.log");

    public string ErrorLogFile(DateTime? utc = null) =>
        Path.Combine(Logs, $"mercury-{(utc ?? DateTime.UtcNow):yyyyMMdd}.log");

    public string CatcherErrorLogFile(DateTime? utc = null) =>
        Path.Combine(Logs, $"mercury-catcher-{(utc ?? DateTime.UtcNow):yyyyMMdd}.log");

    public IReadOnlyList<DataPathRow> ListWritePaths()
    {
        var rows = new List<DataPathRow>
        {
            new() { Label = "Data folder", FullPath = DataRoot },
            new() { Label = "Error log (today)", FullPath = ErrorLogFile() },
            new() { Label = "Catcher error log (today)", FullPath = CatcherErrorLogFile() },
            new() { Label = "Job logs folder", FullPath = Logs },
            new() { Label = "Resume journals (job.db)", FullPath = Jobs },
            new() { Label = "settings.json", FullPath = SettingsFile },
            new() { Label = "last-job.json", FullPath = LastJobFile },
            new() { Label = "recents.json", FullPath = RecentsFile },
            new() { Label = "saved-jobs.json", FullPath = SavedJobsFile },
            new() { Label = "queue.json", FullPath = QueueFile },
            new() { Label = "history.db", FullPath = HistoryFile },
            new() { Label = "themes.json", FullPath = ThemesFile },
            new() { Label = "catcher-templates.json", FullPath = CatcherTemplatesFile },
            new() { Label = "catcher-settings.json", FullPath = CatcherSettingsFile },
            new() { Label = "Portable flag", FullPath = PortableFlagFile },
            new() { Label = "Roaming AppData (default)", FullPath = RoamingDataRoot },
            new() { Label = "Beside-exe data (portable)", FullPath = PortableDataRoot }
        };

        try
        {
            var catcher = CatcherStore.LoadSettings(this);
            if (!string.IsNullOrWhiteSpace(catcher.DestinationFolder))
            {
                rows.Add(new DataPathRow { Label = "Catcher receive folder", FullPath = catcher.DestinationFolder });
            }
        }
        catch
        {
            // settings may not exist yet
        }

        return rows;
    }

    public static bool HasJobJournals(string dataRoot)
    {
        var jobs = Path.Combine(dataRoot, "jobs");
        if (!Directory.Exists(jobs))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(jobs, "job.db", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    public static (string Root, bool Portable, bool Legacy) ResolveProduction(string exeDirectory, string roamingRoot)
    {
        var portableRoot = Path.Combine(exeDirectory, "data");
        var flag = Path.Combine(portableRoot, PortableFlagName);
        if (File.Exists(flag) && TryUse(portableRoot))
        {
            return (portableRoot, true, false);
        }

        var roamingHasJobs = HasJobJournals(roamingRoot);
        var portableHasJobs = HasJobJournals(portableRoot);
        if (!roamingHasJobs && portableHasJobs && TryUse(portableRoot))
        {
            return (portableRoot, true, true);
        }

        Directory.CreateDirectory(roamingRoot);
        return (roamingRoot, false, false);
    }

    public static void SetPortablePreferred(string exeDirectory, bool portable)
    {
        var portableRoot = Path.Combine(exeDirectory, "data");
        Directory.CreateDirectory(portableRoot);
        var flag = Path.Combine(portableRoot, PortableFlagName);
        if (portable)
        {
            File.WriteAllText(flag, "1");
        }
        else if (File.Exists(flag))
        {
            File.Delete(flag);
        }
    }

    private static bool TryUse(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class MercuryErrorLog
{
    private static readonly object Gate = new();

    public static void Write(AppPaths paths, string source, string message, Exception? ex = null, bool catcher = false)
    {
        try
        {
            Directory.CreateDirectory(paths.Logs);
            var file = catcher ? paths.CatcherErrorLogFile() : paths.ErrorLogFile();
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:O} [{source}] {message}");
            if (ex is not null)
            {
                line += Environment.NewLine + ex;
            }

            lock (Gate)
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // never throw from the error log
        }
    }
}
