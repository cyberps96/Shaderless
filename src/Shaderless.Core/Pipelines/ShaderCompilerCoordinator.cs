using Shaderless.Core.Drivers;
using Shaderless.Core.Games;
using Shaderless.Core.Models;
using System.Diagnostics;
using System.Threading.Channels;

namespace Shaderless.Core.Pipelines;

public class ShaderCompilerCoordinator
{
    private readonly GameManager _gameManager;
    private readonly IDriverBackend _driverBackend;

    public ShaderCompilerCoordinator(GameManager gameManager, IDriverBackend driverBackend)
    {
        _gameManager = gameManager;
        _driverBackend = driverBackend;
    }

    public static Shaderless.Engine.IEngineReader CreateAllEngineReaders(string appDataDir) =>
        new Shaderless.Engine.Carved.EngineReaders(
            ("Unreal", new Shaderless.Engine.Unreal.UnrealReader(appDataDir)),
            (Shaderless.Engine.FromSoft.FromSoftReader.Family, new Shaderless.Engine.FromSoft.FromSoftReader(appDataDir)),
            (Shaderless.Engine.Unity.UnityReader.Family, new Shaderless.Engine.Unity.UnityReader()),
            (Shaderless.Engine.ReEngine.ReEngineReader.Family, new Shaderless.Engine.ReEngine.ReEngineReader(appDataDir)),
            (Shaderless.Engine.RedEngine.RedEngineReader.Family, new Shaderless.Engine.RedEngine.RedEngineReader()),
            (Shaderless.Engine.Dagor.DagorReader.Family, new Shaderless.Engine.Dagor.DagorReader()),
            (Shaderless.Engine.Northlight.NorthlightReader.Family, new Shaderless.Engine.Northlight.NorthlightReader()),
            (Shaderless.Engine.Carved.CarvedReader.Family, new Shaderless.Engine.Carved.CarvedReader())
        );

    public async IAsyncEnumerable<CompilationProgress> CompileGameShadersAsync(
        GameInfo game,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
        bool recordedOnly = false)
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
        if (antiCheat != Shaderless.Engine.AntiCheat.None)
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

        string appDataDir = RecorderManager.DataDir;
        string workingDir = Path.Combine(appDataDir, "work", game.Id);
        Directory.CreateDirectory(workingDir);

        // Resolve the real game / Shipping executable using Shaderless.Engine's GameFiles resolver
        string rawExe = !string.IsNullOrEmpty(game.ExecutablePath)
            ? game.ExecutablePath
            : (GameEngineDetector.FindExecutable(game.InstallPath) ?? "");
        if (!string.IsNullOrEmpty(rawExe) && Directory.Exists(game.InstallPath))
        {
            try
            {
                string resolvedExe = Shaderless.Engine.Games.GameFiles.GameExe(game.InstallPath, rawExe);
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
        var caps = GetVendorCaps(driverInfo.Vendor);

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
             string.Equals(game.StaleReason, "launch options changed", StringComparison.Ordinal) ||
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

        string workRehydratedPath = Path.Combine(workingDir, "recording.db");
        string planBinPath = Path.Combine(workingDir, "plan.bin");

        string? resolvedExePath = GameEngineDetector.ResolveRealGameExecutable(game.InstallPath, game.ExecutablePath) ?? game.ExecutablePath;
        bool hasValidGameExe =
            Directory.Exists(game.InstallPath) &&
            !string.IsNullOrEmpty(resolvedExePath) &&
            File.Exists(resolvedExePath) &&
            resolvedExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        var scsStore = game.Source switch
        {
            GameSource.Steam => Shaderless.Engine.Store.Steam,
            GameSource.Heroic => Shaderless.Engine.Store.Epic,
            _ => Shaderless.Engine.Store.Manual
        };
        var scsGame = new Shaderless.Engine.Game(
            game.RawId,
            game.Name,
            scsStore,
            game.InstallPath ?? "",
            resolvedExePath ?? "");

        // Always import any finished recording session from the game folder first (works even for unsupported static engines)
        try { RecorderManager.Import(game); } catch { }

        string store = RecorderManager.RecordingPath(game.Id);
        if ((!File.Exists(store) || new FileInfo(store).Length == 0) && !string.Equals(game.Id, game.RawId, StringComparison.Ordinal))
        {
            string rawStore = RecorderManager.RecordingPath(game.RawId);
            if (File.Exists(rawStore) && new FileInfo(rawStore).Length > 0)
                store = rawStore;
        }
        bool hasStore = File.Exists(store) && new FileInfo(store).Length > 0;

        bool prevSavedRecordedOnly = _gameManager.IsSavedAsRecordedOnly(game.Id);
        string? prevCompileNote = RecorderManager.LoadCompileNote(game.Id);
        var prevPlanHeader = TryReadPlanHeader(planBinPath);
        bool prevWasFileOnlyPartial =
            (prevPlanHeader is { Stats.Recorded: 0 }) ||
            (!string.IsNullOrEmpty(prevCompileNote) && prevCompileNote.StartsWith("Partial:", StringComparison.Ordinal));

        bool canFastIncrementalDelta =
            !recordedOnly &&
            canIncremental &&
            existingWarmedKeys.Count > 0 &&
            !(prevWasFileOnlyPartial && hasStore) &&
            _gameManager.CanUseIncrementalRecordedDelta(
                game.Id,
                currentDriverVersion,
                currentProtonStamp,
                currentGameBuildStamp,
                Shaderless.Engine.Planning.Planner.Version);

        Func<(Shaderless.Engine.IEngineReader Reader, Shaderless.Engine.EngineInfo? Engine)>? lazyEngineReader = hasValidGameExe
            ? () =>
            {
                var r = CreateAllEngineReaders(appDataDir);
                return (r, r.Detect(scsGame));
            }
            : null;

        HashSet<string> keysToSave = new(StringComparer.Ordinal);
        int unwarmedCount = estimatedTotal;
        int newTargetCount = 0;
        int alreadyWarmedCount = 0;
        byte[]? prebuiltWarmedSnapshot = null;
        bool mergeWarmedSnapshot = false;
        bool saveAsRecordedOnly = false;
        bool usedRecordedDeltaPath = false;
        string? partialNote = null;

        if (recordedOnly)
        {
            usedRecordedDeltaPath = true;
            try { if (File.Exists(recDbPath)) File.Delete(recDbPath); } catch { }
            try { if (File.Exists(genDbPath)) File.Delete(genDbPath); } catch { }
            try { if (File.Exists(workRehydratedPath)) File.Delete(workRehydratedPath); } catch { }
            if (!canIncremental)
            {
                try { if (File.Exists(planBinPath)) File.Delete(planBinPath); } catch { }
            }

            if (!hasStore)
            {
                string noRecMsg = $"No recorded pipelines found yet for {game.Name}. Enable ● Record, play about 5 minutes (DirectX 12), then click Recorded Only.";
                yield return new CompilationProgress(
                    GameId: game.Id,
                    GameName: game.Name,
                    ProcessedShaders: 0,
                    TotalShaders: 0,
                    Percentage: 0,
                    CurrentAction: noRecMsg,
                    IsComplete: true,
                    Failed: true,
                    ErrorMessage: noRecMsg
                );
                yield break;
            }

            yield return new CompilationProgress(
                GameId: game.Id,
                GameName: game.Name,
                ProcessedShaders: 0,
                TotalShaders: estimatedTotal,
                Percentage: 2.0,
                CurrentAction: "Preparing recorded gameplay pipelines (Recorded Only — skipping static archive scan)...",
                IsComplete: false,
                Failed: false,
                ErrorMessage: null
            );

            var recResult = await Task.Run(
                () => RecorderManager.MaterializeRecordedPipelines(
                    store,
                    scsGame,
                    recDbPath,
                    existingWarmedKeys,
                    lazyEngineReader,
                    cancellationToken),
                cancellationToken);

            if (recResult.AllRecordedKeys.Count == 0)
            {
                string emptyRecMsg = "No replayable DirectX 12/11 pipelines were found in this game's recording yet. Play into gameplay with ● Record enabled, then try again.";
                yield return new CompilationProgress(
                    GameId: game.Id,
                    GameName: game.Name,
                    ProcessedShaders: 0,
                    TotalShaders: 0,
                    Percentage: 0,
                    CurrentAction: emptyRecMsg,
                    IsComplete: true,
                    Failed: true,
                    ErrorMessage: emptyRecMsg
                );
                yield break;
            }

            if (recResult.AlreadyWarmedCount == 0 && recResult.NewlyPlannedKeys.Count == 0)
            {
                string missingBlobsMsg = "Recorded pipelines are missing shader bytecode that could not be found in the current game install. Record a fresh session and try again.";
                yield return new CompilationProgress(
                    GameId: game.Id,
                    GameName: game.Name,
                    ProcessedShaders: 0,
                    TotalShaders: 0,
                    Percentage: 0,
                    CurrentAction: missingBlobsMsg,
                    IsComplete: true,
                    Failed: true,
                    ErrorMessage: missingBlobsMsg
                );
                yield break;
            }

            keysToSave = recResult.NewlyPlannedKeys;
            unwarmedCount = recResult.KeptExecutableCount;
            newTargetCount = recResult.NewlyPlannedKeys.Count;
            alreadyWarmedCount = canIncremental
                ? Math.Max(existingWarmedKeys.Count, recResult.AlreadyWarmedCount)
                : 0;
            prebuiltWarmedSnapshot = recResult.DeltaWarmedSnapshot;
            mergeWarmedSnapshot = canIncremental && existingWarmedKeys.Count > 0;
            saveAsRecordedOnly = !(canIncremental && !prevSavedRecordedOnly && existingWarmedKeys.Count > 0);
            partialNote = (canIncremental && !prevSavedRecordedOnly) ? prevCompileNote : null;
        }
        else if (canFastIncrementalDelta)
        {
            usedRecordedDeltaPath = true;
            try { if (File.Exists(recDbPath)) File.Delete(recDbPath); } catch { }
            try { if (File.Exists(genDbPath)) File.Delete(genDbPath); } catch { }
            try { if (File.Exists(workRehydratedPath)) File.Delete(workRehydratedPath); } catch { }

            yield return new CompilationProgress(
                GameId: game.Id,
                GameName: game.Name,
                ProcessedShaders: 0,
                TotalShaders: estimatedTotal,
                Percentage: 2.0,
                CurrentAction: "Checking newly recorded pipelines (game files unchanged — skipping full archive scan)...",
                IsComplete: false,
                Failed: false,
                ErrorMessage: null
            );

            if (!hasStore)
            {
                keysToSave = new HashSet<string>(StringComparer.Ordinal);
                unwarmedCount = 0;
                newTargetCount = 0;
                alreadyWarmedCount = existingWarmedKeys.Count;
                prebuiltWarmedSnapshot = null;
                mergeWarmedSnapshot = true;
                saveAsRecordedOnly = false;
                partialNote = prevCompileNote;
            }
            else
            {
                var recResult = await Task.Run(
                    () => RecorderManager.MaterializeRecordedPipelines(
                        store,
                        scsGame,
                        recDbPath,
                        existingWarmedKeys,
                        lazyEngineReader,
                        cancellationToken),
                    cancellationToken);

                keysToSave = recResult.NewlyPlannedKeys;
                unwarmedCount = recResult.KeptExecutableCount;
                newTargetCount = recResult.NewlyPlannedKeys.Count;
                alreadyWarmedCount = Math.Max(existingWarmedKeys.Count, recResult.AlreadyWarmedCount);
                prebuiltWarmedSnapshot = recResult.DeltaWarmedSnapshot;
                mergeWarmedSnapshot = true;
                saveAsRecordedOnly = false;
                partialNote = recResult.AllRecordedKeys.Count > 0 ? null : prevCompileNote;
            }
        }
        else
        {
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
            Shaderless.Engine.PlanCheck? planCheck = null;   // what upstream's Planner.Check says about this plan (partial without a recording?)
            Shaderless.Engine.Plan? builtPlan = null;
            bool hasCompressedCompanions = false;

            if (hasValidGameExe)
            {
                // Clean any previous materialized DBs in workingDir so extraction/fallback reflects current install & recordings
                try { if (File.Exists(recDbPath)) File.Delete(recDbPath); } catch { }
                try { if (File.Exists(genDbPath)) File.Delete(genDbPath); } catch { }
                try { if (File.Exists(workRehydratedPath)) File.Delete(workRehydratedPath); } catch { }

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
                        hasCompressedCompanions = HasCompressedCompanionShaderArchives(game.InstallPath, engineInfo);
                        string packDir = Path.Combine(appDataDir, "packs");
                        string sharedPackDir = Path.Combine(appDataDir, "community", "packs", driverInfo.Vendor == GpuVendor.Amd ? "amd" : "nvidia");
                        var planner = new Shaderless.Engine.Planning.Planner(packDir, sharedPackDir);

                        Shaderless.Engine.Recording? recording = null;
                        var index = await Task.Run(() => reader.Index(scsGame, engineInfo, new Progress<string>(), cancellationToken), cancellationToken);

                        // PrepareRecording: Shaderless's copy, with every shader it names by hash read back from the install or middleware DLLs
                        if (hasStore)
                        {
                            await Task.Run(() =>
                            {
                                using (Shaderless.Engine.App.Recordings.Lock(store))
                                    Shaderless.Engine.Planning.Rehydrate.Run(
                                        store,
                                        workRehydratedPath,
                                        scsGame,
                                        engineInfo,
                                        reader,
                                        index,
                                        null,
                                        cancellationToken,
                                        want => Shaderless.Engine.Planning.Middleware.Blobs(scsGame, want));
                            }, cancellationToken);
                            recording = new Shaderless.Engine.Recording(workRehydratedPath);
                        }

                        // vkd3d-proton keys its Vulkan pipelines on the whole PSO state (input layout, RT/DS formats, blend, raster):
                        // with the game's draws recorded, plan their exact layouts (ExactLayouts) as on a state-dependent cache.
                        bool hasDraws = recording != null && RecorderManager.HasDraws(recording.DbPath);
                        var planCaps = hasDraws ? caps with { StateIndependentCache = false } : caps;

                        var plan = await Task.Run(() => planner.Build(scsGame, engineInfo, index, recording, planCaps, workingDir, new Progress<string>(), cancellationToken, maximum: true), cancellationToken);
                        builtPlan = plan;
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
                if (!producedStaticDb && hasStore)
                {
                    int standaloneRecorded = await Task.Run(
                        () => RecorderManager.MaterializeStandaloneRecording(store, scsGame, recDbPath, cancellationToken),
                        cancellationToken);
                    if (standaloneRecorded > 0)
                    {
                        try { if (File.Exists(genDbPath)) File.Delete(genDbPath); } catch { }
                        try { if (File.Exists(planBinPath)) File.Delete(planBinPath); } catch { }
                        extractError = null;
                        planCheck = null;   // compiled exactly what was recorded: nothing guessed
                        builtPlan = null;
                        hasCompressedCompanions = false;
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
                string friendlyError = antiCheat != Shaderless.Engine.AntiCheat.None
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
                string nothingMsg = antiCheat != Shaderless.Engine.AntiCheat.None
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
            prebuiltWarmedSnapshot = RecorderManager.BuildWarmedSnapshot(new[] { recDbPath, genDbPath });
            var (allKeys, unwarmed, alreadyWarmed) = RecorderManager.FilterDatabasesToUnwarmed(
                recDbPath,
                genDbPath,
                existingWarmedKeys);
            keysToSave = allKeys;
            unwarmedCount = unwarmed;
            newTargetCount = unwarmed;
            alreadyWarmedCount = alreadyWarmed;
            mergeWarmedSnapshot = false;
            saveAsRecordedOnly = false;

            // A DX12 plan made without a (usable) recording is partly guessed or misses compressed companion archives: tell the user
            partialNote = (builtPlan != null || planCheck != null)
                ? PartialCompileNote(
                    planCheck,
                    antiCheat,
                    builtPlan?.Stats,
                    hasCompressedCompanions,
                    linuxVkd3dStateDependent: true)
                : null;
        }

        // Fast-path: if 100% of planned & recorded pipelines are already warmed in the verified on-disk cache
        if (canIncremental && alreadyWarmedCount > 0 && unwarmedCount == 0)
        {
            try { if (File.Exists(genDbPath)) File.Delete(genDbPath); } catch { }
            try { if (File.Exists(workRehydratedPath)) File.Delete(workRehydratedPath); } catch { }
            try { if (File.Exists(recDbPath)) File.Delete(recDbPath); } catch { }

            try { ProtonRunner.FinalizeGameDriverCaches(game); } catch { }

            if (keysToSave.Count > 0 || prebuiltWarmedSnapshot != null || !File.Exists(RecorderManager.WarmedKeysPath(game.Id)))
            {
                RecorderManager.SaveWarmedKeys(
                    game,
                    keysToSave.Count > 0 ? keysToSave : existingWarmedKeys,
                    runtimeStamp,
                    prebuiltWarmedSnapshot: prebuiltWarmedSnapshot,
                    mergeWithExistingWarmedSnapshot: mergeWarmedSnapshot);
            }

            RecorderManager.SaveCompileNote(game.Id, partialNote);
            int savedCompiledCount = Math.Max(alreadyWarmedCount, keysToSave.Count);
            _gameManager.SaveGameCompiled(
                game.Id,
                savedCompiledCount,
                currentDriverVersion,
                currentProtonStamp,
                currentGameBuildStamp,
                Shaderless.Engine.Planning.Planner.Version,
                saveAsRecordedOnly);

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
                    : string.Equals(runtimeRebuildReason, "launch options changed", StringComparison.OrdinalIgnoreCase)
                        ? "Launch options changed"
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
            : recordedOnly
                ? (rebuildPrefix != null
                    ? $"{rebuildPrefix} — rebuilding {estimatedTotal:N0} recorded pipelines across {threads} CPU threads..."
                    : preserveExistingCache
                        ? $"Warming {estimatedTotal:N0} recorded pipelines via Proton across {threads} CPU threads (Recorded Only, keeping existing {FormatBytes(existingBytes)} cache)..."
                        : $"Warming {estimatedTotal:N0} recorded pipelines via Proton across {threads} CPU threads (Recorded Only)...")
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
                    string pipeLabel = isIncrementalRun
                        ? "new pipelines"
                        : (recordedOnly ? "recorded pipelines" : "pipelines");
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
            if (keysToSave.Count > 0 && (success || completedAll))
            {
                RecorderManager.SaveWarmedKeys(
                    game,
                    keysToSave,
                    runtimeStamp,
                    new[] { recDbPath, genDbPath },
                    prebuiltWarmedSnapshot,
                    mergeWithExistingWarmedSnapshot: mergeWarmedSnapshot);
            }

            // Clean up large temporary materialized DBs after warming & key persistence finish
            try { if (File.Exists(genDbPath)) File.Delete(genDbPath); } catch { }
            try { if (File.Exists(workRehydratedPath)) File.Delete(workRehydratedPath); } catch { }
            if ((success || completedAll) && File.Exists(RecorderManager.WarmedKeysPath(game.Id)))
            {
                try { if (File.Exists(recDbPath)) File.Delete(recDbPath); } catch { }
            }
        }

        // Verify newly compiled cache on disk and in the folders the game reads
        var (cacheBytes, cacheFiles, cachePath) = await _driverBackend.GetGameCacheStatsAsync(game.Id, game.Source, game.PrefixPath);
        var (verifiedOnDisk, verifyDetail) = ProtonRunner.VerifyCacheInstalled(game);
        bool cacheVerified = verifiedOnDisk && cacheBytes > 0;

        int totalProcessed = processed > 0 ? processed : currentTotal;
        int compiledCount = succeeded > 0 ? succeeded : Math.Max(0, totalProcessed - failed);
        int totalCumulativeCompiled = isIncrementalRun
            ? (usedRecordedDeltaPath
                ? existingWarmedKeys.Count + Math.Max(0, (newTargetCount > 0 ? newTargetCount : compiledCount) - failed)
                : (keysToSave.Count > 0 ? keysToSave.Count - failed : (compiledCount > 0 ? compiledCount : totalProcessed)))
            : (compiledCount > 0 ? compiledCount : totalProcessed);

        if (success || processed > 0)
        {
            _gameManager.SaveGameCompiled(
                game.Id,
                totalCumulativeCompiled,
                currentDriverVersion,
                currentProtonStamp,
                currentGameBuildStamp,
                Shaderless.Engine.Planning.Planner.Version,
                saveAsRecordedOnly);
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

    public readonly record struct GameReadinessResult(bool NeedsRecordingFirst, string? ReadinessNote);

    private sealed record StaticReadinessEntry(
        string CacheKey,
        string? EngineFamily,
        string? EngineVersion,
        string? GraphicsApi,
        bool ConfirmedD3d12,
        bool Encrypted,
        string? Unsupported,
        Shaderless.Engine.AntiCheat AntiCheat,
        Shaderless.Engine.Readiness? StaticReadiness,
        string? StaticReason,
        bool HasCompressedCompanions);

    private static readonly object _readinessLock = new();
    private static string? _loadedReadinessDataDir;
    private static Dictionary<string, StaticReadinessEntry> _readinessCache = new(StringComparer.Ordinal);

    private static readonly HashSet<string> CompanionArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".forge", ".pak", ".psarc", ".ba2", ".archive", ".vfs", ".big", ".toc", ".bundle"
    };

    private static readonly HashSet<string> IgnoredScanSubdirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "pfx", ".wine", "shadercache", "__pycache__", "logs", "saved", "crashreportclient"
    };

    public static Shaderless.Engine.VendorCaps GetVendorCaps(GpuVendor vendor) =>
        vendor == GpuVendor.Amd
            ? new Shaderless.Engine.VendorCaps(
                "amd-1",
                CacheKeyedByExeName: true,
                StateIndependentCache: false,
                CacheSizeConfigurable: false,
                PerStageCache: true,
                RtCacheGranularity: Shaderless.Engine.RtCacheGranularity.WholeObject)
            : new Shaderless.Engine.VendorCaps(
                "nvidia-1",
                CacheKeyedByExeName: true,
                StateIndependentCache: true,
                CacheSizeConfigurable: true,
                PerStageCache: true,
                RtCacheGranularity: Shaderless.Engine.RtCacheGranularity.Collection,
                PackageKeyed: true);

    /// <summary>Reads the JSON <see cref="Shaderless.Engine.Plan"/> header from a <c>plan.bin</c> file without decompressing its Brotli body.</summary>
    public static Shaderless.Engine.Plan? TryReadPlanHeader(string planBinPath)
    {
        if (string.IsNullOrEmpty(planBinPath) || !File.Exists(planBinPath))
            return null;

        try
        {
            using var f = new FileStream(planBinPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length < 16) return null;

            Span<byte> head = stackalloc byte[16];
            f.ReadExactly(head);
            if (!head[..8].SequenceEqual("SCSKPLAN"u8) || BitConverter.ToInt32(head[8..12]) != 1)
                return null;

            int jsonLen = BitConverter.ToInt32(head[12..16]);
            if (jsonLen <= 0 || jsonLen > 16 * 1024 * 1024 || f.Length < 16L + jsonLen)
                return null;

            byte[] json = new byte[jsonLen];
            f.ReadExactly(json);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Shaderless.Engine.Plan>(json);
            return parsed != null ? parsed with { FilePath = planBinPath } : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Detects hybrid games where <see cref="Shaderless.Engine.Carved.CarvedReader"/> carved raw uncompressed global
    /// shaders (e.g. <c>ShaderDataDX12.bin</c> in Assassin's Creed Shadows), while additional DX12/shader-named archives
    /// (e.g. Oodle-compressed <c>DataPC_*_dx12.forge</c>) contain compressed shaders that cannot be carved statically.</summary>
    public static bool HasCompressedCompanionShaderArchives(string? installPath, Shaderless.Engine.EngineInfo? engineInfo)
    {
        if (engineInfo?.Family != Shaderless.Engine.Carved.CarvedReader.Family ||
            string.IsNullOrEmpty(installPath) ||
            !Directory.Exists(installPath))
        {
            return false;
        }

        try
        {
            foreach (var file in EnumerateFilesBoundedDepth(installPath, maxDepth: 3))
            {
                string ext = Path.GetExtension(file.Name);
                if (!CompanionArchiveExtensions.Contains(ext) || file.Length < 64 * 1024)
                    continue;

                string stem = Path.GetFileNameWithoutExtension(file.Name);
                bool looksLikeDx12OrShaderArchive =
                    stem.Contains("_dx12", StringComparison.OrdinalIgnoreCase) ||
                    stem.Contains("dx12_", StringComparison.OrdinalIgnoreCase) ||
                    stem.EndsWith("dx12", StringComparison.OrdinalIgnoreCase) ||
                    stem.Contains("d3d12", StringComparison.OrdinalIgnoreCase) ||
                    stem.Contains("shader", StringComparison.OrdinalIgnoreCase);

                if (!looksLikeDx12OrShaderArchive)
                    continue;

                if (!ContainsRawDxbcContainers(file.FullName))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static bool ContainsRawDxbcContainers(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            if (len < 48) return false;

            const int windowSize = 256 * 1024;
            long[] offsets = len <= windowSize
                ? [0L]
                : [0L, Math.Max(0L, (len - windowSize) / 2), Math.Max(0L, len - windowSize)];

            byte[] buf = new byte[Math.Min((int)Math.Min(len, windowSize), windowSize)];
            foreach (long off in offsets)
            {
                fs.Position = off;
                int read = fs.Read(buf, 0, (int)Math.Min(buf.Length, len - off));
                for (int i = 0; i <= read - 32; i++)
                {
                    if (buf[i] == (byte)'D' &&
                        buf[i + 1] == (byte)'X' &&
                        buf[i + 2] == (byte)'B' &&
                        buf[i + 3] == (byte)'C')
                    {
                        uint totalSize = BitConverter.ToUInt32(buf, i + 24);
                        ushort partCount = BitConverter.ToUInt16(buf, i + 28);
                        if (totalSize is >= 48 and <= (16u << 20) && partCount is >= 1 and <= 32)
                            return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static IEnumerable<FileInfo> EnumerateFilesBoundedDepth(string rootDir, int maxDepth)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((rootDir, 0));

        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            DirectoryInfo di;
            try { di = new DirectoryInfo(dir); }
            catch { continue; }

            FileInfo[] files;
            try { files = di.GetFiles(); }
            catch { continue; }

            foreach (var f in files)
                yield return f;

            if (depth >= maxDepth)
                continue;

            DirectoryInfo[] subdirs;
            try { subdirs = di.GetDirectories(); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                if (!IgnoredScanSubdirs.Contains(sub.Name))
                    stack.Push((sub.FullName, depth + 1));
            }
        }
    }

    /// <summary>The card note for a compile whose DX12 part was planned without a usable recording, from upstream's
    /// <see cref="Shaderless.Engine.Planning.Planner.Check"/>, <see cref="Shaderless.Engine.PlanStats"/>, hybrid compressed
    /// companion archive inspection, and Linux <c>vkd3d-proton</c> state-dependent PSO translation; null when nothing was
    /// guessed (compiled from a usable recording, or DirectX 11 only).</summary>
    public static string? PartialCompileNote(
        Shaderless.Engine.PlanCheck? check,
        Shaderless.Engine.AntiCheat antiCheat,
        Shaderless.Engine.PlanStats? stats = null,
        bool hasCompressedCompanionArchives = false,
        bool linuxVkd3dStateDependent = false)
    {
        if (check?.Readiness == Shaderless.Engine.Readiness.Unsupported)
            return null;

        // Pure DirectX 11 compile (e.g. A Short Hike): cached per-shader, no DX12 PSO recording needed
        if (stats != null &&
            stats.D3D11Shaders > 0 &&
            stats.Generated == 0 &&
            stats.SynthesizedTemplates == 0 &&
            stats.RtLibraries == 0 &&
            !hasCompressedCompanionArchives)
        {
            return null;
        }

        // Already compiled from a usable recording (e.g. Phantom Blade Zero, Valor Mortis, CONTROL Resonant)
        if (stats is { Recorded: > 0 } && check?.Readiness != Shaderless.Engine.Readiness.NeedsRecording)
        {
            return null;
        }

        // Hybrid game with raw global shaders + compressed companion shader archives (e.g. Assassin's Creed Shadows:
        // ShaderDataDX12.bin is raw DXIL+RTS0, while DataPC_*_dx12.forge holds Oodle-compressed world/material shaders)
        if ((stats == null || stats.Recorded == 0) && hasCompressedCompanionArchives)
        {
            return antiCheat != Shaderless.Engine.AntiCheat.None
                ? "Partial: compiled global shaders from files only (compressed shader archives need recording, which anti-cheat blocks)."
                : "Partial: compiled global shaders from files only — compressed shader archives still need recording. Turn on ● Record, play once, then Re-Compile.";
        }

        bool checkPartial = check != null && (
            check.Readiness == Shaderless.Engine.Readiness.NeedsRecording ||
            check.Reason.Contains(Shaderless.Engine.Planning.Planner.UntestedNote, StringComparison.Ordinal) ||
            check.Reason.Contains(Shaderless.Engine.Planning.Planner.Record, StringComparison.Ordinal));

        bool statsPartial = stats is { Recorded: 0 } && (
            Shaderless.Engine.App.ShaderEngine.IsPartial(stats) ||
            Shaderless.Engine.App.ShaderEngine.NeedsRtRecording(stats) ||
            (linuxVkd3dStateDependent && (stats.GuessedUnits > 0 || stats.SynthesizedTemplates > 0)));

        if (!checkPartial && !statsPartial)
            return null;

        return antiCheat != Shaderless.Engine.AntiCheat.None
            ? "Partial: compiled from the game's files only (anti-cheat blocks recording), so some stutter may remain."
            : "Partial: compiled from the game's files only. Turn on ● Record, play once, then Re-Compile to improve it.";
    }

    /// <summary>Retroactively infers <see cref="GameInfo.CompileNote"/> from <c>work/&lt;id&gt;/plan.bin</c> for games
    /// compiled before enhanced partial-compile note tracking was saved to <c>compile-note.txt</c>.</summary>
    public static string? InferRetroactiveCompileNote(GameInfo game, GpuVendor vendor)
    {
        try
        {
            // If all recorded PSOs are already warmed into the active cache, it is not a file-only compile
            if (game.RecordedPsos > 0 && game.NewRecordedPsos == 0)
                return null;

            string planBinPath = Path.Combine(RecorderManager.DataDir, "work", game.Id, "plan.bin");
            var plan = TryReadPlanHeader(planBinPath);
            if (plan == null)
                return null;

            var stats = plan.Stats;
            if (stats.Recorded > 0)
                return null;
            if (stats.Generated == 0 && stats.SynthesizedTemplates == 0 && stats.RtLibraries == 0)
                return null;

            bool hasCompressedCompanions = false;
            Shaderless.Engine.AntiCheat? cachedAntiCheat = null;
            if (game.Engine is GameEngine.DirectX12 or GameEngine.Unknown)
            {
                var staticEntry = GetOrComputeStaticReadiness(game, vendor);
                if (staticEntry != null)
                {
                    hasCompressedCompanions = staticEntry.HasCompressedCompanions;
                    cachedAntiCheat = staticEntry.AntiCheat;
                }
            }

            var antiCheat = cachedAntiCheat ?? RecorderManager.DetectAntiCheat(game);

            return PartialCompileNote(
                check: null,
                antiCheat: antiCheat,
                stats: stats,
                hasCompressedCompanionArchives: hasCompressedCompanions,
                linuxVkd3dStateDependent: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Evaluates upfront at scan time whether an uncompiled game requires a gameplay recording first before
    /// any shaders can be compiled (modeled after upstream <c>ShaderEngine.EvaluateGame</c> + <c>Planner.Check</c>).</summary>
    public static GameReadinessResult EvaluateReadiness(GameInfo game, GpuVendor vendor, int recordedPsos)
    {
        try
        {
            if (string.IsNullOrEmpty(game.InstallPath) || !Directory.Exists(game.InstallPath))
                return new(false, null);

            var entry = GetOrComputeStaticReadiness(game, vendor);
            if (entry == null)
                return new(false, null);

            string planBinPath = Path.Combine(RecorderManager.DataDir, "work", game.Id, "plan.bin");
            var lastPlan = TryReadPlanHeader(planBinPath);
            bool lastPlanProducedZeroFromFiles =
                lastPlan != null &&
                lastPlan.Stats.Recorded == 0 &&
                lastPlan.Stats.Generated == 0 &&
                lastPlan.Stats.D3D11Shaders == 0 &&
                lastPlan.Stats.MiddlewareItems == 0 &&
                (lastPlan.Stats.StageSets > 0 || lastPlan.Stats.RtLibraries > 0);

            bool apiMentionsD3d12 = entry.GraphicsApi?.Contains("D3D12", StringComparison.OrdinalIgnoreCase) == true;
            bool apiStartsWithD3d11 = entry.GraphicsApi?.StartsWith("D3D11", StringComparison.OrdinalIgnoreCase) == true;
            bool isD3d12Capable = entry.ConfirmedD3d12;

            bool isEncryptedOrAesMissing = entry.Encrypted ||
                (entry.Unsupported?.Contains("AES key", StringComparison.OrdinalIgnoreCase) == true);
            bool isCompressedOrPacked = entry.Unsupported?.Contains("compressed or packed", StringComparison.OrdinalIgnoreCase) == true;

            bool packedOrEncryptedD3d12 = isD3d12Capable && (isEncryptedOrAesMissing || isCompressedOrPacked);
            bool plannerNeedsRecording = entry.StaticReadiness == Shaderless.Engine.Readiness.NeedsRecording;
            bool staticNeedsRecording = plannerNeedsRecording || packedOrEncryptedD3d12 || lastPlanProducedZeroFromFiles;

            if (staticNeedsRecording)
            {
                if (recordedPsos <= 0)
                {
                    if (entry.AntiCheat != Shaderless.Engine.AntiCheat.None)
                    {
                        return new(true, $"Cannot pre-compile from files on this engine, and {entry.AntiCheat} blocks ● Record.");
                    }

                    if (isEncryptedOrAesMissing)
                    {
                        return new(true, "Needs recording first: game archives are encrypted (AES/DRM). Turn on ● Record, play ~5 min, then Compile.");
                    }

                    return new(true, "Needs recording first: shaders are packed or runtime-built on this engine. Turn on ● Record, play ~5 min, then Compile.");
                }

                // Recording exists: on AMD non-standalone plans, verify at least one graphics draw with a vertex shader was recorded
                if (vendor == GpuVendor.Amd && plannerNeedsRecording && !packedOrEncryptedD3d12 && !HasUsableDraws(game))
                {
                    return new(true, "Needs gameplay recording: menu-only session had no 3D draws yet. Play into the game world (~5 min), then Compile.");
                }

                return new(false, null);
            }

            // Can compile partially from static files right now (Keep Kill Stutter enabled, show helpful hint when unrecorded)
            if (recordedPsos == 0)
            {
                if (entry.HasCompressedCompanions)
                {
                    string hint = entry.AntiCheat != Shaderless.Engine.AntiCheat.None
                        ? "Compiles global shaders from files; compressed shader archives need recording (blocked by anti-cheat)."
                        : "Compiles global shaders now; compressed archives also need ● Record + play once for full coverage.";
                    return new(false, hint);
                }

                if (entry.StaticReadiness == Shaderless.Engine.Readiness.Ready &&
                    isD3d12Capable &&
                    !(apiStartsWithD3d11 && apiMentionsD3d12) &&
                    entry.StaticReason?.Contains(Shaderless.Engine.Planning.Planner.UntestedNote, StringComparison.Ordinal) == true)
                {
                    string hint = entry.AntiCheat != Shaderless.Engine.AntiCheat.None
                        ? "Engine version not verified yet (anti-cheat blocks recording): compiles from game files only."
                        : "Compiles static shaders now; ● Record + play once gives full DX12 pipeline coverage.";
                    return new(false, hint);
                }
            }

            return new(false, null);
        }
        catch
        {
            return new(false, null);
        }
    }

    private static bool HasUsableDraws(GameInfo game)
    {
        try
        {
            string store = RecorderManager.RecordingPath(game.Id);
            if (File.Exists(store) && new FileInfo(store).Length > 0 && RecorderManager.HasDraws(store))
                return true;

            if (!string.Equals(game.Id, game.RawId, StringComparison.Ordinal))
            {
                string rawStore = RecorderManager.RecordingPath(game.RawId);
                if (File.Exists(rawStore) && new FileInfo(rawStore).Length > 0 && RecorderManager.HasDraws(rawStore))
                    return true;
            }

            if (RecorderManager.ExeDir(game) is { } dir)
            {
                string inbox = Path.Combine(dir, "shaderless.db");
                if (File.Exists(inbox) && new FileInfo(inbox).Length > 0 && RecorderManager.HasDraws(inbox))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static StaticReadinessEntry? GetOrComputeStaticReadiness(GameInfo game, GpuVendor vendor)
    {
        if (string.IsNullOrEmpty(game.InstallPath) || !Directory.Exists(game.InstallPath))
            return null;

        string rawExe = !string.IsNullOrEmpty(game.ExecutablePath)
            ? game.ExecutablePath
            : (GameEngineDetector.FindExecutable(game.InstallPath) ?? "");
        if (string.IsNullOrEmpty(rawExe) || !File.Exists(rawExe))
            return null;

        // Skip synthetic dummy unit-test folders (e.g., 2-byte stub .exe with no real game archives or shader files)
        if (new FileInfo(rawExe).Length < 64 * 1024 && !HasAnyArchiveOrShaderFile(game.InstallPath))
            return null;

        string dataDir = RecorderManager.DataDir;
        string buildStamp = ProtonRunner.GetGameBuildStamp(game);
        string aesStamp = GetAesKeyStamp(dataDir, game.Id, game.RawId);
        string cacheKey = $"{game.Id}|{game.InstallPath}|{rawExe}|{buildStamp}|{aesStamp}|{vendor}|v3";

        lock (_readinessLock)
        {
            EnsureReadinessCacheLoadedNoLock(dataDir);
            if (_readinessCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        string? resolvedExe = GameEngineDetector.ResolveRealGameExecutable(game.InstallPath, rawExe) ?? rawExe;
        if (string.IsNullOrEmpty(resolvedExe) || !File.Exists(resolvedExe) || !resolvedExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var scsStore = game.Source switch
        {
            GameSource.Steam => Shaderless.Engine.Store.Steam,
            GameSource.Heroic => Shaderless.Engine.Store.Epic,
            _ => Shaderless.Engine.Store.Manual
        };
        var scsGame = new Shaderless.Engine.Game(game.RawId, game.Name, scsStore, game.InstallPath, resolvedExe);
        var antiCheat = RecorderManager.DetectAntiCheat(game);

        Shaderless.Engine.EngineInfo? engineInfo = null;
        Shaderless.Engine.PlanCheck? staticCheck = null;
        try
        {
            var reader = CreateAllEngineReaders(dataDir);
            engineInfo = reader.Detect(scsGame);
            if (engineInfo != null)
            {
                staticCheck = new Shaderless.Engine.Planning.Planner().Check(scsGame, engineInfo, null, GetVendorCaps(vendor));
            }
        }
        catch (Exception ex)
        {
            staticCheck = new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.Unsupported, ex.Message);
        }

        bool hasCompressedCompanions = HasCompressedCompanionShaderArchives(game.InstallPath, engineInfo);
        bool confirmedD3d12 = DetermineConfirmedD3d12(game, scsGame, engineInfo, antiCheat);
        var computed = new StaticReadinessEntry(
            CacheKey: cacheKey,
            EngineFamily: engineInfo?.Family,
            EngineVersion: engineInfo?.Version,
            GraphicsApi: engineInfo?.GraphicsApi,
            ConfirmedD3d12: confirmedD3d12,
            Encrypted: engineInfo?.Encrypted == true,
            Unsupported: engineInfo?.Unsupported,
            AntiCheat: antiCheat,
            StaticReadiness: staticCheck?.Readiness,
            StaticReason: staticCheck?.Reason,
            HasCompressedCompanions: hasCompressedCompanions);

        lock (_readinessLock)
        {
            EnsureReadinessCacheLoadedNoLock(dataDir);
            // Prune any older cache keys for the same game ID so scan-readiness.json stays compact across game updates
            string prefix = game.Id + "|";
            foreach (var oldKey in _readinessCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _readinessCache.Remove(oldKey);

            _readinessCache[cacheKey] = computed;
            SaveReadinessCacheNoLock(dataDir);
        }

        return computed;
    }

    private static bool DetermineConfirmedD3d12(
        GameInfo game,
        Shaderless.Engine.Game scsGame,
        Shaderless.Engine.EngineInfo? engineInfo,
        Shaderless.Engine.AntiCheat antiCheat)
    {
        string? api = engineInfo?.GraphicsApi;
        if (api != null)
        {
            if (api.StartsWith("D3D12", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!api.Contains("D3D12", StringComparison.OrdinalIgnoreCase))
                return false;

            // Known dedicated engine readers only include "D3D12" when the engine genuinely supports D3D12
            if (engineInfo?.Family != Shaderless.Engine.Carved.CarvedReader.Family)
                return true;

            // CarvedReader returns "D3D11 or D3D12" both when D3D11+D3D12 are both imported AND when neither was found (e.g. OpenGL).
            // Verify positive D3D12 evidence so OpenGL/2D games are never misclassified as needing D3D12 recording.
            if (game.Engine == GameEngine.DirectX12)
                return true;

            return HasPositiveD3d12BinaryEvidence(scsGame, antiCheat);
        }

        return game.Engine is GameEngine.DirectX12 or GameEngine.UnrealEngine5 or GameEngine.ReEngine or GameEngine.Northlight;
    }

    private static bool HasPositiveD3d12BinaryEvidence(Shaderless.Engine.Game scsGame, Shaderless.Engine.AntiCheat antiCheat)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            if (Directory.EnumerateFiles(scsGame.InstallDir, "D3D12Core.dll", opts).Any())
                return true;

            if (antiCheat != Shaderless.Engine.AntiCheat.None)
                return false;

            string? exeDir = Path.GetDirectoryName(scsGame.ExePath);
            if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir))
                return false;

            foreach (var bin in Directory.EnumerateFiles(exeDir, "*.dll").Prepend(scsGame.ExePath))
            {
                try
                {
                    var dlls = Shaderless.Engine.Carved.CarvedReader.PeImports(bin, out bool agility);
                    if (agility || dlls.Any(d => d.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    private static bool HasAnyArchiveOrShaderFile(string installPath)
    {
        try
        {
            foreach (var f in EnumerateFilesBoundedDepth(installPath, maxDepth: 3))
            {
                if (f.Length >= 4096)
                {
                    string ext = Path.GetExtension(f.Name);
                    if (CompanionArchiveExtensions.Contains(ext) ||
                        ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ucas", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ushaderbytecode", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".upipelinecache", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".bdt", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".dcx", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".rmdp", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".vromfs.bin", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.Contains(".pak.", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static string GetAesKeyStamp(string dataDir, string gameId, string rawId)
    {
        foreach (var id in new[] { gameId, rawId }.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var fi = new FileInfo(Path.Combine(dataDir, "games", id, "aes.key"));
                if (fi.Exists)
                    return $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
            }
            catch { }
        }
        return "none";
    }

    private static void EnsureReadinessCacheLoadedNoLock(string dataDir)
    {
        if (string.Equals(_loadedReadinessDataDir, dataDir, StringComparison.Ordinal))
            return;

        _loadedReadinessDataDir = dataDir;
        _readinessCache = new Dictionary<string, StaticReadinessEntry>(StringComparer.Ordinal);

        string path = Path.Combine(dataDir, "scan-readiness.json");
        if (!File.Exists(path))
            return;

        try
        {
            string json = File.ReadAllText(path);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, StaticReadinessEntry>>(json);
            if (loaded != null)
            {
                foreach (var kv in loaded)
                    _readinessCache[kv.Key] = kv.Value;
            }
        }
        catch { }
    }

    private static void SaveReadinessCacheNoLock(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            string path = Path.Combine(dataDir, "scan-readiness.json");
            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            string json = System.Text.Json.JsonSerializer.Serialize(
                _readinessCache,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
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

