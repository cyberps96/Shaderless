using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Models;
using System.Text.Json;

namespace SCSKiller.Linux.Core.Games;

public class GameManager
{
    private readonly List<IGameSource> _sources;
    private readonly string _stateFilePath;

    public GameManager(IEnumerable<IGameSource>? sources = null)
    {
        _sources = sources?.ToList() ?? new List<IGameSource>
        {
            new SteamLinuxSource(),
            new HeroicLinuxSource(),
            new LutrisLinuxSource()
        };

        string appData = SCSKiller.Linux.Core.AppPaths.DataDir;
        Directory.CreateDirectory(appData);
        _stateFilePath = Path.Combine(appData, "games-cache-state.json");
    }

    public async Task<IReadOnlyList<GameInfo>> ScanAllGamesAsync(IDriverBackend? driverBackend = null, CancellationToken cancellationToken = default)
    {
        var tasks = _sources.Select(s => DiscoverSourceSafeAsync(s, cancellationToken));
        var results = await Task.WhenAll(tasks);

        var allGames = results.SelectMany(r => r).ToList();

        if (driverBackend == null)
        {
            driverBackend = await DriverDetector.DetectActiveBackendAsync(cancellationToken);
        }

        return await EnrichGamesWithCacheStateAsync(allGames, driverBackend);
    }

    public async Task<DiscoveryReport> DiagnoseAsync(IDriverBackend? driverBackend = null, CancellationToken cancellationToken = default)
    {
        if (driverBackend == null)
        {
            driverBackend = await DriverDetector.DetectActiveBackendAsync(cancellationToken);
        }

        var driverInfo = await driverBackend.GetDriverInfoAsync(cancellationToken);
        var tasks = _sources.Select(s => DiagnoseSourceSafeAsync(s, cancellationToken));
        var rawDiagnostics = await Task.WhenAll(tasks);

        var enrichedDiagnostics = new List<SourceDiagnostic>();
        foreach (var diag in rawDiagnostics)
        {
            var enrichedGames = await EnrichGamesWithCacheStateAsync(diag.DiscoveredGames, driverBackend);
            enrichedDiagnostics.Add(diag with { DiscoveredGames = enrichedGames });
        }

        return new DiscoveryReport(
            GeneratedAtUtc: DateTime.UtcNow,
            ActiveDriverSummary: $"{driverInfo.DeviceName} • {driverInfo.DriverName} (v{driverInfo.DriverVersion})",
            DefaultProtonBinary: ProtonRunner.FindProtonBinary(),
            Sources: enrichedDiagnostics
        );
    }

    private static async Task<IReadOnlyList<GameInfo>> DiscoverSourceSafeAsync(IGameSource source, CancellationToken cancellationToken)
    {
        try
        {
            return await source.DiscoverGamesAsync(cancellationToken);
        }
        catch
        {
            // Isolate each launcher scan so one broken launcher never hides the others
            return Array.Empty<GameInfo>();
        }
    }

    private static async Task<SourceDiagnostic> DiagnoseSourceSafeAsync(IGameSource source, CancellationToken cancellationToken)
    {
        try
        {
            return await source.DiagnoseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return new SourceDiagnostic(
                Source: source.SourceType,
                CheckedPaths: Array.Empty<CheckedPathInfo>(),
                DiscoveredGames: Array.Empty<GameInfo>(),
                SkippedItems: Array.Empty<SkippedItemInfo>(),
                Error: ex.Message);
        }
    }

    public static IReadOnlyList<GameInfo> FilterGames(
        IEnumerable<GameInfo> games,
        GameSource? sourceFilter = null,
        string? searchQuery = null,
        bool onlyNeedsCompile = false)
    {
        if (games == null) return Array.Empty<GameInfo>();

        var query = searchQuery?.Trim();
        bool hasQuery = !string.IsNullOrEmpty(query);

        return games.Where(g =>
        {
            if (sourceFilter.HasValue && g.Source != sourceFilter.Value)
                return false;

            if (onlyNeedsCompile)
            {
                bool needsAction = g.State != CacheState.Cached || g.NewRecordedPsos > 0;
                if (!needsAction) return false;
            }

            if (hasQuery)
            {
                bool matchName = g.Name.Contains(query!, StringComparison.OrdinalIgnoreCase);
                bool matchId = g.Id.Contains(query!, StringComparison.OrdinalIgnoreCase);
                bool matchEngine = g.Engine.ToString().Contains(query!, StringComparison.OrdinalIgnoreCase);
                bool matchSource = g.Source.ToString().Contains(query!, StringComparison.OrdinalIgnoreCase)
                                   || (g.IsFlatpak && "flatpak".Contains(query!, StringComparison.OrdinalIgnoreCase));
                if (!matchName && !matchId && !matchEngine && !matchSource)
                    return false;
            }

            return true;
        }).ToList();
    }

    public static string? EvaluateStaleReason(
        string? savedDriver,
        string? savedProton,
        string? savedBuild,
        string? currentDriver,
        string? currentProton,
        string? currentBuild,
        DateTime? compiledAt = null)
    {
        if (!string.IsNullOrEmpty(savedDriver) &&
            !string.IsNullOrEmpty(currentDriver) &&
            !string.Equals(savedDriver, "Unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentDriver, "Unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(savedDriver, currentDriver, StringComparison.Ordinal))
        {
            return "driver updated";
        }

        if (!string.IsNullOrEmpty(savedProton) &&
            !string.IsNullOrEmpty(currentProton) &&
            !string.Equals(savedProton, "unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentProton, "unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(savedProton, currentProton, StringComparison.Ordinal))
        {
            return "Proton updated";
        }

        if (!string.IsNullOrEmpty(savedBuild) &&
            !string.IsNullOrEmpty(currentBuild) &&
            !string.Equals(savedBuild, "unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentBuild, "unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(savedBuild, currentBuild, StringComparison.Ordinal))
        {
            return "game updated";
        }

        if (compiledAt.HasValue)
        {
            var latestContentUtc = ProtonRunner.GetLatestGameContentWriteUtc(currentBuild);
            if (latestContentUtc.HasValue && latestContentUtc.Value > compiledAt.Value.ToUniversalTime().AddMinutes(2))
            {
                return "game updated";
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<GameInfo>> EnrichGamesWithCacheStateAsync(IReadOnlyList<GameInfo> games, IDriverBackend driverBackend)
    {
        var savedStates = LoadStates();
        bool statesModified = false;
        var settings = SCSKiller.Linux.Core.SettingsManager.Load();
        string? currentDriverVersion = null;
        try
        {
            var dInfo = await driverBackend.GetDriverInfoAsync();
            currentDriverVersion = dInfo.DriverVersion;
        }
        catch { }

        var updated = new List<GameInfo>(games.Count);
        foreach (var g in games)
        {
            var (cacheBytes, cacheFiles, _) = await driverBackend.GetGameCacheStatsAsync(g.Id, g.Source, g.PrefixPath);

            // PHYSICAL ON-DISK BYTES ARE THE ONLY SOURCE OF TRUTH.
            // If bytes are 0, it is NEVER cached, regardless of any state file.
            CacheState state;
            int compiledCount;
            DateTime? compiledAt;
            string? staleReason = null;

            if (cacheBytes > 0 && ProtonRunner.HasShaderlessCompiledArtifacts(g))
            {
                string currentProton = ProtonRunner.GetProtonStamp(g);
                string currentBuild = ProtonRunner.GetGameBuildStamp(g);

                if (savedStates.TryGetValue(g.Id, out var rec))
                {
                    compiledCount = rec.CompiledCount > 0 ? rec.CompiledCount : g.TotalShadersEstimated;
                    compiledAt = rec.CompiledAt;

                    staleReason = EvaluateStaleReason(
                        rec.DriverVersion,
                        rec.ProtonStamp,
                        rec.GameBuildStamp,
                        currentDriverVersion,
                        currentProton,
                        currentBuild,
                        rec.CompiledAt)
                        ?? RecorderManager.GetWarmedStampMismatchReason(g.Id, currentDriverVersion, currentProton)
                        ?? ProtonRunner.GetRuntimeCacheStampMismatchReason(g)
                        ?? (driverBackend.Vendor == GpuVendor.Nvidia && ProtonRunner.IsNvidiaCacheTruncated(g) ? "cache truncated" : null);

                    state = staleReason != null ? CacheState.Outdated : CacheState.Cached;

                    // Adopt missing stamp fields for caches compiled before stamp tracking was added (only when not already stale)
                    if (staleReason == null && (rec.DriverVersion == null || rec.ProtonStamp == null || rec.GameBuildStamp == null))
                    {
                        savedStates[g.Id] = rec with
                        {
                            DriverVersion = rec.DriverVersion ?? currentDriverVersion,
                            ProtonStamp = rec.ProtonStamp ?? currentProton,
                            GameBuildStamp = rec.GameBuildStamp ?? currentBuild
                        };
                        statesModified = true;
                    }
                }
                else
                {
                    staleReason = RecorderManager.GetWarmedStampMismatchReason(g.Id, currentDriverVersion, currentProton)
                               ?? ProtonRunner.GetRuntimeCacheStampMismatchReason(g)
                               ?? (driverBackend.Vendor == GpuVendor.Nvidia && ProtonRunner.IsNvidiaCacheTruncated(g) ? "cache truncated" : null);
                    state = staleReason != null ? CacheState.Outdated : CacheState.Cached;
                    compiledCount = g.TotalShadersEstimated;
                    compiledAt = DateTime.UtcNow;
                    if (staleReason == null)
                    {
                        savedStates[g.Id] = new GameStateRecord(
                            CacheState.Cached,
                            compiledCount,
                            compiledAt.Value,
                            currentDriverVersion,
                            currentProton,
                            currentBuild);
                        statesModified = true;
                    }
                }
            }
            else
            {
                state = CacheState.NeedsCompilation;
                compiledCount = 0;
                compiledAt = null;
                if (savedStates.Remove(g.Id))
                {
                    statesModified = true;
                }
            }

            bool recorder = false;
            int recorded = 0;
            int newRecorded = 0;
            try
            {
                recorder = RecorderManager.IsInstalled(g);
                if (!recorder && settings.AutoRecordAllGames && !settings.IsAutoRecordDisabledForGame(g.Id))
                {
                    recorder = RecorderManager.TryAutoInstall(g);
                }
                RecorderManager.Import(g);   // a finished session's inbox (skipped while the game holds it)
                recorded = RecorderManager.RecordedPsos(g);
                newRecorded = RecorderManager.UnwarmedRecordedPsos(g);
            }
            catch { }

            updated.Add(g with
            {
                State = state,
                CompiledShadersCount = compiledCount,
                LastCompiledAt = compiledAt,
                CacheSizeBytes = cacheBytes,
                CachedFilesCount = cacheFiles,
                RecorderInstalled = recorder,
                RecordedPsos = recorded,
                StaleReason = staleReason,
                NewRecordedPsos = newRecorded
            });
        }

        if (statesModified)
            SaveStates(savedStates);

        return updated;
    }

    public void SaveGameCompiled(
        string gameId,
        int compiledCount,
        string? driverVersion = null,
        string? protonStamp = null,
        string? gameBuildStamp = null)
    {
        // Saved for audit history and stale detection
        var states = LoadStates();
        states[gameId] = new GameStateRecord(
            CacheState.Cached,
            compiledCount,
            DateTime.UtcNow,
            driverVersion,
            protonStamp,
            gameBuildStamp);
        SaveStates(states);
    }

    private Dictionary<string, GameStateRecord> LoadStates()
    {
        if (!File.Exists(_stateFilePath)) return new Dictionary<string, GameStateRecord>();
        try
        {
            var json = File.ReadAllText(_stateFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, GameStateRecord>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void SaveStates(Dictionary<string, GameStateRecord> states)
    {
        try
        {
            var json = JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFilePath, json);
        }
        catch { }
    }

    public record GameStateRecord(
        CacheState State,
        int CompiledCount,
        DateTime CompiledAt,
        string? DriverVersion = null,
        string? ProtonStamp = null,
        string? GameBuildStamp = null);
}
