using Shaderless.Core.Drivers;
using Shaderless.Core.Models;
using System.Text.Json;

namespace Shaderless.Core.Games;

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

        string appData = RecorderManager.DataDir;
        Directory.CreateDirectory(appData);
        _stateFilePath = Path.Combine(appData, "games-cache-state.json");
    }

    public async Task<IReadOnlyList<GameInfo>> ScanAllGamesAsync(
        IDriverBackend? driverBackend = null,
        CancellationToken cancellationToken = default,
        DriverInfo? preloadedDriverInfo = null)
    {
        var tasks = _sources.Select(s => DiscoverSourceSafeAsync(s, cancellationToken));
        var results = await Task.WhenAll(tasks);

        var allGames = results.SelectMany(r => r).ToList();

        if (driverBackend == null)
        {
            driverBackend = await DriverDetector.DetectActiveBackendAsync(cancellationToken);
        }

        return await EnrichGamesWithCacheStateAsync(allGames, driverBackend, preloadedDriverInfo, cancellationToken);
    }

    public async Task<DiscoveryReport> DiagnoseAsync(IDriverBackend? driverBackend = null, CancellationToken cancellationToken = default)
    {
        if (driverBackend == null)
        {
            driverBackend = await DriverDetector.DetectActiveBackendAsync(cancellationToken);
        }

        var driverInfo = await driverBackend.GetDriverInfoAsync(cancellationToken);
        string activeDriverSummary = $"{driverInfo.DeviceName} • {driverInfo.DriverName} (v{driverInfo.DriverVersion})";
        CrashLogger.SetActiveDriverSummary(activeDriverSummary);
        var tasks = _sources.Select(s => DiagnoseSourceSafeAsync(s, cancellationToken));
        var rawDiagnostics = await Task.WhenAll(tasks);

        var enrichedDiagnostics = new List<SourceDiagnostic>();
        foreach (var diag in rawDiagnostics)
        {
            var enrichedGames = await EnrichGamesWithCacheStateAsync(diag.DiscoveredGames, driverBackend, driverInfo, cancellationToken);
            enrichedDiagnostics.Add(diag with { DiscoveredGames = enrichedGames });
        }

        var crashStatus = CrashLogger.GetStatus();
        return new DiscoveryReport(
            GeneratedAtUtc: DateTime.UtcNow,
            ActiveDriverSummary: activeDriverSummary,
            DefaultProtonBinary: ProtonRunner.FindProtonBinary(),
            Sources: enrichedDiagnostics,
            DataDirectory: crashStatus.DataDir,
            CrashLogPath: crashStatus.CrashLogPath,
            CrashLogExists: crashStatus.Exists,
            CrashLogSizeBytes: crashStatus.SizeBytes,
            CrashLogLastModifiedUtc: crashStatus.LastModifiedUtc
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

        string? protonOrLaunchDiff = ProtonRunner.CompareProtonStamps(savedProton, currentProton);
        if (protonOrLaunchDiff != null)
        {
            return protonOrLaunchDiff;
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

    private async Task<IReadOnlyList<GameInfo>> EnrichGamesWithCacheStateAsync(
        IReadOnlyList<GameInfo> games,
        IDriverBackend driverBackend,
        DriverInfo? preloadedDriverInfo = null,
        CancellationToken cancellationToken = default)
    {
        if (games.Count == 0)
            return Array.Empty<GameInfo>();

        var savedStates = LoadStates();
        bool statesModified = false;
        var stateSync = new object();
        var settings = Shaderless.Core.SettingsManager.Load();
        string? currentDriverVersion = preloadedDriverInfo?.DriverVersion;
        if (currentDriverVersion == null)
        {
            try
            {
                var dInfo = await driverBackend.GetDriverInfoAsync(cancellationToken);
                currentDriverVersion = dInfo.DriverVersion;
            }
            catch { }
        }

        // Capture /proc/*/cmdline once across the entire scan instead of scanning /proc once per game
        var runningExeNames = ProtonRunner.CaptureRunningExeNames();
        var updated = new GameInfo[games.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, games.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
                CancellationToken = cancellationToken
            },
            async (index, _) =>
            {
                var g = games[index];
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

                    GameStateRecord? rec;
                    lock (stateSync)
                    {
                        savedStates.TryGetValue(g.Id, out rec);
                    }

                    if (rec != null)
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
                            lock (stateSync)
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
                            lock (stateSync)
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
                }
                else
                {
                    state = CacheState.NeedsCompilation;
                    compiledCount = 0;
                    compiledAt = null;
                    lock (stateSync)
                    {
                        if (savedStates.Remove(g.Id))
                        {
                            statesModified = true;
                        }
                    }
                }

                bool recorder = false;
                int recorded = 0;
                int newRecorded = 0;
                bool isRunning = false;
                try
                {
                    try { isRunning = ProtonRunner.IsGameRunning(g, runningExeNames); } catch { }

                    recorder = RecorderManager.IsInstalled(g);
                    if (!isRunning && !recorder && settings.AutoRecordAllGames && !settings.IsAutoRecordDisabledForGame(g.Id))
                    {
                        recorder = RecorderManager.TryAutoInstall(g);
                    }
                    if (!isRunning)
                    {
                        RecorderManager.Import(g);   // a finished session's inbox (never imported while the game runs)
                    }
                    (recorded, newRecorded) = RecorderManager.GetRecordedAndUnwarmedCounts(g);
                }
                catch { }

                bool needsRecordingFirst = false;
                string? readinessNote = null;
                string? compileNote = null;

                if (state is CacheState.Cached or CacheState.Outdated)
                {
                    compileNote = RecorderManager.LoadCompileNote(g.Id)
                                  ?? Shaderless.Core.Pipelines.ShaderCompilerCoordinator.InferRetroactiveCompileNote(
                                      g with { RecordedPsos = recorded, NewRecordedPsos = newRecorded },
                                      driverBackend.Vendor);
                }
                else if (state == CacheState.NeedsCompilation)
                {
                    var readiness = Shaderless.Core.Pipelines.ShaderCompilerCoordinator.EvaluateReadiness(
                        g,
                        driverBackend.Vendor,
                        recorded);
                    needsRecordingFirst = readiness.NeedsRecordingFirst;
                    readinessNote = readiness.ReadinessNote;
                }

                updated[index] = g with
                {
                    State = state,
                    CompiledShadersCount = compiledCount,
                    LastCompiledAt = compiledAt,
                    CacheSizeBytes = cacheBytes,
                    CachedFilesCount = cacheFiles,
                    RecorderInstalled = recorder,
                    RecordedPsos = recorded,
                    StaleReason = staleReason,
                    NewRecordedPsos = newRecorded,
                    CompileNote = compileNote,
                    NeedsRecordingFirst = needsRecordingFirst,
                    ReadinessNote = readinessNote,
                    IsRunning = isRunning
                };
            });

        if (statesModified)
            SaveStates(savedStates);

        return updated;
    }

    /// <summary>
    /// Fast single-game refresh used after toggling Record mode so the UI updates in milliseconds
    /// without re-scanning all Steam/Heroic/Lutris libraries or re-reading unchanged game archives.
    /// </summary>
    public GameInfo RefreshSingleGameRecorderState(GameInfo g, GpuVendor vendor)
    {
        bool recorder = g.RecorderInstalled;
        int recorded = g.RecordedPsos;
        int newRecorded = g.NewRecordedPsos;
        bool isRunning = g.IsRunning;

        try
        {
            try { isRunning = ProtonRunner.IsGameRunning(g); } catch { }
            recorder = RecorderManager.IsInstalled(g);
            if (!isRunning)
            {
                RecorderManager.Import(g);
            }
            (recorded, newRecorded) = RecorderManager.GetRecordedAndUnwarmedCounts(g);
        }
        catch { }

        bool needsRecordingFirst = g.NeedsRecordingFirst;
        string? readinessNote = g.ReadinessNote;
        string? compileNote = g.CompileNote;

        if (recorded != g.RecordedPsos || newRecorded != g.NewRecordedPsos)
        {
            if (g.State is CacheState.Cached or CacheState.Outdated)
            {
                compileNote = RecorderManager.LoadCompileNote(g.Id)
                              ?? Shaderless.Core.Pipelines.ShaderCompilerCoordinator.InferRetroactiveCompileNote(
                                  g with { RecordedPsos = recorded, NewRecordedPsos = newRecorded },
                                  vendor);
            }
            else if (g.State == CacheState.NeedsCompilation)
            {
                var readiness = Shaderless.Core.Pipelines.ShaderCompilerCoordinator.EvaluateReadiness(
                    g,
                    vendor,
                    recorded);
                needsRecordingFirst = readiness.NeedsRecordingFirst;
                readinessNote = readiness.ReadinessNote;
            }
        }

        return g with
        {
            RecorderInstalled = recorder,
            RecordedPsos = recorded,
            NewRecordedPsos = newRecorded,
            CompileNote = compileNote,
            NeedsRecordingFirst = needsRecordingFirst,
            ReadinessNote = readinessNote,
            IsRunning = isRunning
        };
    }

    public void SaveGameCompiled(
        string gameId,
        int compiledCount,
        string? driverVersion = null,
        string? protonStamp = null,
        string? gameBuildStamp = null,
        int? plannerVersion = null,
        bool recordedOnly = false)
    {
        // Saved for audit history and stale detection
        var states = LoadStates();
        states[gameId] = new GameStateRecord(
            CacheState.Cached,
            compiledCount,
            DateTime.UtcNow,
            driverVersion,
            protonStamp,
            gameBuildStamp,
            plannerVersion,
            recordedOnly);
        SaveStates(states);
    }

    public bool IsSavedAsRecordedOnly(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return false;
        var states = LoadStates();
        return states.TryGetValue(gameId, out var rec) && rec.RecordedOnly;
    }

    /// <summary>
    /// Returns true when <paramref name="gameId"/> already has a full (non-RecordedOnly) compiled baseline whose
    /// GPU driver, Proton runtime, game build/archive timestamps, and planner version all match the current install,
    /// allowing incremental re-compiles to skip re-indexing static game archives and compile only new recorded PSOs.
    /// </summary>
    public bool CanUseIncrementalRecordedDelta(
        string gameId,
        string? currentDriverVersion,
        string? currentProtonStamp,
        string? currentGameBuildStamp,
        int currentPlannerVersion)
    {
        if (string.IsNullOrEmpty(gameId)) return false;
        var states = LoadStates();
        if (!states.TryGetValue(gameId, out var rec))
            return false;

        if (rec.RecordedOnly)
            return false;

        if (rec.PlannerVersion.HasValue && rec.PlannerVersion.Value != currentPlannerVersion)
            return false;

        string? staleReason = EvaluateStaleReason(
            rec.DriverVersion,
            rec.ProtonStamp,
            rec.GameBuildStamp,
            currentDriverVersion,
            currentProtonStamp,
            currentGameBuildStamp,
            rec.CompiledAt);

        return staleReason == null;
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
        string? GameBuildStamp = null,
        int? PlannerVersion = null,
        bool RecordedOnly = false);
}
