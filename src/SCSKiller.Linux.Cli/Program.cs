using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Games;
using SCSKiller.Linux.Core.Models;
using SCSKiller.Linux.Core.Pipelines;

namespace SCSKiller.Linux.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length > 0 && (args[0] is "--version" or "-v" or "version"))
        {
            Console.WriteLine($"Shaderless {SCSKiller.Linux.Core.AppPaths.Version}");
            return 0;
        }
        PrintHeader();

        var driverBackend = await DriverDetector.DetectActiveBackendAsync();
        var gameManager = new GameManager();

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        switch (command)
        {
            case "status":
            case "driver":
                return await ShowDriverStatusAsync(driverBackend);

            case "list":
            case "scan":
            case "games":
                return await ListGamesAsync(gameManager, driverBackend, args.Skip(1).ToArray());

            case "diagnose":
            case "diag":
                return await ShowDiagnosticsAsync(gameManager, driverBackend);

            case "settings":
                return HandleSettingsCommand(args);

            case "compile":
                if (args.Length < 2)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Usage: ./run-cli.sh compile <game-id-or-name>");
                    Console.ResetColor();
                    return 1;
                }
                return await CompileGameAsync(gameManager, driverBackend, args[1]);

            case "compile-all":
                return await CompileAllGamesAsync(gameManager, driverBackend);

            case "clear":
            case "clear-game":
                if (args.Length < 2)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Usage: ./run-cli.sh clear <game-id-or-name>");
                    Console.WriteLine("Example: ./run-cli.sh clear cyberpunk");
                    Console.ResetColor();
                    return 1;
                }
                return await ClearGameCacheAsync(gameManager, driverBackend, args[1]);

            case "clear-all":
                return await ClearAllCachesAsync(driverBackend);

            case "clear-cache":
                if (args.Length > 1)
                {
                    if (args[1].Equals("--all", StringComparison.OrdinalIgnoreCase) || args[1].Equals("-a", StringComparison.OrdinalIgnoreCase) || args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        return await ClearAllCachesAsync(driverBackend);
                    }
                    return await ClearGameCacheAsync(gameManager, driverBackend, args[1]);
                }
                return await ClearCacheInteractiveAsync(gameManager, driverBackend);

            case "help":
            default:
                PrintUsage();
                return 0;
        }
    }

    static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════╗
║      Shaderless — Shader Compilation Stutter Killer           ║
║        Kills shader stutter before you play (AMD/NVIDIA)      ║
╚═══════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  v{SCSKiller.Linux.Core.AppPaths.Version} (unofficial Linux port of SCSKiller)");
        Console.ResetColor();
    }

    static void PrintUsage()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  ./run-cli.sh status              Show GPU driver and shader cache stats");
        Console.WriteLine("  ./run-cli.sh list                List detected games across Steam, Heroic & Lutris (Native + Flatpak)");
        Console.WriteLine("  ./run-cli.sh diagnose            Inspect all checked library paths, discovered games, and skipped entries");
        Console.WriteLine("  ./run-cli.sh settings            Show or update automation & max cache size settings");
        Console.WriteLine("  ./run-cli.sh compile <id|name>   Pre-compile shaders for a specific game");
        Console.WriteLine("  ./run-cli.sh compile-all         Pre-compile shaders for all uncompiled games");
        Console.WriteLine("  ./run-cli.sh clear <id|name>     Purge shader cache for a specific game (e.g. cyberpunk)");
        Console.WriteLine("  ./run-cli.sh clear-all           Purge ALL GPU driver and game shader caches");
        Console.WriteLine("  ./run-cli.sh clear-cache         Interactive cache clearing menu");
        Console.WriteLine();
    }

    static int HandleSettingsCommand(string[] args)
    {
        var settings = SCSKiller.Linux.Core.SettingsManager.Load();
        bool modified = false;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--auto-record", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                bool val = args[++i] is "on" or "true" or "1";
                settings = settings with { AutoRecordAllGames = val };
                modified = true;
            }
            else if (args[i].Equals("--auto-compile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                bool val = args[++i] is "on" or "true" or "1";
                settings = settings with { AutoCompileOnExit = val };
                modified = true;
            }
            else if ((args[i].Equals("--max-cache-gb", StringComparison.OrdinalIgnoreCase) ||
                      args[i].Equals("--max-cache", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                string rawVal = args[++i].TrimEnd('g', 'G', 'b', 'B');
                if (int.TryParse(rawVal, out int gb))
                {
                    settings = settings with { MaxCacheSizeGb = AppSettings.ClampMaxCacheSizeGb(gb) };
                    modified = true;
                }
            }
        }

        if (modified)
        {
            SCSKiller.Linux.Core.SettingsManager.Save(settings);
            ProtonRunner.RefreshInstalledProtonShaderCacheSettings(settings);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Updated settings saved.\n");
            Console.ResetColor();
        }

        Console.WriteLine($"Settings file: {SCSKiller.Linux.Core.SettingsManager.DefaultSettingsFilePath}");
        Console.WriteLine($"  Auto-Record All Games:          {(settings.AutoRecordAllGames ? "ON" : "OFF")}");
        Console.WriteLine($"  Auto-Compile After Game Closes: {(settings.AutoCompileOnExit ? "ON" : "OFF")}");
        Console.WriteLine($"  Max Shader Cache Size:          {settings.EffectiveMaxCacheSizeGb} GB (5-500 GB)");
        Console.WriteLine("\nUsage: ./run-cli.sh settings [--auto-record on|off] [--auto-compile on|off] [--max-cache-gb <5..500>]\n");
        return 0;
    }

    static async Task<int> ShowDriverStatusAsync(IDriverBackend backend)
    {
        Console.WriteLine("Detecting GPU and driver caching configuration...\n");
        var info = await backend.GetDriverInfoAsync();

        Console.WriteLine($"GPU Device:           {info.DeviceName}");
        Console.WriteLine($"Driver Stack:         {info.DriverName} (v{info.DriverVersion})");
        Console.WriteLine($"Vendor:               {info.Vendor}");
        Console.WriteLine($"Global Cache Path:    {info.ShaderCachePath}");
        Console.WriteLine($"Global Cached Files:  {info.CachedFilesCount:N0} entries ({FormatBytes(info.CacheSizeBytes)})");
        if (info.SteamShaderCacheSizeBytes > 0)
        {
            Console.WriteLine($"Steam Shader Caches:  {info.SteamShaderCacheFilesCount:N0} entries ({FormatBytes(info.SteamShaderCacheSizeBytes)})");
        }
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Total Cache on Disk:  {FormatBytes(info.TotalCacheSizeBytes)}");
        Console.ResetColor();
        Console.WriteLine($"Max Cache Cap:        {SCSKiller.Linux.Core.SettingsManager.Load().EffectiveMaxCacheSizeGb} GB (configurable via './run-cli.sh settings --max-cache-gb <GB>')");

        Console.WriteLine($"Status:               {info.Status} {(info.StatusMessage != null ? $"({info.StatusMessage})" : "")}");

        string? replayer = FossilizeLocator.FindFossilizeReplay();
        Console.WriteLine($"Pipeline Replayer:    {(replayer != null ? $"Found ({replayer})" : "Not found in system")}");

        Console.WriteLine("\nRecommended Environment Variables:");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(backend.GetRecommendedEnvironmentVariables());
        Console.ResetColor();

        return 0;
    }

    static async Task<int> ListGamesAsync(GameManager manager, IDriverBackend backend, string[] filterArgs)
    {
        GameSource? sourceFilter = null;
        bool onlyNeedsCompile = false;
        var searchParts = new List<string>();

        for (int i = 0; i < filterArgs.Length; i++)
        {
            string arg = filterArgs[i];
            if ((arg.Equals("--source", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase)) && i + 1 < filterArgs.Length)
            {
                string val = filterArgs[++i];
                if (Enum.TryParse<GameSource>(val, ignoreCase: true, out var parsedSource))
                    sourceFilter = parsedSource;
            }
            else if (arg.Equals("--needs-compile", StringComparison.OrdinalIgnoreCase) || arg.Equals("--uncompiled", StringComparison.OrdinalIgnoreCase))
            {
                onlyNeedsCompile = true;
            }
            else if (Enum.TryParse<GameSource>(arg, ignoreCase: true, out var directSource) && searchParts.Count == 0 && !sourceFilter.HasValue)
            {
                sourceFilter = directSource;
            }
            else
            {
                searchParts.Add(arg);
            }
        }

        string? searchQuery = searchParts.Count > 0 ? string.Join(' ', searchParts) : null;

        Console.WriteLine("Scanning game libraries (Steam, Heroic, Lutris — Native & Flatpak)...");
        var allGames = await manager.ScanAllGamesAsync(backend);

        if (allGames.Count == 0)
        {
            Console.WriteLine("No games discovered in Steam, Heroic, or Lutris libraries.");
            Console.WriteLine("Tip: Run './run-cli.sh diagnose' to see all checked paths and why entries were skipped.");
            return 0;
        }

        var games = GameManager.FilterGames(allGames, sourceFilter, searchQuery, onlyNeedsCompile);
        if (games.Count == 0)
        {
            Console.WriteLine($"\n0 of {allGames.Count} installed games matched your filter.");
            Console.WriteLine("Usage: ./run-cli.sh list [--source steam|heroic|lutris] [--needs-compile] [<search>]\n");
            return 0;
        }

        string headerCount = games.Count == allGames.Count
            ? $"Found {games.Count} installed games:"
            : $"Showing {games.Count} of {allGames.Count} installed games:";
        Console.WriteLine($"\n{headerCount}\n");
        Console.WriteLine(string.Format("{0,-15} {1,-28} {2,-14} {3,-22} {4,-10} {5,-10} {6,-12}", "Source", "Name", "Engine", "Status", "Recorded", "Shaders", "Disk Cache"));
        Console.WriteLine(new string('-', 118));

        foreach (var g in games)
        {
            var color = g.State == CacheState.Cached ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.ForegroundColor = color;
            string cacheStr = g.CacheSizeBytes > 0 ? FormatBytes(g.CacheSizeBytes) : "0 B";
            string srcLabel = g.IsFlatpak ? $"{g.Source} (FP)" : g.Source.ToString();
            string statusLabel = g.State == CacheState.Outdated
                ? (!string.IsNullOrEmpty(g.StaleReason) ? $"Stale ({g.StaleReason})" : "Stale")
                : g.State.ToString();
            string recLabel = g.RecordedPsos > 0 ? g.RecordedPsos.ToString("N0") : "-";
            Console.WriteLine(string.Format("{0,-15} {1,-28} {2,-14} {3,-22} {4,-10} {5,-10} {6,-12}",
                srcLabel,
                Truncate(g.Name, 26),
                g.Engine,
                Truncate(statusLabel, 21),
                recLabel,
                $"~{g.TotalShadersEstimated:N0}",
                cacheStr));
            Console.ResetColor();
        }

        Console.WriteLine("\nFilter tip: './run-cli.sh list steam', './run-cli.sh list --needs-compile', or './run-cli.sh list <name>'.");
        Console.WriteLine("Run './run-cli.sh compile <id|name>' to pre-compile shaders ahead of time.\n");
        return 0;
    }

    static async Task<int> ShowDiagnosticsAsync(GameManager manager, IDriverBackend backend)
    {
        Console.WriteLine("Running full library discovery diagnostics (Native, XDG, and Flatpak)...\n");
        var report = await manager.DiagnoseAsync(backend);

        Console.WriteLine($"Driver:         {report.ActiveDriverSummary}");
        Console.WriteLine($"Default Proton: {report.DefaultProtonBinary ?? "Not found"}");
        Console.WriteLine($"XDG_DATA_HOME:  {SCSKiller.Linux.Core.AppPaths.GetXdgDataHome()}");
        Console.WriteLine($"XDG_CONFIG_HOME:{SCSKiller.Linux.Core.AppPaths.GetXdgConfigHome()}");
        Console.WriteLine($"XDG_CACHE_HOME: {SCSKiller.Linux.Core.AppPaths.GetXdgCacheHome()}");

        foreach (var src in report.Sources)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"=== {src.Source.ToString().ToUpperInvariant()} ===");
            Console.ResetColor();

            if (!string.IsNullOrEmpty(src.Error))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  Source Error: {src.Error}");
                Console.ResetColor();
            }

            Console.WriteLine($"  Checked Paths ({src.CheckedPaths.Count}):");
            foreach (var p in src.CheckedPaths)
            {
                Console.ForegroundColor = p.Exists ? ConsoleColor.Green : ConsoleColor.DarkGray;
                string status = p.Exists ? "[FOUND]  " : "[MISSING]";
                Console.WriteLine($"    {status} {p.Description}: {p.Path}");
                Console.ResetColor();
            }

            Console.WriteLine($"  Discovered Games ({src.DiscoveredGames.Count}):");
            if (src.DiscoveredGames.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("    (none)");
                Console.ResetColor();
            }
            else
            {
                foreach (var g in src.DiscoveredGames)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"    + {g.Name} [ID: {g.Id}, Source: {g.SourceLabel}, Engine: {g.Engine}]");
                    Console.ResetColor();
                    Console.WriteLine($"      Install: {g.InstallPath}");
                    Console.WriteLine($"      Exe:     {g.ExecutablePath}");
                    Console.WriteLine($"      Prefix:  {g.PrefixPath ?? "(default/shared)"}");
                    Console.WriteLine($"      Cache:   {FormatBytes(g.CacheSizeBytes)} ({g.State})");
                }
            }

            Console.WriteLine($"  Skipped Entries ({src.SkippedItems.Count}):");
            if (src.SkippedItems.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("    (none)");
                Console.ResetColor();
            }
            else
            {
                foreach (var s in src.SkippedItems)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    string fpTag = s.IsFlatpak ? " [Flatpak]" : "";
                    Console.WriteLine($"    - {s.NameOrId}{fpTag}: {s.Reason}");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"      Path: {s.Path}");
                    Console.ResetColor();
                }
            }
        }

        Console.WriteLine();
        return 0;
    }

    static GameInfo? MatchGame(IReadOnlyList<GameInfo> games, string targetQuery) =>
        games.FirstOrDefault(g => g.Id.Equals(targetQuery, StringComparison.OrdinalIgnoreCase)) ??
        games.FirstOrDefault(g => g.RawId.Equals(targetQuery, StringComparison.OrdinalIgnoreCase)) ??
        games.FirstOrDefault(g => g.Name.Contains(targetQuery, StringComparison.OrdinalIgnoreCase));

    static async Task<int> CompileGameAsync(GameManager manager, IDriverBackend backend, string targetQuery)
    {
        var games = await manager.ScanAllGamesAsync(backend);
        var game = MatchGame(games, targetQuery);

        if (game == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Game matching '{targetQuery}' was not found in installed libraries.");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"Preparing pre-compilation for: {game.Name} ({game.SourceLabel}, ID: {game.Id})");
        Console.WriteLine($"Engine: {game.Engine} | Estimated Shaders: {game.TotalShadersEstimated:N0}");
        Console.WriteLine($"Game Directory: {game.InstallPath}\n");

        var coordinator = new ShaderCompilerCoordinator(manager, backend);
        CompilationProgress? lastProgress = null;

        await foreach (var progress in coordinator.CompileGameShadersAsync(game))
        {
            lastProgress = progress;
            RenderProgressBar(progress.Percentage, progress.ProcessedShaders, progress.TotalShaders, progress.CurrentAction);
        }

        if (lastProgress?.Failed == true)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n\nERROR: {lastProgress.ErrorMessage ?? lastProgress.CurrentAction}\n");
            Console.ResetColor();
            return 1;
        }

        if (lastProgress != null && !lastProgress.CacheVerified)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n\nWARNING: {lastProgress.CurrentAction}\n");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n\n{lastProgress?.CurrentAction ?? $"Cache ready for '{game.Name}'."}");
        Console.WriteLine("Shader cache is installed and ready.\n");
        Console.ResetColor();

        return 0;
    }

    static async Task<int> CompileAllGamesAsync(GameManager manager, IDriverBackend backend, CancellationToken cancellationToken = default)
    {
        var games = await manager.ScanAllGamesAsync(backend, cancellationToken);
        var uncompiled = games.Where(g => g.State != CacheState.Cached).ToList();

        if (uncompiled.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("All detected games are already cached and up to date!");
            Console.ResetColor();
            return 0;
        }

        Console.WriteLine($"Starting batch compilation for {uncompiled.Count} games...\n");
        foreach (var game in uncompiled)
        {
            await CompileGameAsync(manager, backend, game.Id);
        }

        return 0;
    }

    static async Task<int> ClearGameCacheAsync(GameManager manager, IDriverBackend backend, string targetQuery)
    {
        var games = await manager.ScanAllGamesAsync(backend);
        var game = MatchGame(games, targetQuery);

        if (game == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Game matching '{targetQuery}' was not found in installed libraries.");
            Console.ResetColor();
            return 1;
        }

        var (bytes, files, cachePath) = await backend.GetGameCacheStatsAsync(game.Id, game.Source, game.PrefixPath);
        Console.WriteLine($"Target game:         {game.Name} ({game.SourceLabel}, ID: {game.Id})");
        Console.WriteLine($"Cache location:      {(string.IsNullOrEmpty(cachePath) ? "N/A" : cachePath)}");
        Console.WriteLine($"Current cache size:  {FormatBytes(bytes)} ({files:N0} files)");

        if (bytes == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Shader cache is already 0 B (empty). Nothing to clean.");
            Console.ResetColor();
            return 0;
        }

        Console.Write($"\nAre you sure you want to delete shader cache for '{game.Name}'? (y/N): ");
        string? confirm = Console.ReadLine();
        if (confirm?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
        {
            Console.WriteLine("Operation cancelled.");
            return 0;
        }

        Console.WriteLine("Deleting shader cache...");
        await backend.ClearGameCacheAsync(game.Id, game.Source, game.PrefixPath);

        // Verify on disk
        var (afterBytes, _, _) = await backend.GetGameCacheStatsAsync(game.Id, game.Source, game.PrefixPath);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSUCCESS: Shader cache for '{game.Name}' has been purged from disk!");
        Console.WriteLine($"Space freed: {FormatBytes(bytes)}");
        Console.WriteLine($"New state:   NeedsCompilation ({FormatBytes(afterBytes)})");
        Console.ResetColor();
        return 0;
    }

    static async Task<int> ClearAllCachesAsync(IDriverBackend backend)
    {
        var info = await backend.GetDriverInfoAsync();
        Console.WriteLine($"Total cache detected on disk: {FormatBytes(info.TotalCacheSizeBytes)}");
        Console.WriteLine($"  - Global Driver Cache:      {FormatBytes(info.CacheSizeBytes)} ({info.ShaderCachePath})");
        if (info.SteamShaderCacheSizeBytes > 0)
        {
            Console.WriteLine($"  - Steam Game Shader Caches: {FormatBytes(info.SteamShaderCacheSizeBytes)}");
        }

        Console.Write("\nAre you sure you want to purge ALL GPU driver and Steam game shader caches? (y/N): ");
        string? confirm = Console.ReadLine();
        if (confirm?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
        {
            Console.WriteLine("Operation cancelled.");
            return 0;
        }

        Console.WriteLine("Purging all driver and game shader caches from disk...");
        await backend.ClearAllCachesAsync();

        var afterInfo = await backend.GetDriverInfoAsync();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSUCCESS: All shader caches successfully purged from disk!");
        Console.WriteLine($"Remaining cache size: {FormatBytes(afterInfo.TotalCacheSizeBytes)}");
        Console.ResetColor();
        return 0;
    }

    static async Task<int> ClearCacheInteractiveAsync(GameManager manager, IDriverBackend backend)
    {
        Console.WriteLine("Select cache purge option:");
        Console.WriteLine("  1) Clear ALL GPU driver and Steam game shader caches");
        Console.WriteLine("  2) Clear shader cache for a specific game");
        Console.WriteLine("  3) Clear only global driver cache");
        Console.WriteLine("  q) Cancel");
        Console.Write("\nEnter choice (1-3, q): ");
        string? choice = Console.ReadLine()?.Trim();

        switch (choice)
        {
            case "1":
                return await ClearAllCachesAsync(backend);
            case "2":
                Console.Write("Enter game name or App ID: ");
                string? query = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(query))
                {
                    Console.WriteLine("No game specified. Cancelled.");
                    return 0;
                }
                return await ClearGameCacheAsync(manager, backend, query);
            case "3":
                Console.WriteLine("Purging global driver cache...");
                await backend.ClearCacheAsync();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Global driver cache cleared.");
                Console.ResetColor();
                return 0;
            default:
                Console.WriteLine("Cancelled.");
                return 0;
        }
    }

    static void RenderProgressBar(double percentage, int current, int total, string action)
    {
        int barWidth = 30;
        int filled = (int)Math.Round((percentage / 100.0) * barWidth);
        string bar = new string('█', Math.Min(barWidth, filled)) + new string('░', Math.Max(0, barWidth - filled));

        Console.Write($"\r[{bar}] {percentage,5:F1}% ({current}/{total}) — {Truncate(action, 45)}");
    }

    static string FormatBytes(long bytes)
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

    static string Truncate(string val, int maxLen)
    {
        if (string.IsNullOrEmpty(val)) return string.Empty;
        return val.Length <= maxLen ? val : val.Substring(0, maxLen - 3) + "...";
    }
}
