using Photino.NET;
using Shaderless.Core.Drivers;
using Shaderless.Core.Games;
using Shaderless.Core.Models;
using Shaderless.Core.Pipelines;
using System.Text.Json;

namespace Shaderless.App;

class Program
{
    [System.Runtime.InteropServices.DllImport("libc.so.6", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    private static void SetNativeEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try { setenv(name, value, 1); } catch { }
    }

    private static void TrySetNativeClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var candidates = new List<(string Exe, string[] Args)>();
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            candidates.Add(("wl-copy", Array.Empty<string>()));
        }
        candidates.Add(("xclip", new[] { "-selection", "clipboard" }));
        candidates.Add(("xsel", new[] { "--clipboard", "--input" }));

        foreach (var (exe, args) in candidates)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) continue;
                proc.StandardInput.Write(text);
                proc.StandardInput.Close();
                proc.WaitForExit(250);
                return;
            }
            catch
            {
                // Try next clipboard utility
            }
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        Shaderless.Core.CrashLogger.RegisterGlobalHandlers("Shaderless.App");
        try
        {
            RunApp(args);
        }
        catch (Exception ex)
        {
            Shaderless.Core.CrashLogger.LogException(ex, "Shaderless.App Main Fatal");
            throw;
        }
    }

    private static void RunApp(string[] args)
    {
        // Fix GTK3 / WebKitGTK Wayland & DMA-BUF rendering issues on both NVIDIA and AMD (RADV)
        SetNativeEnv("GDK_BACKEND", "x11");
        SetNativeEnv("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        SetNativeEnv("WEBKIT_DISABLE_COMPOSITING_MODE", "1");

        // Keep installed Proton user_settings.py files up to date in the background on startup
        _ = Task.Run(() =>
        {
            try { ProtonRunner.RefreshInstalledProtonShaderCacheSettings(); } catch { }
        });

        IDriverBackend? driverBackend = null;
        GameManager gameManager = new GameManager();

        // Pre-warm library discovery & cache state immediately in parallel while GTK / WebKitGTK initializes the window
        Task<(IDriverBackend Backend, DriverInfo Driver, IReadOnlyList<GameInfo> Games)>? startupPrewarmTask = Task.Run(async () =>
        {
            var backend = await DriverDetector.DetectActiveBackendAsync();
            var drv = await backend.GetDriverInfoAsync();
            var gms = await gameManager.ScanAllGamesAsync(backend, preloadedDriverInfo: drv);
            return (backend, drv, gms);
        });

        var window = new PhotinoWindow()
            .SetTitle($"Shaderless v{Shaderless.Core.AppPaths.Version} — Linux Proton Shader Compiler")
            .SetUseOsDefaultSize(false)
            .SetMinSize(900, 600)
            .SetSize(1150, 750)
            .Center()
            .SetResizable(true);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "shaderless.png");
        if (File.Exists(iconPath)) window.SetIconFile(iconPath);

        bool clientAcknowledged = false;
        var stateLock = new SemaphoreSlim(1, 1);
        var stateCoalesceGate = new object();
        Task? inFlightStateTask = null;
        var compileLock = new SemaphoreSlim(1, 1);
        var sendLock = new object();
        IReadOnlyList<GameInfo> lastKnownGames = Array.Empty<GameInfo>();

        void SafeSendWebMessage(string json)
        {
            lock (sendLock)
            {
                window.SendWebMessage(json);
            }
        }

        async Task RunStateDispatchCoreAsync()
        {
            await stateLock.WaitAsync();
            try
            {
                DriverInfo driver;
                IReadOnlyList<GameInfo> games;
                var prewarm = Interlocked.Exchange(ref startupPrewarmTask, null);
                if (prewarm != null)
                {
                    var prewarmed = await prewarm;
                    driverBackend ??= prewarmed.Backend;
                    driver = prewarmed.Driver;
                    games = prewarmed.Games;
                }
                else
                {
                    if (driverBackend == null)
                    {
                        driverBackend = await DriverDetector.DetectActiveBackendAsync();
                    }

                    driver = await driverBackend.GetDriverInfoAsync();
                    games = await gameManager.ScanAllGamesAsync(driverBackend, preloadedDriverInfo: driver);
                }

                lastKnownGames = games;
                Shaderless.Core.CrashLogger.SetActiveDriverSummary(
                    $"{driver.DeviceName} • {driver.DriverName} (v{driver.DriverVersion})");
                var settings = Shaderless.Core.SettingsManager.Load();
                var crashLog = Shaderless.Core.CrashLogger.GetStatus();

                var payload = JsonSerializer.Serialize(new
                {
                    type = "state",
                    driver,
                    games,
                    settings,
                    crashLog
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                SafeSendWebMessage(payload);
            }
            catch (Exception ex)
            {
                Shaderless.Core.CrashLogger.LogException(ex, "Shaderless.App StateDispatch");
                Console.Error.WriteLine($"Error preparing state dispatch: {ex.Message}");
            }
            finally
            {
                stateLock.Release();
            }
        }

        async Task SendCurrentStateAsync(bool waitIfBusy = false)
        {
            if (waitIfBusy)
            {
                await RunStateDispatchCoreAsync();
                return;
            }

            Task taskToAwait;
            lock (stateCoalesceGate)
            {
                if (inFlightStateTask == null || inFlightStateTask.IsCompleted)
                {
                    inFlightStateTask = RunStateDispatchCoreAsync();
                }
                taskToAwait = inFlightStateTask;
            }
            await taskToAwait;
        }

        string? activeCompilingGameId = null;

        async Task RunGameCompilationAsync(GameInfo game, CancellationToken ct = default, bool recordedOnly = false)
        {
            if (!await compileLock.WaitAsync(0, ct))
                return;

            activeCompilingGameId = game.Id;
            try
            {
                if (driverBackend == null)
                    driverBackend = await DriverDetector.DetectActiveBackendAsync(ct);

                var coordinator = new ShaderCompilerCoordinator(gameManager, driverBackend);
                await foreach (var progress in coordinator.CompileGameShadersAsync(game, ct, recordedOnly))
                {
                    var pObj = new
                    {
                        type = "progress",
                        gameId = progress.GameId,
                        gameName = progress.GameName,
                        processedShaders = progress.ProcessedShaders,
                        totalShaders = progress.TotalShaders,
                        percentage = progress.Percentage,
                        currentAction = progress.CurrentAction,
                        isComplete = progress.IsComplete,
                        failed = progress.Failed,
                        errorMessage = progress.ErrorMessage,
                        failedShaders = progress.FailedShaders,
                        cacheVerified = progress.CacheVerified
                    };
                    var pJson = JsonSerializer.Serialize(pObj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    SafeSendWebMessage(pJson);
                }

                await SendCurrentStateAsync(waitIfBusy: true);
            }
            catch (OperationCanceledException)
            {
                // Cancelled cleanly
            }
            catch (Exception ex)
            {
                Shaderless.Core.CrashLogger.LogException(ex, $"CompileGame ({game.Name} [{game.Id}])");
                SafeSendWebMessage(JsonSerializer.Serialize(new
                {
                    type = "progress",
                    gameId = game.Id,
                    gameName = game.Name,
                    processedShaders = 0,
                    totalShaders = 0,
                    percentage = 0,
                    currentAction = $"Unexpected error during compile: {ex.Message} (logged to crash.log)",
                    isComplete = true,
                    failed = true,
                    errorMessage = ex.Message
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            }
            finally
            {
                activeCompilingGameId = null;
                compileLock.Release();
            }
        }

        using var exitMonitor = new GameExitMonitor(
            () => lastKnownGames,
            async (exitedGame, ct) =>
            {
                GameInfo target = exitedGame;
                try
                {
                    // Rescan (the exit monitor already imported the finished session) and push it to the window before the
                    // compile starts, so the card shows the new "Recorded Pipelines" count instead of the pre-launch one.
                    if (clientAcknowledged)
                        await SendCurrentStateAsync(waitIfBusy: true);
                    else if (driverBackend != null)
                        lastKnownGames = await gameManager.ScanAllGamesAsync(driverBackend);
                    target = lastKnownGames.FirstOrDefault(g => g.Id == exitedGame.Id) ?? exitedGame;
                }
                catch { }
                await RunGameCompilationAsync(target, ct, recordedOnly: gameManager.IsSavedAsRecordedOnly(target.Id));
            },
            onGameExited: async (_, _) =>
            {
                if (clientAcknowledged && compileLock.CurrentCount > 0)
                {
                    await SendCurrentStateAsync(waitIfBusy: true);
                }
            },
            onGamesStarted: (startedGames, _) =>
            {
                var startedIds = new HashSet<string>(startedGames.Select(g => g.Id), StringComparer.Ordinal);
                lastKnownGames = lastKnownGames
                    .Select(g => startedIds.Contains(g.Id) ? g with { IsRunning = true } : g)
                    .ToList();

                if (clientAcknowledged)
                {
                    var msgJson = JsonSerializer.Serialize(new
                    {
                        type = "running-changed",
                        gameIds = startedIds.ToArray(),
                        isRunning = true
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    SafeSendWebMessage(msgJson);
                }
                return Task.CompletedTask;
            });
        exitMonitor.Start();

        using var configWatchCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            string? lastProtonSig = null;
            try { lastProtonSig = ProtonRunner.GetLauncherProtonConfigSignature(); } catch { }

            while (!configWatchCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, configWatchCts.Token);
                    if (!clientAcknowledged || compileLock.CurrentCount == 0)
                        continue;

                    string currentSig = ProtonRunner.GetLauncherProtonConfigSignature();
                    if (lastProtonSig == null)
                    {
                        lastProtonSig = currentSig;
                        continue;
                    }

                    if (!string.Equals(lastProtonSig, currentSig, StringComparison.Ordinal))
                    {
                        lastProtonSig = currentSig;
                        await SendCurrentStateAsync(waitIfBusy: true);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch { }
            }
        });

        int updateCheckStarted = 0;

        window.RegisterWebMessageReceivedHandler((sender, message) =>
        {
            Task.Run(async () =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(message);
                    string? action = doc.RootElement.GetProperty("action").GetString();

                    if (action == "ack")
                    {
                        clientAcknowledged = true;
                        if (Interlocked.Exchange(ref updateCheckStarted, 1) == 0)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    string? dismissed = Shaderless.Core.SettingsManager.Load().DismissedUpdateTag;
                                    var update = await Shaderless.Core.UpdateChecker.CheckForUpdateAsync(dismissedTag: dismissed);
                                    if (update != null)
                                    {
                                        var updJson = JsonSerializer.Serialize(new
                                        {
                                            type = "update-available",
                                            update
                                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                                        SafeSendWebMessage(updJson);
                                    }
                                }
                                catch { }
                            });
                        }
                        return;
                    }

                    if (action == "dismiss-welcome")
                    {
                        Shaderless.Core.SettingsManager.MarkWelcomeSeen();
                        return;
                    }

                    if (action == "dismiss-update")
                    {
                        string? tag = doc.RootElement.TryGetProperty("tag", out var tagProp) ? tagProp.GetString() : null;
                        Shaderless.Core.SettingsManager.SetDismissedUpdateTag(tag);
                        return;
                    }

                    if (action == "open-data-dir")
                    {
                        try
                        {
                            string dataDir = Shaderless.Core.AppPaths.DataDir;
                            Directory.CreateDirectory(dataDir);
                            var psi = new System.Diagnostics.ProcessStartInfo("xdg-open")
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            psi.ArgumentList.Add(dataDir);
                            System.Diagnostics.Process.Start(psi);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Failed to open data directory: {ex.Message}");
                        }
                        return;
                    }

                    if (action == "copy-crash-log")
                    {
                        string? tail = Shaderless.Core.CrashLogger.ReadRecentTail();
                        bool hasLog = !string.IsNullOrEmpty(tail);
                        if (hasLog)
                        {
                            TrySetNativeClipboard(tail!);
                        }
                        SafeSendWebMessage(JsonSerializer.Serialize(new
                        {
                            type = "crash-log-copied",
                            hasLog,
                            text = tail ?? string.Empty,
                            path = Shaderless.Core.CrashLogger.CrashLogPath
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                        return;
                    }

                    if (action == "copy-text")
                    {
                        if (doc.RootElement.TryGetProperty("text", out var txtProp))
                        {
                            string? text = txtProp.GetString();
                            if (!string.IsNullOrEmpty(text) && text.Length <= 512)
                            {
                                TrySetNativeClipboard(text);
                            }
                        }
                        return;
                    }

                    if (action == "open-url")
                    {
                        if (doc.RootElement.TryGetProperty("url", out var urlProp))
                        {
                            string? url = urlProp.GetString();
                            if (!string.IsNullOrWhiteSpace(url) &&
                                Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                                uri.Scheme == Uri.UriSchemeHttps)
                            {
                                try
                                {
                                    var psi = new System.Diagnostics.ProcessStartInfo("xdg-open")
                                    {
                                        UseShellExecute = false,
                                        CreateNoWindow = true
                                    };
                                    psi.ArgumentList.Add(uri.AbsoluteUri);
                                    System.Diagnostics.Process.Start(psi);
                                }
                                catch (Exception ex)
                                {
                                    Console.Error.WriteLine($"Failed to open external URL: {ex.Message}");
                                }
                            }
                        }
                        return;
                    }

                    if (driverBackend == null)
                    {
                        driverBackend = await DriverDetector.DetectActiveBackendAsync();
                    }

                    if (action == "scan")
                    {
                        await SendCurrentStateAsync();
                    }
                    else if (action == "diagnose")
                    {
                        var report = await gameManager.DiagnoseAsync(driverBackend);
                        var diagJson = JsonSerializer.Serialize(new
                        {
                            type = "diagnostics",
                            report
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        SafeSendWebMessage(diagJson);
                    }
                    else if (action == "save-settings")
                    {
                        var current = Shaderless.Core.SettingsManager.Load();
                        bool autoRecord = doc.RootElement.TryGetProperty("autoRecordAllGames", out var arProp)
                            ? arProp.GetBoolean()
                            : current.AutoRecordAllGames;
                        bool autoCompile = doc.RootElement.TryGetProperty("autoCompileOnExit", out var acProp)
                            ? acProp.GetBoolean()
                            : current.AutoCompileOnExit;
                        int maxCacheGb = current.EffectiveMaxCacheSizeGb;
                        if (doc.RootElement.TryGetProperty("maxCacheSizeGb", out var mcProp))
                        {
                            if (mcProp.ValueKind == JsonValueKind.Number && mcProp.TryGetInt32(out int parsedGb))
                                maxCacheGb = AppSettings.ClampMaxCacheSizeGb(parsedGb);
                            else if (mcProp.ValueKind == JsonValueKind.String && int.TryParse(mcProp.GetString(), out int strGb))
                                maxCacheGb = AppSettings.ClampMaxCacheSizeGb(strGb);
                        }

                        // If the user explicitly turns AutoRecordAllGames on, clear previous opt-outs only if requested, or preserve per-game opt-outs
                        var updated = current with
                        {
                            AutoRecordAllGames = autoRecord,
                            AutoCompileOnExit = autoCompile,
                            MaxCacheSizeGb = maxCacheGb
                        };
                        Shaderless.Core.SettingsManager.Save(updated);
                        ProtonRunner.RefreshInstalledProtonShaderCacheSettings(updated);
                        await SendCurrentStateAsync(waitIfBusy: true);
                    }
                    else if (action == "compile")
                    {
                        string gameId = doc.RootElement.GetProperty("gameId").GetString()!;
                        bool recordedOnly = doc.RootElement.TryGetProperty("recordedOnly", out var roProp) && roProp.ValueKind == JsonValueKind.True;
                        var existing = lastKnownGames.FirstOrDefault(g => g.Id == gameId);
                        SafeSendWebMessage(JsonSerializer.Serialize(new
                        {
                            type = "progress", gameId, gameName = existing?.Name ?? "", processedShaders = 0, totalShaders = 0, percentage = 0,
                            currentAction = "Starting...", isComplete = false, failed = false, errorMessage = (string?)null
                        }));

                        GameInfo? game;
                        if (existing != null)
                        {
                            game = gameManager.RefreshSingleGameRecorderState(existing, driverBackend.Vendor);
                            lastKnownGames = lastKnownGames
                                .Select(g => g.Id == game.Id ? game : g)
                                .ToList();
                        }
                        else
                        {
                            var games = await gameManager.ScanAllGamesAsync(driverBackend);
                            lastKnownGames = games;
                            game = games.FirstOrDefault(g => g.Id == gameId);
                        }

                        if (game != null)
                        {
                            if (game.IsRunning)
                            {
                                SafeSendWebMessage(JsonSerializer.Serialize(new
                                {
                                    type = "progress",
                                    gameId = game.Id,
                                    gameName = game.Name,
                                    processedShaders = 0,
                                    totalShaders = 0,
                                    percentage = 0,
                                    currentAction = $"Close '{game.Name}' before compiling shaders.",
                                    isComplete = true,
                                    failed = true,
                                    errorMessage = $"'{game.Name}' is currently running."
                                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                                await SendCurrentStateAsync(waitIfBusy: true);
                            }
                            else
                            {
                                await RunGameCompilationAsync(game, recordedOnly: recordedOnly);
                            }
                        }
                    }
                    else if (action == "compile-all")
                    {
                        var games = await gameManager.ScanAllGamesAsync(driverBackend);
                        lastKnownGames = games;
                        var uncompiled = games
                            .Where(g => (g.State != CacheState.Cached || g.NewRecordedPsos > 0) && !g.NeedsRecordingFirst && !g.IsRunning)
                            .ToList();

                        foreach (var game in uncompiled)
                        {
                            await RunGameCompilationAsync(game);
                        }
                    }
                    else if (action == "clear-game")
                    {
                        string gameId = doc.RootElement.GetProperty("gameId").GetString()!;
                        var game = lastKnownGames.FirstOrDefault(g => g.Id == gameId);
                        if (game == null)
                        {
                            var games = await gameManager.ScanAllGamesAsync(driverBackend);
                            lastKnownGames = games;
                            game = games.FirstOrDefault(g => g.Id == gameId);
                        }
                        if (game != null)
                        {
                            await driverBackend.ClearGameCacheAsync(game.Id, game.Source, game.PrefixPath);
                        }
                        await SendCurrentStateAsync(waitIfBusy: true);
                    }
                    else if (action == "clear-cache" || action == "clear-all")
                    {
                        await driverBackend.ClearAllCachesAsync();
                        await SendCurrentStateAsync(waitIfBusy: true);
                    }
                    else if (action == "play")
                    {
                        string gameId = doc.RootElement.GetProperty("gameId").GetString()!;
                        var game = lastKnownGames.FirstOrDefault(g => g.Id == gameId);
                        if (game == null)
                        {
                            var games = await gameManager.ScanAllGamesAsync(driverBackend);
                            lastKnownGames = games;
                            game = games.FirstOrDefault(g => g.Id == gameId);
                        }

                        if (game == null)
                        {
                            SafeSendWebMessage(JsonSerializer.Serialize(new
                            {
                                type = "error",
                                gameId,
                                message = $"Game '{gameId}' was not found in installed libraries."
                            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                        }
                        else
                        {
                            // Refresh live running check right before launching
                            var freshGame = game with { IsRunning = ProtonRunner.IsGameRunning(game) };
                            bool isCompilingThis = string.Equals(activeCompilingGameId, freshGame.Id, StringComparison.Ordinal);
                            var launchResult = GameLauncher.Launch(freshGame, isCompiling: isCompilingThis);
                            if (!launchResult.Ok)
                            {
                                SafeSendWebMessage(JsonSerializer.Serialize(new
                                {
                                    type = "error",
                                    gameId = freshGame.Id,
                                    message = launchResult.Message
                                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                                if (freshGame.IsRunning)
                                {
                                    await SendCurrentStateAsync(waitIfBusy: true);
                                }
                            }
                            else
                            {
                                SafeSendWebMessage(JsonSerializer.Serialize(new
                                {
                                    type = "launched",
                                    gameId = freshGame.Id,
                                    gameName = freshGame.Name,
                                    message = launchResult.Message
                                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                            }
                        }
                    }
                    else if (action == "toggle-recorder")
                    {
                        string gameId = doc.RootElement.GetProperty("gameId").GetString()!;
                        var game = lastKnownGames.FirstOrDefault(g => g.Id == gameId);
                        if (game == null)
                        {
                            var games = await gameManager.ScanAllGamesAsync(driverBackend);
                            lastKnownGames = games;
                            game = games.FirstOrDefault(g => g.Id == gameId);
                        }
                        if (game != null)
                        {
                            string? error = null;
                            try
                            {
                                bool currentlyInstalled = RecorderManager.IsInstalled(game);
                                if (currentlyInstalled)
                                {
                                    RecorderManager.Uninstall(game);
                                    Shaderless.Core.SettingsManager.SetGameAutoRecordOptOut(game.Id, optOut: true);
                                }
                                else
                                {
                                    RecorderManager.Install(game);
                                    Shaderless.Core.SettingsManager.SetGameAutoRecordOptOut(game.Id, optOut: false);
                                }
                            }
                            catch (Exception ex) { error = ex.Message; }

                            var updatedGame = gameManager.RefreshSingleGameRecorderState(game, driverBackend.Vendor);
                            lastKnownGames = lastKnownGames
                                .Select(g => g.Id == updatedGame.Id ? updatedGame : g)
                                .ToList();

                            if (error != null)
                            {
                                SafeSendWebMessage(JsonSerializer.Serialize(
                                    new { type = "error", gameId, message = error },
                                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                            }

                            SafeSendWebMessage(JsonSerializer.Serialize(
                                new { type = "game-updated", game = updatedGame },
                                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Shaderless.Core.CrashLogger.LogException(ex, "Shaderless.App WebMessageHandler");
                    Console.Error.WriteLine($"Error handling UI message: {ex.Message}");
                }
            });
        });

        // Loop initial dispatch until UI acknowledges receipt
        Task.Run(async () =>
        {
            for (int i = 0; i < 5 && !clientAcknowledged; i++)
            {
                await Task.Delay(300 + (i * 200));
                if (!clientAcknowledged)
                {
                    await SendCurrentStateAsync();
                }
            }
        });

        window.LoadRawString(UiAssets.GetHtml());
        window.WaitForClose();
        configWatchCts.Cancel();
    }
}
