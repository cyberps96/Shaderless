using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Games;

public interface IGameSource
{
    GameSource SourceType { get; }
    Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default);

    async Task<SourceDiagnostic> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        var games = await DiscoverGamesAsync(cancellationToken);
        return new SourceDiagnostic(
            SourceType,
            Array.Empty<CheckedPathInfo>(),
            games,
            Array.Empty<SkippedItemInfo>());
    }
}

