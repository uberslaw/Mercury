using System.Text.Json;

namespace Mercury;

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static BandwidthSettings Load(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.SettingsFile))
            {
                var json = File.ReadAllText(paths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<BandwidthSettings>(json, Json) ?? new BandwidthSettings();
                SeedPackLists(loaded);
                return loaded;
            }
        }
        catch
        {
            // defaults
        }

        var fresh = new BandwidthSettings();
        SeedPackLists(fresh);
        return fresh;
    }

    public static void Save(AppPaths paths, BandwidthSettings settings)
    {
        Directory.CreateDirectory(paths.DataRoot);
        SeedPackLists(settings);
        settings.PackExtensions = PackPolicy.NormalizeList(settings.PackExtensions);
        settings.NeverPackExtensions = PackPolicy.NormalizeList(settings.NeverPackExtensions);
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(settings, Json));
    }

    private static void SeedPackLists(BandwidthSettings settings)
    {
        settings.PackExtensions = PackPolicy.NormalizeList(settings.PackExtensions);
        settings.NeverPackExtensions ??= PackPolicy.DefaultNeverPackExtensions();
        settings.NeverPackExtensions = PackPolicy.NormalizeList(settings.NeverPackExtensions);
    }

    public static string? LoadLastJobId(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.LastJobFile))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(paths.LastJobFile));
            if (doc.RootElement.TryGetProperty("jobId", out var id))
            {
                return id.GetString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static void SaveLastJobId(AppPaths paths, string jobId)
    {
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.LastJobFile, JsonSerializer.Serialize(new { jobId }, Json));
    }
}

public sealed class FileJobLog : IJobLog, IDisposable
{
    private readonly AppPaths _paths;
    private readonly object _lock = new();
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.Ordinal);

    public event Action<LogEvent>? LineWritten;

    public FileJobLog(AppPaths paths)
    {
        _paths = paths;
    }

    public void Info(string jobId, string jobName, string message) =>
        Write(jobId, jobName, "Info", message);

    public void Error(string jobId, string jobName, string message) =>
        Write(jobId, jobName, "Error", message);

    private void Write(string jobId, string jobName, string level, string message)
    {
        var ev = new LogEvent
        {
            Utc = DateTime.UtcNow,
            JobId = jobId,
            JobName = jobName,
            Level = level,
            Message = message
        };

        lock (_lock)
        {
            if (!_writers.TryGetValue(jobId, out var writer))
            {
                Directory.CreateDirectory(_paths.Logs);
                writer = new StreamWriter(new FileStream(
                    _paths.JobLogFile(jobId),
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                {
                    AutoFlush = true
                };
                _writers[jobId] = writer;
            }

            writer.WriteLine($"{ev.Utc:O} [{level}] {message}");
            if (level == "Error" ||
                message.StartsWith("Deferred ", StringComparison.Ordinal) ||
                message.StartsWith("Retrying deferred", StringComparison.Ordinal) ||
                message.StartsWith("TLS ", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("fingerprint", StringComparison.OrdinalIgnoreCase))
            {
                MercuryErrorLog.Write(_paths, jobName, $"[{level}] {message}");
            }
        }

        LineWritten?.Invoke(ev);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var w in _writers.Values)
            {
                w.Dispose();
            }

            _writers.Clear();
        }
    }

    public static bool Exists(AppPaths paths, string jobId) =>
        !string.IsNullOrWhiteSpace(jobId) && File.Exists(paths.JobLogFile(jobId));

    public static IReadOnlyList<string> ReadAllLines(AppPaths paths, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return [];
        }

        var file = paths.JobLogFile(jobId);
        if (!File.Exists(file))
        {
            return [];
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}
