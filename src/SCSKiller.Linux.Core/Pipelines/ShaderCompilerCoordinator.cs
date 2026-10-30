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
        SCSKiller.Core.PlanCheck? planCheck = null;   // what upstream's Planner.Check says about this plan (partial without a recording?)

        string? resolvedExePath = GameEngineDetector.ResolveRealGameExecutable(game.InstallPath, game.ExecutablePath) ?? game.ExecutablePath;
        if (Directory.Exists(game.InstallPath) &&
            !string.IsNullOrEmpty(resolvedExePath) &&
            File.Exists(resolvedExePath) &&
            resolvedExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
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
            var scsGame = new SCSKiller.Core.Game(game.RawId, game.Name, scsStore, game.InstallPath, resolvedExePath);

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
                    try { planCheck = planner.Check(scsGame, engineInfo, recording, planCaps); } catch { planCheck = null; }
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
                    planCheck = null;   // compiled exactly what was recorded: nothing guessed
                    plannedItems = standaloneRecorded;
                    estimatedTotal = standaloneRecorded;
                }
            }
        }

        bool hasDb = (File.Exists(genDbPath) && new FileInfo(genDbPath).Length > 0) ||
                     (File.Exists(recDbPath) && new FileInfo(recDbPath).Length > 0);

        if (!hasDb && extractError != null)
        {
            bool needsAesKey = extractError.Contains("needs the game's AES key", StringComparison.OrdinalIgnoreCase);
            string friendlyError = antiCheat != SCSKiller.Core.AntiCheat.None
                ? $"Failed to extract game shaders: {extractError}"
                : needsAesKey
                    ? $"Encrypted game archives (AES/DRM-protected). Enable ● Record, play once (DX12) to capture pipelines, then click Compile (or save its hex key to ~/.local/share/shaderless/games/{game.Id}/aes.key)."
                    : $"Static shader archives are not directly readable for this engine ({extractError}). Enable ● Record, play once (DX12) to capture pipelines, then click Compile.";

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

        // The game files were read but no pipeline could be planned from them (e.g. CONTROL Resonant: 1,000 shader sets,
        // all left out without a recording). Say so instead of running an empty warm and reporting the estimate as compiled.
        if (!hasDb)
        {
            string nothingMsg = antiCheat != SCSKiller.Core.AntiCheat.None
                ? "Nothing to compile from the game's files for this engine yet (its shaders can't be paired into pipelines without a recording, and anti-cheat blocks recording)."
                : "Nothing to compile from the game's files for this engine yet: its shaders can't be paired into pipelines without a recording. Turn on ● Record, play about 5 minutes (DirectX 12), then Compile.";
            RecorderManager.SaveCompileNote(game.Id, null);

            yield return new CompilationProgress(
                GameId: game.Id,
                GameName: game.Name,
                ProcessedShaders: 0,
                TotalShaders: 0,
                Percentage: 0,
                CurrentAction: nothingMsg,
                IsComplete: true,
                Failed: true,
                ErrorMessage: nothingMsg
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

        // A DX12 plan made without a (usable) recording is partly guessed: tell the user (Windows shows its "not tested" note)
        string? partialNote = PartialCompileNote(planCheck, antiCheat);

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
            RecorderManager.SaveCompileNote(game.Id, partialNote);
            _gameManager.SaveGameCompiled(
                game.Id,
                allPlannedKeys.Count,
                currentDriverVersion,
                currentProtonStamp,
                currentGameBuildStamp);

            string upToDateMsg = $"Cache ready: 0 new pipelines ({alreadyWarmedCount:N0} already compiled, {FormatBytes(existingBytes)} on disk, cache verified)."
                + (partialNote != null ? " " + partialNote : "");
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

        // A full compile for the same driver + Proton keeps what is already compiled (a compile from the game files still
        // helps the game after a recording replans it); only a driver / Proton change, or a cache near the size cap, starts clean.
        long maxCacheBytes = SettingsManager.Load().MaxCacheSizeBytes;
        bool preserveExistingCache = ShouldPreserveExistingCache(isIncrementalRun, runtimeRebuildReason, existingBytes, maxCacheBytes);

        string warmHeader = isIncrementalRun
            ? $"Warming {unwarmedCount:N0} new pipelines (skipping {alreadyWarmedCount:N0} already compiled) across {threads} CPU threads..."
            : (rebuildPrefix != null
                ? $"{rebuildPrefix} — rebuilding full cache{recordedRebuildSuffix} across {threads} CPU threads..."
                : preserveExistingCache
                    ? $"Warming GPU driver pipeline cache via Proton across {threads} CPU threads (keeping the existing {FormatBytes(existingBytes)} cache)..."
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
                    isIncrementalRun,
                    preserveExistingCache: preserveExistingCache);
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
            RecorderManager.SaveCompileNote(game.Id, partialNote);
        }

        string finishMessage = FormatCompletionMessage(totalProcessed, failed, cacheBytes, cacheVerified, verifyDetail, alreadyWarmedCount)
            + (partialNote != null && (success || processed > 0) ? " " + partialNote : "");

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

    /// <summary>Whether a full (non-incremental) compile keeps the cache already compiled for this same driver + Proton.
    /// Measured on Phantom Blade Zero: a compile from the game files alone (no recording) cut the first launch's shader
    /// phase from ~30 to ~8.5 minutes, then the compile planned from the recording deleted it. A runtime change (driver,
    /// Proton, a truncated legacy cache) still starts clean, and so does a cache already over 3/4 of the size cap.</summary>
    public static bool ShouldPreserveExistingCache(bool isIncrementalRun, string? runtimeRebuildReason, long existingBytes, long maxCacheBytes)
    {
        if (isIncrementalRun) return false;                 // an incremental compile keeps its cache anyway
        if (!string.IsNullOrEmpty(runtimeRebuildReason)) return false;
        if (existingBytes <= 0) return false;
        return maxCacheBytes <= 0 || existingBytes <= maxCacheBytes / 4 * 3;
    }

    /// <summary>The card note for a compile whose DX12 part was planned without a usable recording, from upstream's
    /// <see cref="SCSKiller.Core.Planning.Planner.Check"/> (the check behind the Windows app's "not tested on this engine
    /// version yet" note); null when nothing was guessed (recorded, a confirmed engine, DirectX 11 only).</summary>
    public static string? PartialCompileNote(SCSKiller.Core.PlanCheck? check, SCSKiller.Core.AntiCheat antiCheat)
    {
        if (check == null || check.Readiness == SCSKiller.Core.Readiness.Unsupported) return null;
        bool partial = check.Readiness == SCSKiller.Core.Readiness.NeedsRecording
            || check.Reason.Contains(SCSKiller.Core.Planning.Planner.UntestedNote, StringComparison.Ordinal)
            || check.Reason.Contains(SCSKiller.Core.Planning.Planner.Record, StringComparison.Ordinal);
        if (!partial) return null;
        return antiCheat != SCSKiller.Core.AntiCheat.None
            ? "Partial: compiled from the game's files only (anti-cheat blocks recording), so some stutter may remain."
            : "Partial: compiled from the game's files only. Turn on ● Record, play once, then Re-Compile to improve it.";
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

