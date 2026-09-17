using System.Text.Json;

namespace Mercury;

public static class CatcherStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyList<CatcherEnvelope> LoadTemplates(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.CatcherTemplatesFile))
            {
                var catalog = JsonSerializer.Deserialize<CatcherCatalog>(
                    File.ReadAllText(paths.CatcherTemplatesFile), Json);
                return catalog?.Templates ?? [];
            }
        }
        catch
        {
            // defaults
        }

        return [];
    }

    public static void SaveTemplates(AppPaths paths, IReadOnlyList<CatcherEnvelope> templates)
    {
        Directory.CreateDirectory(paths.DataRoot);
        var catalog = new CatcherCatalog { Templates = templates.ToList() };
        File.WriteAllText(paths.CatcherTemplatesFile, JsonSerializer.Serialize(catalog, Json));
        var readBack = LoadTemplates(paths);
        if (readBack.Count != templates.Count)
        {
            throw new IOException("Catcher templates were written but could not be read back.");
        }
    }

    public static void Upsert(AppPaths paths, CatcherEnvelope envelope)
    {
        CatcherPack.ValidateEnvelope(envelope);
        var list = LoadTemplates(paths).ToList();
        var i = list.FindIndex(t => t.Id == envelope.Id);
        if (i >= 0)
        {
            list[i] = envelope;
        }
        else
        {
            list.Add(envelope);
        }

        SaveTemplates(paths, list);
    }

    public static void Remove(AppPaths paths, string templateId)
    {
        var list = LoadTemplates(paths).Where(t => t.Id != templateId).ToList();
        SaveTemplates(paths, list);
    }

    public static CatcherLocalSettings LoadSettings(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.CatcherSettingsFile))
            {
                return JsonSerializer.Deserialize<CatcherLocalSettings>(
                    File.ReadAllText(paths.CatcherSettingsFile), Json) ?? new CatcherLocalSettings();
            }
        }
        catch
        {
            // defaults
        }

        return new CatcherLocalSettings();
    }

    public static void SaveSettings(AppPaths paths, CatcherLocalSettings settings)
    {
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.CatcherSettingsFile, JsonSerializer.Serialize(settings, Json));
    }

    public static string DefaultReceiveFolder(AppPaths paths)
    {
        var folder = Path.Combine(paths.DataRoot, "received");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
