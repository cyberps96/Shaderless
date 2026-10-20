using System.Text.Json;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core;

/// <summary>Loads and saves persistent user settings in ~/.local/share/shaderless/settings.json.</summary>
public static class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultSettingsFilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Load(string? customPath = null)
    {
        string path = customPath ?? DefaultSettingsFilePath;
        if (!File.Exists(path))
            return new AppSettings(AutoRecordAllGames: false, AutoCompileOnExit: false);

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? new AppSettings(AutoRecordAllGames: false, AutoCompileOnExit: false);
        }
        catch
        {
            return new AppSettings(AutoRecordAllGames: false, AutoCompileOnExit: false);
        }
    }

    public static void Save(AppSettings settings, string? customPath = null)
    {
        string path = customPath ?? DefaultSettingsFilePath;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public static AppSettings SetGameAutoRecordOptOut(string gameId, bool optOut, string? customPath = null)
    {
        var current = Load(customPath);
        var set = new HashSet<string>(current.DisabledAutoRecordGameIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        if (optOut) set.Add(gameId);
        else set.Remove(gameId);

        var updated = current with { DisabledAutoRecordGameIds = set.OrderBy(x => x, StringComparer.Ordinal).ToList() };
        Save(updated, customPath);
        return updated;
    }
}
