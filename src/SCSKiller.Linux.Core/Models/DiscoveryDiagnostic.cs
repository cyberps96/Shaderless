namespace SCSKiller.Linux.Core.Models;

public record CheckedPathInfo(
    string Path,
    string Description,
    bool Exists,
    bool IsFlatpak = false
);

public record SkippedItemInfo(
    string NameOrId,
    string Path,
    string Reason,
    bool IsFlatpak = false
);

public record SourceDiagnostic(
    GameSource Source,
    IReadOnlyList<CheckedPathInfo> CheckedPaths,
    IReadOnlyList<GameInfo> DiscoveredGames,
    IReadOnlyList<SkippedItemInfo> SkippedItems,
    string? Error = null
);

public record DiscoveryReport(
    DateTime GeneratedAtUtc,
    string ActiveDriverSummary,
    string? DefaultProtonBinary,
    IReadOnlyList<SourceDiagnostic> Sources
);
