using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Games;

/// <summary>Watches running processes for known games.
/// When a running game exits and no other game is running, imports any newly recorded pipelines and refreshes UI state,
/// and optionally triggers an incremental compile when AutoCompileOnExit is enabled in Settings (default: OFF).</summary>
public sealed class GameExitMonitor : IDisposable
{
    private readonly Func<IReadOnlyList<GameInfo>> _getKnownGames;
    private readonly Func<GameInfo, CancellationToken, Task> _onGameReadyToCompile;
    private readonly Func<GameInfo, CancellationToken, Task>? _onGameExited;
    private readonly Func<GameInfo, bool> _isGameRunning;
    private readonly HashSet<string> _runningGameIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public GameExitMonitor(
        Func<IReadOnlyList<GameInfo>> getKnownGames,
        Func<GameInfo, CancellationToken, Task> onGameReadyToCompile,
        Func<GameInfo, bool>? isGameRunning = null,
        Func<GameInfo, CancellationToken, Task>? onGameExited = null)
    {
        _getKnownGames = getKnownGames;
        _onGameReadyToCompile = onGameReadyToCompile;
        _isGameRunning = isGameRunning ?? (g => ProtonRunner.IsGameRunning(g));
        _onGameExited = onGameExited;
    }

    public void Start(TimeSpan? pollInterval = null, TimeSpan? settleDelay = null)
    {
        if (_loopTask != null) return;
        var interval = pollInterval ?? TimeSpan.FromSeconds(3);
        var settle = settleDelay ?? TimeSpan.FromSeconds(3);
        _loopTask = Task.Run(() => MonitorLoopAsync(interval, settle, _cts.Token));
    }

    /// <summary>Updates <paramref name="runningGameIds"/> with the current running state of <paramref name="games"/>
    /// and returns the list of games that just transitioned from running to exited while no other game remains running.</summary>
    public static IReadOnlyList<GameInfo> DetectExitedGames(
        IReadOnlyList<GameInfo> games,
        ISet<string> runningGameIds,
        Func<GameInfo, bool>? isRunningPredicate = null)
    {
        var checkRunning = isRunningPredicate ?? (g => ProtonRunner.IsGameRunning(g));
        var exited = new List<GameInfo>();
        bool anyStillRunning = false;

        foreach (var game in games)
        {
            bool runningNow = false;
            try { runningNow = checkRunning(game); } catch { }

            if (runningNow)
            {
                runningGameIds.Add(game.Id);
                anyStillRunning = true;
            }
            else if (runningGameIds.Remove(game.Id))
            {
                exited.Add(game);
            }
        }

        // Never start a background compile while another game is still running
        return anyStillRunning ? Array.Empty<GameInfo>() : exited;
    }

    private async Task MonitorLoopAsync(TimeSpan pollInterval, TimeSpan settleDelay, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, ct);

                var games = _getKnownGames();
                if (games == null || games.Count == 0) continue;

                var exitedGames = DetectExitedGames(games, _runningGameIds, _isGameRunning);
                if (exitedGames.Count == 0) continue;

                // Wait for Wine / launcher wrapper processes to finish flushing shaderless.db and releasing cache locks
                await Task.Delay(settleDelay, ct);

                var latestGames = _getKnownGames();
                if (latestGames.Any(g => { try { return _isGameRunning(g); } catch { return false; } }))
                    continue;

                bool autoCompile = SettingsManager.Load().AutoCompileOnExit;

                foreach (var exited in exitedGames)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        if (_isGameRunning(exited)) continue;

                        try { ProtonRunner.FinalizeGameDriverCaches(exited); } catch { }

                        int newlyImported = 0;
                        try { newlyImported = RecorderManager.Import(exited); } catch { }

                        if (autoCompile)
                        {
                            int totalRecorded = RecorderManager.RecordedPsos(exited);
                            var warmedKeys = RecorderManager.LoadWarmedKeys(exited.Id);

                            bool needsCompile = newlyImported > 0
                                || exited.State != CacheState.Cached
                                || (totalRecorded > 0 && totalRecorded > warmedKeys.Count);

                            if (needsCompile)
                            {
                                await _onGameReadyToCompile(exited, ct);
                                continue;
                            }
                        }

                        if (_onGameExited != null)
                        {
                            await _onGameExited(exited, ct);
                        }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
