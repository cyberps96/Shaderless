using Photino.NET;
using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Games;
using SCSKiller.Linux.Core.Models;
using SCSKiller.Linux.Core.Pipelines;
using System.Text.Json;

namespace SCSKiller.Linux.App;

class Program
{
    [System.Runtime.InteropServices.DllImport("libc.so.6", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    private static void SetNativeEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try { setenv(name, value, 1); } catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Fix GTK3 / WebKitGTK Wayland & DMA-BUF rendering issues on both NVIDIA and AMD (RADV)
        SetNativeEnv("GDK_BACKEND", "x11");
        SetNativeEnv("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        SetNativeEnv("WEBKIT_DISABLE_COMPOSITING_MODE", "1");

        var window = new PhotinoWindow()
            .SetTitle($"Shaderless v{SCSKiller.Linux.Core.AppPaths.Version} — shader stutter killer for Linux (unofficial SCSKiller port)")
            .SetSize(1150, 750)
            .Center()
            .SetResizable(true);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "shaderless.png");
        if (File.Exists(iconPath)) window.SetIconFile(iconPath);

        IDriverBackend? driverBackend = null;
        GameManager gameManager = new GameManager();
        bool clientAcknowledged = false;
        var stateLock = new SemaphoreSlim(1, 1);
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

        async Task SendCurrentStateAsync(bool waitIfBusy = false)
        {
            if (waitIfBusy)
            {
                await stateLock.WaitAsync();
            }
            else if (!await stateLock.WaitAsync(0))
            {
                return;
            }
            try
            {
                if (driverBackend == null)
                {
                    driverBackend = await DriverDetector.DetectActiveBackendAsync();
                }

                var driver = await driverBackend.GetDriverInfoAsync();
                var games = await gameManager.ScanAllGamesAsync(driverBackend);
                lastKnownGames = games;
                var settings = SCSKiller.Linux.Core.SettingsManager.Load();

                var payload = JsonSerializer.Serialize(new
                {
                    type = "state",
                    driver,
                    games,
                    settings
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                SafeSendWebMessage(payload);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error preparing state dispatch: {ex.Message}");
            }
            finally
            {
                stateLock.Release();
            }
        }

        async Task RunGameCompilationAsync(GameInfo game, CancellationToken ct = default)
        {
            if (!await compileLock.WaitAsync(0, ct))
                return;

            try
            {
                if (driverBackend == null)
                    driverBackend = await DriverDetector.DetectActiveBackendAsync(ct);

                var coordinator = new ShaderCompilerCoordinator(gameManager, driverBackend);
                await foreach (var progress in coordinator.CompileGameShadersAsync(game, ct))
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
            finally
            {
                compileLock.Release();
            }
        }

        using var exitMonitor = new GameExitMonitor(
            () => lastKnownGames,
            async (exitedGame, ct) =>
            {
                await RunGameCompilationAsync(exitedGame, ct);
            });
        exitMonitor.Start();

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
                        var current = SCSKiller.Linux.Core.SettingsManager.Load();
                        bool autoRecord = doc.RootElement.TryGetProperty("autoRecordAllGames", out var arProp)
                            ? arProp.GetBoolean()
                            : current.AutoRecordAllGames;
                        bool autoCompile = doc.RootElement.TryGetProperty("autoCompileOnExit", out var acProp)
                            ? acProp.GetBoolean()
                            : current.AutoCompileOnExit;

                        // If the user explicitly turns AutoRecordAllGames on, clear previous opt-outs only if requested, or preserve per-game opt-outs
                        var updated = current with
                        {
                            AutoRecordAllGames = autoRecord,
                            AutoCompileOnExit = autoCompile
                        };
                        SCSKiller.Linux.Core.SettingsManager.Save(updated);
                        await SendCurrentStateAsync(waitIfBusy: true);
                    }
                    else if (action == "compile")
                    {
                        string gameId = doc.RootElement.GetProperty("gameId").GetString()!;
                        // the bar shows at once: the scan below reads every library first
                        SafeSendWebMessage(JsonSerializer.Serialize(new
                        {
                            type = "progress", gameId, gameName = "", processedShaders = 0, totalShaders = 0, percentage = 0,
                            currentAction = "Starting...", isComplete = false, failed = false, errorMessage = (string?)null
                        }));
                        var games = await gameManager.ScanAllGamesAsync(driverBackend);
                        lastKnownGames = games;
                        var game = games.FirstOrDefault(g => g.Id == gameId);

                        if (game != null)
                        {
                            await RunGameCompilationAsync(game);
                        }
                    }
                    else if (action == "compile-all")
                    {
                        var games = await gameManager.ScanAllGamesAsync(driverBackend);
                        lastKnownGames = games;
                        var uncompiled = games.Where(g => g.State != CacheState.Cached).ToList();

                        foreach (var game in uncompiled)
                        {
                            await RunGameCompilationAsync(game);
                        }
                    }
                    else if (action == "clear-game")
                    {
                        string gameId = doc.RootElement.GetProperty("gameId").GetString()!;
                        var games = await gameManager.ScanAllGamesAsync(driverBackend);
                        var game = games.FirstOrDefault(g => g.Id == gameId);
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
                    else if (action == "toggle-recorder")
                    {
                        string gameId = doc.RootElement.GetProperty("gameId").GetString()!;
                        var games = await gameManager.ScanAllGamesAsync(driverBackend);
                        var game = games.FirstOrDefault(g => g.Id == gameId);
                        if (game != null)
                        {
                            string? error = null;
                            try
                            {
                                if (game.RecorderInstalled)
                                {
                                    RecorderManager.Uninstall(game);
                                    SCSKiller.Linux.Core.SettingsManager.SetGameAutoRecordOptOut(game.Id, optOut: true);
                                }
                                else
                                {
                                    RecorderManager.Install(game);
                                    SCSKiller.Linux.Core.SettingsManager.SetGameAutoRecordOptOut(game.Id, optOut: false);
                                }
                            }
                            catch (Exception ex) { error = ex.Message; }
                            if (error != null)
                                SafeSendWebMessage(JsonSerializer.Serialize(new { type = "error", gameId, message = error }));
                        }
                        await SendCurrentStateAsync(waitIfBusy: true);
                    }
                }
                catch (Exception ex)
                {
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
    }
}
