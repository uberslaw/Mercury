using System.Text.Json;

namespace Mercury;

public static class QueueStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static List<Job> Load(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.QueueFile))
            {
                return JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(paths.QueueFile), Json) ?? [];
            }
        }
        catch
        {
            // defaults
        }

        return [];
    }

    public static void Save(AppPaths paths, IReadOnlyList<Job> jobs)
    {
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.QueueFile, JsonSerializer.Serialize(jobs, Json));
    }
}
