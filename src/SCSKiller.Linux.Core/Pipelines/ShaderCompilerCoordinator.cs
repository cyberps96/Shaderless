using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Games;
using SCSKiller.Linux.Core.Models;
using System.Diagnostics;
using System.Threading.Channels;

namespace SCSKiller.Linux.Core.Pipelines;

public class ShaderCompilerCoordinator
{
    private readonly GameManager _gameManager;
    private readonly IDriverBackend _driverBackend;

    public ShaderCompilerCoordinator(GameManager gameManager, IDriverBackend driverBackend)
    {
        _gameManager = gameManager;
        _driverBackend = driverBackend;
    }

    public static SCSKiller.Core.IEngineReader CreateAllEngineReaders(string appDataDir) =>
        new SCSKiller.Core.Carved.EngineReaders(
            ("Unreal", new SCSKiller.Core.Unreal.UnrealReader(appDataDir)),
            (SCSKiller.Core.FromSoft.FromSoftReader.Family, new SCSKiller.Core.FromSoft.FromSoftReader(appDataDir)),
            (SCSKiller.Core.Unity.UnityReader.Family, new SCSKiller.Core.Unity.UnityReader()),
            (SCSKiller.Core.ReEngine.ReEngineReader.Family, new SCSKiller.Core.ReEngine.ReEngineReader(appDataDir)),
            (SCSKiller.Core.RedEngine.RedEngineReader.Family, new SCSKiller.Core.RedEngine.RedEngineReader()),
            (SCSKiller.Core.Dagor.DagorReader.Family, new SCSKiller.Core.Dagor.DagorReader()),
            (SCSKiller.Core.Northlight.NorthlightReader.Family, new SCSKiller.Core.Northlight.NorthlightReader()),
            (SCSKiller.Core.Carved.CarvedReader.Family, new SCSKiller.Core.Carved.CarvedReader())
        );

    public async IAsyncEnumerable<CompilationProgress> CompileGameShadersAsync(
        GameInfo game,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int estimatedTotal = game.TotalShadersEstimated > 0 ? game.TotalShadersEstimated : 5000;

        yield return new CompilationProgress(
            GameId: game.Id,
            GameName: game.Name,
            ProcessedShaders: 0,
            TotalShaders: estimatedTotal,
            Percentage: 0,
            CurrentAction: "Preparing shader extraction and D3D12/D3D11 pipeline environment...",
            IsComplete: false,
            Failed: false,
            ErrorMessage: null
        );

        await Task.Delay(50, cancellationToken);

        var antiCheat = RecorderManager.DetectAntiCheat(game);
        if (antiCheat != SCSKiller.Core.AntiCheat.None)
        {
            yield return new CompilationProgress(
                GameId: game.Id,
                GameName: game.Name,
                ProcessedShaders: 0,
                TotalShaders: estimatedTotal,
                Percentage: 1.0,
                CurrentAction: $"{antiCheat} detected: compiling outside the game, nothing is added to the game's folder",
                IsComplete: false,
                Failed: false,
                ErrorMessage: null
            );
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appDataDir = SCSKiller.Linux.Core.AppPaths.DataDir;
        string workingDir = Path.Combine(appDataDir, "work", game.Id);
        Directory.CreateDirectory(workingDir);

        // Resolve the real game / Shipping executable using SCSKiller.Core's GameFiles resolver
        string rawExe = !string.IsNullOrEmpty(game.ExecutablePath)
            ? game.ExecutablePath
            : (GameEngineDetector.FindExecutable(game.InstallPath) ?? "");
        if (!string.IsNullOrEmpty(rawExe) && Directory.Exists(game.InstallPath))
        {
            try
            {
                string resolvedExe = SCSKiller.Core.Games.GameFiles.GameExe(game.InstallPath, rawExe);
                if (!string.IsNullOrEmpty(resolvedExe) && File.Exists(resolvedExe))
                {
                    game = game with { ExecutablePath = resolvedExe };
                }
            }
            catch { }
        }

        if (ProtonRunner.IsGameRunning(game))
        {
            string runningMsg = $"{game.Name} is currently running. Please close the game before compiling so its cache files aren't locked.";
            yield return new CompilationProgress(
                GameId: game.Id,
                GameName: game.Name,
                ProcessedShaders: 0,
                TotalShaders: estimatedTotal,
                Percentage: 0,
                CurrentAction: runningMsg,
                IsComplete: true,
                Failed: true,
                ErrorMessage: runningMsg
            );
            yield break;
        }

        var driverInfo = await _driverBackend.GetDriverInfoAsync(cancellationToken);
        var caps = driverInfo.Vendor == GpuVendor.Amd
            ? new SCSKiller.Core.VendorCaps(
                "amd-1",
                CacheKeyedByExeName: true,
                StateIndependentCache: false,
                CacheSizeConfigurable: false,
                PerStageCache: true,
                RtCacheGranularity: SCSKiller.Core.RtCacheGranularity.WholeObject)
            : new SCSKiller.Core.VendorCaps(
                "nvidia-1",
                CacheKeyedByExeName: true,
                StateIndependentCache: true,
                CacheSizeConfigurable: true,
                PerStageCache: true,
                RtCacheGranularity: SCSKiller.Core.RtCacheGranularity.Collection,
                PackageKeyed: true);

        RecorderManager.MigrateLegacyFiles(workingDir);
        RecorderManager.MigrateLegacyFiles(RecorderManager.GameDir(game.Id));
        RecorderManager.MigrateLegacyFiles(RecorderManager.ExeDir(game));

        string genDbPath = Path.Combine(workingDir, "shaderless_gen.db");
        string recDbPath = Path.Combine(workingDir, "shaderless.db");
        int threads = Math.Max(2, Environment.ProcessorCount);

        string currentDriverVersion = driverInfo.DriverVersion;
        string currentProtonStamp = ProtonRunner.GetProtonStamp(game);
        string currentGameBuildStamp = ProtonRunner.GetGameBuildStamp(game);
        string runtimeStamp = $"{currentDriverVersion}|{currentProtonStamp}";

        var (existingBytes, _, _) = await _driverBackend.GetGameCacheStatsAsync(game.Id, game.Source, game.PrefixPath);
        var (existingVerified, _) = ProtonRunner.VerifyCacheInstalled(game);
        bool cacheTruncated = driverInfo.Vendor == GpuVendor.Nvidia && ProtonRunner.IsNvidiaCacheTruncated(game);
        string? runtimeRebuildReason =
            (string.Equals(game.StaleReason, "driver updated", StringComparison.Ordinal) ||
             string.Equals(game.StaleReason, "Proton updated", StringComparison.Ordinal) ||
             string.Equals(game.StaleReason, "cache truncated", StringComparison.Ordinal))
                ? game.StaleReason
                : (RecorderManager.GetWarmedStampMismatchReason(game.Id, currentDriverVersion, currentProtonStamp)
                   ?? ProtonRunner.GetRuntimeCacheStampMismatchReason(game)
                   ?? (cacheTruncated ? "cache truncated" : null));

        bool runtimeStale = runtimeRebuildReason != null;
        bool canIncremental = existingBytes > 0 && existingVerified && !runtimeStale && ProtonRunner.IsRuntimeCacheStampValid(game);

        // Load or seed previously warmed keys BEFORE Planner.Build / Materialize overwrites workingDir/plan.bin & shaderless.db
        var existingWarmedKeys = canIncremental
            ? RecorderManager.LoadWarmedKeys(game.Id, runtimeStamp, workingDir)
            : new HashSet<string>(StringComparer.Ordinal);

        yield return new CompilationProgress(
            GameId: game.Id,
            GameName: game.Name,
            ProcessedShaders: 0,
            TotalShaders: estimatedTotal,
            Percentage: 2.0,
            CurrentAction: "Scanning game archives across all supported engines and building compilation plan...",
            IsComplete: false,
            Failed: false,
            ErrorMessage: null
        );

        string? extractError = null;
        int plannedItems = 0;

        if (Directory.Exists(game.InstallPath) &&
            !string.IsNullOrEmpty(game.ExecutablePath) &&
            File.Exists(game.ExecutablePath) &&
            game.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Clean any previous materialized DBs in workingDir so extraction/fallback reflects current install & recordings
            try { if (File.Exists(recDbPath)) File.Delete(recDbPath); } catch { }
            try { if (File.Exists(genDbPath)) File.Delete(genDbPath); } catch { }

            var scsStore = game.Source switch
            {
                GameSource.Steam => SCSKiller.Core.Store.Steam,
                GameSource.Heroic => SCSKiller.Core.Store.Epic,
                _ => SCSKiller.Core.Store.Manual
            };
            var scsGame = new SCSKiller.Core.Game(game.RawId, game.Name, scsStore, game.InstallPath, game.ExecutablePath);

            // Always import any finished recording session from the game folder first (works even for unsupported static engines)
            try { RecorderManager.Import(game); } catch { }

            string store = RecorderManager.RecordingPath(game.Id);
            if ((!File.Exists(store) || new FileInfo(store).Length == 0) && !string.Equals(game.Id, game.RawId, StringComparison.Ordinal))
            {
                string rawStore = RecorderManager.RecordingPath(game.RawId);
                if (File.Exists(rawStore) && new FileInfo(rawStore).Length > 0)
                    store = rawStore;
            }

            try
            {
                var reader = CreateAllEngineReaders(appDataDir);
                var engineInfo = reader.Detect(scsGame);

                if (engineInfo == null)
                {
                    extractError = "Could not detect a supported shader archive in this game directory.";
                }
                else if (engineInfo.Unsupported != null)
                {
                    extractError = $"Unsupported game archive: {engineInfo.Unsupported}";
                }
                else
                {
                    string packDir = Path.Combine(appDataDir, "packs");
                    string sharedPackDir = Path.Combine(appDataDir, "community", "packs", driverInfo.Vendor == GpuVendor.Amd ? "amd" : "nvidia");
                    var planner = new SCSKiller.Core.Planning.Planner(packDir, sharedPackDir);

                    SCSKiller.Core.Recording? recording = null;
                    var index = await Task.Run(() => reader.Index(scsGame, engineInfo, new Progress<string>(), cancellationToken), cancellationToken);

                    // As Windows PrepareRecording: SCSKiller's copy, with every shader it names by hash read back from the install or middleware DLLs
                    string rehydrated = Path.Combine(workingDir, "recording.db");
                    if (File.Exists(store) && new FileInfo(store).Length > 0)
                    {
                        await Task.Run(() =>
                        {
                            using (SCSKiller.Core.App.Recordings.Lock(store))
                                SCSKiller.Core.Planning.Rehydrate.Run(
                                    store,
                                    rehydrated,
                                    scsGame,
                                    engineInfo,
                                    reader,
                                    index,
                                    null,
                                    cancellationToken,
                                    want => SCSKiller.Core.Planning.Middleware.Blobs(scsGame, want));
                        }, cancellationToken);
                        recording = new SCSKiller.Core.Recording(rehydrated);
                    }

                    // vkd3d-proton keys its Vulkan pipelines on the whole PSO state (input layout, RT/DS formats, blend, raster):
                    // with the game's draws recorded, plan their exact layouts (ExactLayouts) as on a state-dependent cache.
                    bool hasDraws = recording != null && RecorderManager.HasDraws(recording.DbPath);
                    var planCaps = hasDraws ? caps with { StateIndependentCache = false } : caps;

                    var plan = await Task.Run(() => planner.Build(scsGame, engineInfo, index, recording, planCaps, workingDir, new Progress<string>(), cancellationToken, maximum: true), cancellationToken);
                    await Task.Run(() => planner.Materialize(plan, scsGame, engineInfo, reader, recording, workingDir, cancellationToken), cancellationToken);
                    plannedItems = (int)Math.Min(int.MaxValue, plan.Stats.Recorded + plan.Stats.Generated + plan.Stats.D3D11Shaders + plan.Stats.MiddlewareItems + plan.Stats.Variants);
                    if (plannedItems > 0)
                    {
                        estimatedTotal = plannedItems;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                extractError = ex.Message;
            }

            // Fallback: if static archive extraction is unsupported or unavailable (e.g. CryEngine / KCD2, custom DX12 engines,
            // or encrypted archives), compile directly from the game's self-contained recorded pipelines in recording.db!
            bool producedStaticDb = (File.Exists(genDbPath) && new FileInfo(genDbPath).Length > 0) ||
                                    (File.Exists(recDbPath) && new FileInfo(recDbPath).Length > 0);
            if (!producedStaticDb && File.Exists(store) && new FileInfo(store).Length > 0)
            {
                int standaloneRecorded = await Task.Run(
                    () => RecorderManager.MaterializeStandaloneRecording(store, scsGame, recDbPath, cancellationToken),
                    cancellationToken);
                if (standaloneRecorded > 0)
                {
                    try { if (File.Exists(genDbPath)) File.Delete(genDbPath); } catch { }
                    extractError = null;
                    plannedItems = standaloneRecorded;
                    estimatedTotal = standaloneRecorded;
                }
            }
        }

        bool hasDb = (File.Exists(genDbPath) && new FileInfo(genDbPath).Length > 0) ||
                     (File.Exists(recDbPath) && new FileInfo(recDbPath).Length > 0);

        if (!hasDb && extractError != null)
        {
            string friendlyError = antiCheat == SCSKiller.Core.AntiCheat.None
                ? $"Static shader archives are not directly readable for this engine ({extractError}). Enable ⏺ Record, play once (DX12) to capture pipelines, then click Compile."
                : $"Failed to extract game shaders: {extractError}";

            yield return new CompilationProgress(
                GameId: game.Id,
                GameName: game.Name,
                ProcessedShaders: 0,
                TotalShaders: estimatedTotal,
                Percentage: 0,
                CurrentAction: friendlyError,
                IsComplete: true,
                Failed: true,
                ErrorMessage: friendlyError
            );
            yield break;
        }

        // Save full shaderless.warmed snapshot before incremental DB filtering
        HashSet<string> allPlannedKeys = new(StringComparer.Ordinal);
        int unwarmedCount = estimatedTotal;
        int alreadyWarmedCount = 0;
        byte[]? fullWarmedSnapshot = null;

        if (hasDb)
        {
            fullWarmedSnapshot = RecorderManager.BuildWarmedSnapshot(new[] { recDbPath, genDbPath });
            var (allKeys, unwarmed, alreadyWarmed) = RecorderManager.FilterDatabasesToUnwarmed(
                recDbPath,
                genDbPath,
                existingWarmedKeys);
            allPlannedKeys = allKeys;
            unwarmedCount = unwarmed;
            alreadyWarmedCount = alreadyWarmed;
        }

        // Fast-path: if 100% of planned & recorded pipelines are already warmed in the verified on-disk cache
        if (canIncremental && alreadyWarmedCount > 0 && unwarmedCount == 0)
        {
            try
            {
                if (File.Exists(genDbPath))
                    File.Delete(genDbPath);
            }
            catch { }

            try { ProtonRunner.FinalizeGameDriverCaches(game); } catch { }

            RecorderManager.SaveWarmedKeys(game, allPlannedKeys, runtimeStamp, prebuiltWarmedSnapshot: fullWarmedSnapshot);
            _gameManager.SaveGameCompiled(
                game.Id,
                allPlannedKeys.Count,
                currentDriverVersion,
                currentProtonStamp,
                currentGameBuildStamp);

            string upToDateMsg = $"Cache ready: 0 new pipelines ({alreadyWarmedCount:N0} already compiled, {FormatBytes(existingBytes)} on disk, cache verified).";
            yield return new CompilationProgress(
                GameId: game.Id,
                GameName: game.Name,
                ProcessedShaders: alreadyWarmedCount,
                TotalShaders: alreadyWarmedCount,
                Percentage: 100.0,
                CurrentAction: upToDateMsg,
                IsComplete: true,
                Failed: false,
                ErrorMessage: null,
                FailedShaders: 0,
                CacheVerified: true
            );
            yield break;
        }

        if (unwarmedCount > 0)
            estimatedTotal = unwarmedCount;

        bool isIncrementalRun = alreadyWarmedCount > 0;
        string? rebuildPrefix = !isIncrementalRun && !string.IsNullOrEmpty(runtimeRebuildReason)
            ? (string.Equals(runtimeRebuildReason, "driver updated", StringComparison.OrdinalIgnoreCase)
                ? "Driver updated"
                : string.Equals(runtimeRebuildReason, "cache truncated", StringComparison.OrdinalIgnoreCase)
                    ? "Legacy >2 GB cache truncated"
                    : "Proton updated")
            : null;
        bool hasRecordedDb = File.Exists(recDbPath) && new FileInfo(recDbPath).Length > 0;
        string recordedRebuildSuffix = hasRecordedDb ? " + recorded pipelines" : "";
        string warmHeader = isIncrementalRun
            ? $"Warming {unwarmedCount:N0} new pipelines (skipping {alreadyWarmedCount:N0} already compiled) across {threads} CPU threads..."
            : (rebuildPrefix != null
                ? $"{rebuildPrefix} — rebuilding full cache{recordedRebuildSuffix} across {threads} CPU threads..."
                : $"Warming GPU driver pipeline cache via Proton across {threads} CPU threads...");

        // Run Proton-powered D3D12/DXR/D3D11 warmer
        yield return new CompilationProgress(
            GameId: game.Id,
            GameName: game.Name,
            ProcessedShaders: 0,
            TotalShaders: estimatedTotal,
            Percentage: 5.0,
            CurrentAction: warmHeader,
            IsComplete: false,
            Failed: false,
            ErrorMessage: null
        );

        var channel = Channel.CreateUnbounded<WarmerEvent>();
        var warmerTask = Task.Run(async () =>
        {
            try
            {
                bool ok = await ProtonRunner.RunWarmerAsync(
                    game,
                    workingDir,
                    ev => channel.Writer.TryWrite(ev),
                    cancellationToken,
                    isIncrementalRun);
                channel.Writer.Complete();
                return ok;
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
                return false;
            }
        }, cancellationToken);

        int processed = 0;
        int succeeded = 0;
        int currentTotal = estimatedTotal;
        int failed = 0;
        string? lastError = null;
        bool success = false;

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var ev))
                {
                    if (ev.Total.HasValue && ev.Total.Value > 0)
                        currentTotal = ev.Total.Value;
                    if (ev.Failed.HasValue)
                        failed = ev.Failed.Value;
                    if (ev.Done.HasValue)
                    {
                        if (ev.Event == "done")
                        {
                            // warmer.cpp "done" event reports doneCount (succeeded), whereas "progress" reports processed (doneCount + failCount)
                            succeeded = ev.Done.Value;
                            processed = succeeded + failed;
                        }
                        else
                        {
                            processed = ev.Done.Value;
                            succeeded = Math.Max(0, processed - failed);
                        }
                    }
                    if (ev.Message != null)
                        lastError = ev.Message;

                    double pct = currentTotal > 0 ? Math.Min(99.0, (double)processed / currentTotal * 100.0) : 0;

                    string failSuffix = failed > 0 ? $", {failed:N0} failed" : "";
                    string pipeLabel = isIncrementalRun ? "new pipelines" : "pipelines";
                    string actionMsg = ev.Event switch
                    {
                        "init" => rebuildPrefix != null
                            ? $"{rebuildPrefix} — initializing D3D12/D3D11 device across {ev.Threads ?? threads} CPU threads..."
                            : $"Initializing D3D12/D3D11 device across {ev.Threads ?? threads} CPU threads...",
                        "start" => rebuildPrefix != null
                            ? $"{rebuildPrefix} — rebuilding full cache ({currentTotal:N0} pipelines)..."
                            : $"Starting compilation of {currentTotal:N0} {pipeLabel}...",
                        "progress" => rebuildPrefix != null
                            ? $"{rebuildPrefix} — rebuilding full cache ({processed:N0}/{currentTotal:N0}{failSuffix})..."
                            : $"Compiling {pipeLabel} into driver cache ({processed:N0}/{currentTotal:N0}{failSuffix})...",
                        "done" => $"Completed compilation: {succeeded:N0} compiled{failSuffix}.",
                        "error" => $"Warmer error: {ev.Message}",
                        _ => rebuildPrefix != null
                            ? $"{rebuildPrefix} — rebuilding full cache ({processed:N0}/{currentTotal:N0}{failSuffix})..."
                            : $"Compiling {pipeLabel} ({processed:N0}/{currentTotal:N0}{failSuffix})..."
                    };

                    yield return new CompilationProgress(
                        GameId: game.Id,
                        GameName: game.Name,
                        ProcessedShaders: processed,
                        TotalShaders: currentTotal,
                        Percentage: Math.Round(pct, 1),
                        CurrentAction: actionMsg,
                        IsComplete: false,
                        Failed: ev.Event == "error",
                        ErrorMessage: ev.Message,
                        FailedShaders: failed
                    );
                }
            }

            success = await warmerTask;
        }
        finally
        {
            bool completedAll = currentTotal > 0 && processed >= currentTotal;
            if (allPlannedKeys.Count > 0 && (success || completedAll))
            {
                RecorderManager.SaveWarmedKeys(
                    game,
                    allPlannedKeys,
                    runtimeStamp,
                    new[] { recDbPath, genDbPath },
                    fullWarmedSnapshot);
            }

            // Clean up large materialized shaderless_gen.db after warming finishes, matching Windows ScsKiller.cs
            try
            {
                if (File.Exists(genDbPath))
                    File.Delete(genDbPath);
            }
            catch { }
        }

        // Verify newly compiled cache on disk and in the folders the game reads
        var (cacheBytes, cacheFiles, cachePath) = await _driverBackend.GetGameCacheStatsAsync(game.Id, game.Source, game.PrefixPath);
        var (verifiedOnDisk, verifyDetail) = ProtonRunner.VerifyCacheInstalled(game);
        bool cacheVerified = verifiedOnDisk && cacheBytes > 0;

        int totalProcessed = processed > 0 ? processed : currentTotal;
        int compiledCount = succeeded > 0 ? succeeded : Math.Max(0, totalProcessed - failed);
        int totalCumulativeCompiled = isIncrementalRun && allPlannedKeys.Count > 0
            ? allPlannedKeys.Count - failed
            : (compiledCount > 0 ? compiledCount : totalProcessed);

        if (success || processed > 0)
        {
            _gameManager.SaveGameCompiled(
                game.Id,
                totalCumulativeCompiled,
                currentDriverVersion,
                currentProtonStamp,
                currentGameBuildStamp);
        }

        string finishMessage = FormatCompletionMessage(totalProcessed, failed, cacheBytes, cacheVerified, verifyDetail, alreadyWarmedCount);

        yield return new CompilationProgress(
            GameId: game.Id,
            GameName: game.Name,
            ProcessedShaders: currentTotal,
            TotalShaders: currentTotal,
            Percentage: 100.0,
            CurrentAction: finishMessage,
            IsComplete: true,
            Failed: !success && processed == 0,
            ErrorMessage: !success && processed == 0 ? (lastError ?? "Warmer process exited unexpectedly") : null,
            FailedShaders: failed,
            CacheVerified: cacheVerified
        );
    }

    public static string FormatCompletionMessage(int done, int failed, long cacheBytes, bool cacheVerified, string? verifyDetail = null, int previouslyCompiled = 0)
    {
        int compiled = Math.Max(0, done - failed);
        string counts = previouslyCompiled > 0
            ? (failed > 0
                ? $"{compiled:N0} new compiled, {failed:N0} failed ({previouslyCompiled + compiled:N0} total)"
                : $"{compiled:N0} new compiled ({previouslyCompiled + compiled:N0} total)")
            : (failed > 0
                ? $"{compiled:N0} compiled, {failed:N0} failed"
                : $"{compiled:N0} compiled");

        if (cacheVerified)
        {
            return cacheBytes > 0
                ? $"Cache ready: {counts} ({FormatBytes(cacheBytes)} on disk, cache verified)."
                : $"Cache ready: {counts} (cache verified).";
        }

        string reason = !string.IsNullOrWhiteSpace(verifyDetail) ? $": {verifyDetail}" : "";
        return $"Compiled ({counts}), but couldn't confirm cache files in game folder{reason}.";
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < suffixes.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.##} {suffixes[i]}";
    }
}

