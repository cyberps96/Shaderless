using Photino.NET;
using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Games;
using SCSKiller.Linux.Core.Models;
using SCSKiller.Linux.Core.Pipelines;
using System.Text.Json;

namespace SCSKiller.Linux.App;

class Program
{
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    private static void SetNativeEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try { setenv(name, value, 1); } catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Fix GTK3 / WebKitGTK Error 71 (Protocol error dispatching to Wayland display) on NVIDIA + Wayland
        SetNativeEnv("GDK_BACKEND", "x11");
        SetNativeEnv("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        SetNativeEnv("WEBKIT_DISABLE_COMPOSITING_MODE", "1");

        var window = new PhotinoWindow()
            .SetTitle("Shaderless — shader stutter killer for Linux (unofficial SCSKiller port)")
            .SetSize(1150, 750)
            .Center()
            .SetResizable(true);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "shaderless.png");
        if (File.Exists(iconPath)) window.SetIconFile(iconPath);

        IDriverBackend? driverBackend = null;
        GameManager gameManager = new GameManager();
        bool clientAcknowledged = false;

        async Task SendCurrentStateAsync()
        {
            try
            {
                if (driverBackend == null)
                {
                    driverBackend = await DriverDetector.DetectActiveBackendAsync();
                }

                var driver = await driverBackend.GetDriverInfoAsync();
                var games = await gameManager.ScanAllGamesAsync(driverBackend);

                var payload = JsonSerializer.Serialize(new
                {
                    type = "state",
                    driver,
                    games
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                window.SendWebMessage(payload);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error preparing state dispatch: {ex.Message}");
            }
        }

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
                    else if (action == "compile")
                    {
                        string gameId = doc.RootElement.GetProperty("gameId").GetString()!;
                        // the bar shows at once: the scan below reads every library first
                        window.SendWebMessage(JsonSerializer.Serialize(new
                        {
                            type = "progress", gameId, gameName = "", processedShaders = 0, totalShaders = 0, percentage = 0,
                            currentAction = "Starting...", isComplete = false, failed = false, errorMessage = (string?)null
                        }));
                        var games = await gameManager.ScanAllGamesAsync(driverBackend);
                        var game = games.FirstOrDefault(g => g.Id == gameId);

                        if (game != null)
                        {
                            var coordinator = new ShaderCompilerCoordinator(gameManager, driverBackend);
                            await foreach (var progress in coordinator.CompileGameShadersAsync(game))
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
                                    errorMessage = progress.ErrorMessage
                                };
                                var pJson = JsonSerializer.Serialize(pObj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                                window.SendWebMessage(pJson);
                            }

                            // Refresh driver cache stats
                            await SendCurrentStateAsync();
                        }
                    }
                    else if (action == "compile-all")
                    {
                        var games = await gameManager.ScanAllGamesAsync(driverBackend);
                        var uncompiled = games.Where(g => g.State != CacheState.Cached).ToList();

                        var coordinator = new ShaderCompilerCoordinator(gameManager, driverBackend);
                        foreach (var game in uncompiled)
                        {
                            await foreach (var progress in coordinator.CompileGameShadersAsync(game))
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
                                    errorMessage = progress.ErrorMessage
                                };
                                var pJson = JsonSerializer.Serialize(pObj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                                window.SendWebMessage(pJson);
                            }
                        }

                        await SendCurrentStateAsync();
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
                        await SendCurrentStateAsync();
                    }
                    else if (action == "clear-cache" || action == "clear-all")
                    {
                        await driverBackend.ClearAllCachesAsync();
                        await SendCurrentStateAsync();
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
                                if (game.RecorderInstalled) RecorderManager.Uninstall(game);
                                else RecorderManager.Install(game);
                            }
                            catch (Exception ex) { error = ex.Message; }
                            if (error != null)
                                window.SendWebMessage(JsonSerializer.Serialize(new { type = "error", gameId, message = error }));
                        }
                        await SendCurrentStateAsync();
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
