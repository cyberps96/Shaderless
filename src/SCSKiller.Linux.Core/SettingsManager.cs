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

    private static readonly AsyncLocal<string?> _testSettingsPathOverride = new();

    public static string? TestSettingsPathOverride
    {
        get => _testSettingsPathOverride.Value;
        set => _testSettingsPathOverride.Value = value;
    }

    public static string DefaultSettingsFilePath =>
        !string.IsNullOrEmpty(TestSettingsPathOverride)
            ? TestSettingsPathOverride
            : (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHADERLESS_SETTINGS_PATH"))
                ? Environment.GetEnvironmentVariable("SHADERLESS_SETTINGS_PATH")!
                : Path.Combine(AppPaths.DataDir, "settings.json"));

    public static AppSettings Load(string? customPath = null)
    {
        string path = customPath ?? DefaultSettingsFilePath;
        if (!File.Exists(path))
            return new AppSettings(AutoRecordAllGames: false, AutoCompileOnExit: false, MaxCacheSizeGb: AppSettings.DefaultMaxCacheSizeGb);

        try
        {
            string json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded == null)
                return new AppSettings(AutoRecordAllGames: false, AutoCompileOnExit: false, MaxCacheSizeGb: AppSettings.DefaultMaxCacheSizeGb);

            int clampedGb = AppSettings.ClampMaxCacheSizeGb(loaded.MaxCacheSizeGb);
            return loaded.MaxCacheSizeGb == clampedGb
                ? loaded
                : loaded with { MaxCacheSizeGb = clampedGb };
        }
        catch
        {
            return new AppSettings(AutoRecordAllGames: false, AutoCompileOnExit: false, MaxCacheSizeGb: AppSettings.DefaultMaxCacheSizeGb);
        }
    }

    public static void Save(AppSettings settings, string? customPath = null)
    {
        string path = customPath ?? DefaultSettingsFilePath;
        var normalized = settings with { MaxCacheSizeGb = AppSettings.ClampMaxCacheSizeGb(settings.MaxCacheSizeGb) };
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            string json = JsonSerializer.Serialize(normalized, JsonOptions);
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
