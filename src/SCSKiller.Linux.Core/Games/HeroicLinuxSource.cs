using SCSKiller.Linux.Core.Models;
using System.Text.Json;

namespace SCSKiller.Linux.Core.Games;

public class HeroicLinuxSource : IGameSource
{
    private readonly string? _homeOverride;
    private readonly string? _xdgConfigOverride;

    public HeroicLinuxSource(string? homeDir = null, string? xdgConfigHome = null)
    {
        _homeOverride = homeDir;
        _xdgConfigOverride = xdgConfigHome;
    }

    public GameSource SourceType => GameSource.Heroic;

    public async Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
    {
        var diag = await DiagnoseAsync(cancellationToken);
        return diag.DiscoveredGames;
    }

    public async Task<SourceDiagnostic> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var checkedPaths = new List<CheckedPathInfo>();
            var discovered = new List<GameInfo>();
            var skipped = new List<SkippedItemInfo>();
            string? topError = null;

            try
            {
                string home = _homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string xdgConfig = AppPaths.GetXdgConfigHome(home, _xdgConfigOverride);
                string defaultConfig = Path.Combine(home, ".config");

                var candidateRoots = new List<(string Path, string Description, bool IsFlatpak)>();
                if (!string.Equals(AppPaths.GetCanonicalPath(xdgConfig), AppPaths.GetCanonicalPath(defaultConfig), StringComparison.Ordinal))
                {
                    candidateRoots.Add((Path.Combine(xdgConfig, "heroic"), "Native Heroic ($XDG_CONFIG_HOME/heroic)", false));
                }

                candidateRoots.AddRange(new[]
                {
                    (Path.Combine(home, ".config", "heroic"), "Native Heroic (~/.config/heroic)", false),
                    (Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"), "Flatpak Heroic (~/.var/app/com.heroicgameslauncher.hgl/config/heroic)", true)
                });

                var activeRoots = new List<(string Root, bool IsFlatpak)>();
                var seenCanonicalRoots = new HashSet<string>(StringComparer.Ordinal);

                foreach (var candidate in candidateRoots)
                {
                    bool exists = Directory.Exists(candidate.Path);
                    checkedPaths.Add(new CheckedPathInfo(candidate.Path, candidate.Description, exists, candidate.IsFlatpak));
                    if (!exists) continue;

                    string canonical = AppPaths.GetCanonicalPath(candidate.Path);
                    if (seenCanonicalRoots.Add(canonical))
                    {
                        activeRoots.Add((candidate.Path, candidate.IsFlatpak));
                    }
                }

                foreach (var (heroicConfig, isFlatpak) in activeRoots)
                {
                    var seenIdsInScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // 1. Epic Games from legendary installed.json
                    string legendaryInstalled = Path.Combine(heroicConfig, "legendaryConfig", "legendary", "installed.json");
                    bool legExists = File.Exists(legendaryInstalled);
                    checkedPaths.Add(new CheckedPathInfo(
                        legendaryInstalled,
                        isFlatpak ? "Flatpak Heroic Legendary (Epic) installed.json" : "Heroic Legendary (Epic) installed.json",
                        legExists,
                        isFlatpak));

                    if (legExists)
                    {
                        try
                        {
                            var json = File.ReadAllText(legendaryInstalled);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                {
                                    var entry = prop.Value;
                                    string appId = prop.Name;
                                    string title = entry.TryGetProperty("title", out var t) && !string.IsNullOrWhiteSpace(t.GetString())
                                        ? t.GetString()!
                                        : appId;
                                    string label = $"{title} ({appId})";

                                    if (entry.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True)
                                    {
                                        skipped.Add(new SkippedItemInfo(label, legendaryInstalled, "DLC entry", isFlatpak));
                                        continue;
                                    }

                                    string? installPath = entry.TryGetProperty("install_path", out var p) ? p.GetString() : null;
                                    string? exeName = entry.TryGetProperty("executable", out var e) ? e.GetString() : null;

                                    if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                                    {
                                        skipped.Add(new SkippedItemInfo(label, installPath ?? legendaryInstalled, "Game install directory missing on disk", isFlatpak));
                                        continue;
                                    }

                                    string exePath = string.IsNullOrWhiteSpace(exeName)
                                        ? (GameEngineDetector.FindExecutable(installPath) ?? string.Empty)
                                        : Path.Combine(installPath, exeName.Replace('\\', '/'));

                                    if (!File.Exists(exePath))
                                    {
                                        exePath = GameEngineDetector.FindExecutable(installPath) ?? string.Empty;
                                    }

                                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                                    {
                                        skipped.Add(new SkippedItemInfo(label, installPath, "No Windows .exe found (Linux native build or empty folder)", isFlatpak));
                                        continue;
                                    }

                                    if (!seenIdsInScope.Add(appId)) continue;

                                    string? prefix = ReadGamePrefixFromHeroicConfig(heroicConfig, appId);
                                    var (engine, estimated) = GameEngineDetector.Detect(installPath);

                                    discovered.Add(new GameInfo(
                                        Id: isFlatpak ? $"flatpak_{appId}" : appId,
                                        Name: title,
                                        Source: GameSource.Heroic,
                                        InstallPath: installPath,
                                        ExecutablePath: exePath,
                                        PrefixPath: prefix,
                                        BannerPath: null,
                                        Engine: engine,
                                        State: CacheState.NeedsCompilation,
                                        TotalShadersEstimated: estimated,
                                        CompiledShadersCount: 0,
                                        LastCompiledAt: null,
                                        IsFlatpak: isFlatpak
                                    ));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new SkippedItemInfo("installed.json", legendaryInstalled, $"Invalid JSON: {ex.Message}", isFlatpak));
                        }
                    }

                    // 2. Sideloaded Heroic apps from sideload_apps/library.json
                    string sideloadLib = Path.Combine(heroicConfig, "sideload_apps", "library.json");
                    bool sideExists = File.Exists(sideloadLib);
                    checkedPaths.Add(new CheckedPathInfo(
                        sideloadLib,
                        isFlatpak ? "Flatpak Heroic sideload_apps/library.json" : "Heroic sideload_apps/library.json",
                        sideExists,
                        isFlatpak));

                    if (sideExists)
                    {
                        try
                        {
                            var json = File.ReadAllText(sideloadLib);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("games", out var gamesArr) && gamesArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in gamesArr.EnumerateArray())
                                {
                                    string? appId = item.TryGetProperty("app_name", out var an) ? an.GetString() : null;
                                    if (string.IsNullOrWhiteSpace(appId)) continue;

                                    string title = item.TryGetProperty("title", out var t) && !string.IsNullOrWhiteSpace(t.GetString())
                                        ? t.GetString()!
                                        : appId;
                                    string label = $"{title} ({appId})";

                                    if (item.TryGetProperty("is_installed", out var inst) && inst.ValueKind == JsonValueKind.False)
                                    {
                                        skipped.Add(new SkippedItemInfo(label, sideloadLib, "Marked as not installed", isFlatpak));
                                        continue;
                                    }

                                    string? exePath = null;
                                    if (item.TryGetProperty("install", out var installObj) && installObj.ValueKind == JsonValueKind.Object)
                                    {
                                        if (installObj.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True)
                                        {
                                            skipped.Add(new SkippedItemInfo(label, sideloadLib, "DLC entry", isFlatpak));
                                            continue;
                                        }
                                        exePath = installObj.TryGetProperty("executable", out var ex) ? ex.GetString() : null;
                                    }

                                    string? folderName = item.TryGetProperty("folder_name", out var fn) ? fn.GetString() : null;
                                    string? installPath = null;

                                    if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                                    {
                                        installPath = LutrisLinuxSource.InferInstallRoot(exePath);
                                    }
                                    else if (!string.IsNullOrWhiteSpace(folderName) && Directory.Exists(folderName))
                                    {
                                        installPath = folderName;
                                        exePath = GameEngineDetector.FindExecutable(installPath);
                                    }

                                    if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                                    {
                                        skipped.Add(new SkippedItemInfo(label, folderName ?? exePath ?? sideloadLib, "Game install directory missing on disk", isFlatpak));
                                        continue;
                                    }

                                    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                    {
                                        skipped.Add(new SkippedItemInfo(label, installPath, "No Windows .exe found", isFlatpak));
                                        continue;
                                    }

                                    if (!seenIdsInScope.Add(appId)) continue;

                                    string? prefix = ReadGamePrefixFromHeroicConfig(heroicConfig, appId);
                                    var (engine, estimated) = GameEngineDetector.Detect(installPath);

                                    discovered.Add(new GameInfo(
                                        Id: isFlatpak ? $"flatpak_{appId}" : appId,
                                        Name: title,
                                        Source: GameSource.Heroic,
                                        InstallPath: installPath,
                                        ExecutablePath: exePath,
                                        PrefixPath: prefix,
                                        BannerPath: null,
                                        Engine: engine,
                                        State: CacheState.NeedsCompilation,
                                        TotalShadersEstimated: estimated,
                                        CompiledShadersCount: 0,
                                        LastCompiledAt: null,
                                        IsFlatpak: isFlatpak
                                    ));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new SkippedItemInfo("sideload_apps/library.json", sideloadLib, $"Invalid JSON: {ex.Message}", isFlatpak));
                        }
                    }

                    // 3. GOG games from gog_store/installed.json
                    string gogInstalled = Path.Combine(heroicConfig, "gog_store", "installed.json");
                    bool gogExists = File.Exists(gogInstalled);
                    checkedPaths.Add(new CheckedPathInfo(
                        gogInstalled,
                        isFlatpak ? "Flatpak Heroic GOG installed.json" : "Heroic GOG installed.json",
                        gogExists,
                        isFlatpak));

                    if (gogExists)
                    {
                        try
                        {
                            var json = File.ReadAllText(gogInstalled);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("installed", out var instArr) && instArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in instArr.EnumerateArray())
                                {
                                    string? appId = item.TryGetProperty("appName", out var an) ? an.GetString() : null;
                                    if (string.IsNullOrWhiteSpace(appId)) continue;

                                    if (item.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True)
                                    {
                                        skipped.Add(new SkippedItemInfo(appId, gogInstalled, "GOG DLC entry", isFlatpak));
                                        continue;
                                    }

                                    string? installPath = item.TryGetProperty("install_path", out var ip) ? ip.GetString() : null;
                                    if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                                    {
                                        skipped.Add(new SkippedItemInfo(appId, installPath ?? gogInstalled, "GOG install directory missing on disk", isFlatpak));
                                        continue;
                                    }

                                    string? exePath = GameEngineDetector.FindExecutable(installPath);
                                    if (string.IsNullOrEmpty(exePath))
                                    {
                                        skipped.Add(new SkippedItemInfo(appId, installPath, "No Windows .exe found (Linux native GOG build or empty folder)", isFlatpak));
                                        continue;
                                    }

                                    if (!seenIdsInScope.Add(appId)) continue;

                                    string title = Path.GetFileName(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                                    if (string.IsNullOrWhiteSpace(title)) title = appId;

                                    string? prefix = ReadGamePrefixFromHeroicConfig(heroicConfig, appId);
                                    var (engine, estimated) = GameEngineDetector.Detect(installPath);

                                    discovered.Add(new GameInfo(
                                        Id: isFlatpak ? $"flatpak_{appId}" : appId,
                                        Name: title,
                                        Source: GameSource.Heroic,
                                        InstallPath: installPath,
                                        ExecutablePath: exePath,
                                        PrefixPath: prefix,
                                        BannerPath: null,
                                        Engine: engine,
                                        State: CacheState.NeedsCompilation,
                                        TotalShadersEstimated: estimated,
                                        CompiledShadersCount: 0,
                                        LastCompiledAt: null,
                                        IsFlatpak: isFlatpak
                                    ));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new SkippedItemInfo("gog_store/installed.json", gogInstalled, $"Invalid JSON: {ex.Message}", isFlatpak));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                topError = ex.Message;
            }

            return new SourceDiagnostic(GameSource.Heroic, checkedPaths, discovered, skipped, topError);
        }, cancellationToken);
    }

    private static string? ReadGamePrefixFromHeroicConfig(string heroicConfig, string gameId)
    {
        string gameConfig = Path.Combine(heroicConfig, "GamesConfig", $"{gameId}.json");
        if (!File.Exists(gameConfig)) return null;
        try
        {
            return ReadWinePrefix(File.ReadAllText(gameConfig), gameId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The game's Wine prefix from its Heroic config file. Heroic keeps the game's settings under a key named
    /// after the game's id (next to "version" and "explicit"); a plain top-level "winePrefix" is accepted too.</summary>
    internal static string? ReadWinePrefix(string json, string gameId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var settings = root.TryGetProperty(gameId, out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
        return settings.TryGetProperty("winePrefix", out var wp) && wp.ValueKind == JsonValueKind.String ? wp.GetString() : null;
    }

    /// <summary>Reads the configured Proton binary path (`wineVersion.bin`) from a Heroic game config JSON if it points to a Proton script.</summary>
    internal static string? ReadProtonBinary(string json, string gameId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var settings = root.TryGetProperty(gameId, out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
        if (settings.TryGetProperty("wineVersion", out var wv) && wv.ValueKind == JsonValueKind.Object)
        {
            string? type = wv.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;
            string? bin = wv.TryGetProperty("bin", out var bp) && bp.ValueKind == JsonValueKind.String ? bp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(bin) &&
                (string.Equals(type, "proton", StringComparison.OrdinalIgnoreCase) || bin.EndsWith("/proton", StringComparison.OrdinalIgnoreCase)))
            {
                return bin;
            }
        }
        return null;
    }
}
