using System.Text.Json.Serialization;

namespace SCSKiller.Linux.Core.Models;

/// <summary>Persistent user preferences stored in ~/.local/share/shaderless/settings.json.
/// All automation options default to false (OFF); max shader cache size defaults to 10 GB.</summary>
public record AppSettings(
    bool AutoRecordAllGames = false,
    bool AutoCompileOnExit = false,
    IReadOnlyList<string>? DisabledAutoRecordGameIds = null,
    int MaxCacheSizeGb = AppSettings.DefaultMaxCacheSizeGb
)
{
    public const int DefaultMaxCacheSizeGb = 10;
    public const int MinMaxCacheSizeGb = 5;
    public const int MaxMaxCacheSizeGb = 500;

    public static int ClampMaxCacheSizeGb(int value) =>
        value <= 0 ? DefaultMaxCacheSizeGb : Math.Clamp(value, MinMaxCacheSizeGb, MaxMaxCacheSizeGb);

    [JsonIgnore]
    public int EffectiveMaxCacheSizeGb => ClampMaxCacheSizeGb(MaxCacheSizeGb);

    [JsonIgnore]
    public long MaxCacheSizeBytes => (long)EffectiveMaxCacheSizeGb * 1024L * 1024L * 1024L;

    [JsonIgnore]
    public string MesaMaxCacheSize => $"{EffectiveMaxCacheSizeGb}G";

    public bool IsAutoRecordDisabledForGame(string gameId) =>
        DisabledAutoRecordGameIds != null && DisabledAutoRecordGameIds.Contains(gameId, StringComparer.Ordinal);
}
