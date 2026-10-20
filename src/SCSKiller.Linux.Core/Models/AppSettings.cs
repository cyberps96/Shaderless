namespace SCSKiller.Linux.Core.Models;

/// <summary>Persistent user preferences stored in ~/.local/share/shaderless/settings.json.
/// All automation options default to false (OFF).</summary>
public record AppSettings(
    bool AutoRecordAllGames = false,
    bool AutoCompileOnExit = false,
    IReadOnlyList<string>? DisabledAutoRecordGameIds = null
)
{
    public bool IsAutoRecordDisabledForGame(string gameId) =>
        DisabledAutoRecordGameIds != null && DisabledAutoRecordGameIds.Contains(gameId, StringComparer.Ordinal);
}
