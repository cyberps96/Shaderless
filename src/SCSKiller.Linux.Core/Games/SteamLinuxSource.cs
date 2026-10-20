using SCSKiller.Linux.Core.Models;
using System.Text.RegularExpressions;

namespace SCSKiller.Linux.Core.Games;

public class SteamLinuxSource : IGameSource
{
    private readonly string? _homeOverride;
    private readonly string? _xdgDataOverride;

    public SteamLinuxSource(string? homeDir = null, string? xdgDataHome = null)
    {
        _homeOverride = homeDir;
        _xdgDataOverride = xdgDataHome;
    }

    public GameSource SourceType => GameSource.Steam;

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
                string xdgData = AppPaths.GetXdgDataHome(home, _xdgDataOverride);
                string defaultData = Path.Combine(home, ".local", "share");

                var candidateRoots = new List<(string Path, string Description, bool IsFlatpak)>();
                if (!string.Equals(AppPaths.GetCanonicalPath(xdgData), AppPaths.GetCanonicalPath(defaultData), StringComparison.Ordinal))
                {
                    candidateRoots.Add((Path.Combine(xdgData, "Steam"), "Native Steam ($XDG_DATA_HOME/Steam)", false));
                }

                candidateRoots.AddRange(new[]
                {
                    (Path.Combine(home, ".local", "share", "Steam"), "Native Steam (~/.local/share/Steam)", false),
                    (Path.Combine(home, ".steam", "steam"), "Native Steam symlink (~/.steam/steam)", false),
                    (Path.Combine(home, ".steam", "root"), "Native Steam symlink (~/.steam/root)", false),
                    (Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"), "Flatpak Steam (~/.var/app/com.valvesoftware.Steam/.local/share/Steam)", true),
                    (Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"), "Flatpak Steam symlink (~/.var/app/com.valvesoftware.Steam/data/Steam)", true)
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

                foreach (var (steamRoot, isFlatpak) in activeRoots)
                {
                    var libraryFolders = new List<string> { steamRoot };
                    var seenCanonicalLibraries = new HashSet<string>(StringComparer.Ordinal)
                    {
                        AppPaths.GetCanonicalPath(steamRoot)
                    };

                    string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                    bool vdfExists = File.Exists(vdfPath);
                    checkedPaths.Add(new CheckedPathInfo(
                        vdfPath,
                        isFlatpak ? "Flatpak Steam libraryfolders.vdf" : "Native Steam libraryfolders.vdf",
                        vdfExists,
                        isFlatpak));

                    if (vdfExists)
                    {
                        try
                        {
                            var content = File.ReadAllText(vdfPath);
                            var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""");
                            foreach (Match m in matches)
                            {
                                if (m.Groups.Count <= 1) continue;
                                string rawLib = m.Groups[1].Value.Replace(@"\\", "/");
                                bool libExists = Directory.Exists(rawLib);
                                string canonicalLib = AppPaths.GetCanonicalPath(rawLib);
                                if (seenCanonicalLibraries.Add(canonicalLib))
                                {
                                    checkedPaths.Add(new CheckedPathInfo(
                                        rawLib,
                                        isFlatpak ? "Flatpak Steam library folder" : "Steam library folder",
                                        libExists,
                                        isFlatpak));
                                    if (libExists)
                                    {
                                        libraryFolders.Add(rawLib);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new SkippedItemInfo("libraryfolders.vdf", vdfPath, $"Unreadable VDF: {ex.Message}", isFlatpak));
                        }
                    }

                    var seenAppIdsInScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var library in libraryFolders)
                    {
                        string steamapps = Path.Combine(library, "steamapps");
                        if (!Directory.Exists(steamapps)) continue;

                        string[] manifestFiles;
                        try
                        {
                            manifestFiles = Directory.GetFiles(steamapps, "appmanifest_*.acf");
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new SkippedItemInfo("steamapps", steamapps, $"Cannot enumerate manifests: {ex.Message}", isFlatpak));
                            continue;
                        }

                        foreach (var manifest in manifestFiles)
                        {
                            try
                            {
                                var text = File.ReadAllText(manifest);
                                string? appId = Regex.Match(text, @"""appid""\s+""([^""]+)""").Groups[1].Value;
                                string? name = Regex.Match(text, @"""name""\s+""([^""]+)""").Groups[1].Value;
                                string? installDir = Regex.Match(text, @"""installdir""\s+""([^""]+)""").Groups[1].Value;

                                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(installDir))
                                {
                                    skipped.Add(new SkippedItemInfo(Path.GetFileName(manifest), manifest, "Manifest missing appid or installdir", isFlatpak));
                                    continue;
                                }

                                string displayLabel = string.IsNullOrWhiteSpace(name) ? $"{installDir} ({appId})" : $"{name} ({appId})";

                                // Skip Steam runtimes and common redistributables
                                if (installDir.StartsWith("SteamLinuxRuntime", StringComparison.OrdinalIgnoreCase) ||
                                    installDir.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
                                    installDir.Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase))
                                {
                                    skipped.Add(new SkippedItemInfo(displayLabel, manifest, "Steam runtime / Proton compatibility tool", isFlatpak));
                                    continue;
                                }

                                if (!seenAppIdsInScope.Add(appId))
                                {
                                    continue;
                                }

                                string gamePath = Path.Combine(steamapps, "common", installDir);
                                if (!Directory.Exists(gamePath))
                                {
                                    skipped.Add(new SkippedItemInfo(displayLabel, gamePath, "Game install directory missing on disk", isFlatpak));
                                    continue;
                                }

                                string prefixPath = Path.Combine(steamapps, "compatdata", appId, "pfx");
                                if (!Directory.Exists(prefixPath))
                                {
                                    string rootPrefix = Path.Combine(steamRoot, "steamapps", "compatdata", appId, "pfx");
                                    prefixPath = Directory.Exists(rootPrefix) ? rootPrefix : string.Empty;
                                }

                                string? exe = GameEngineDetector.FindExecutable(gamePath);
                                if (string.IsNullOrEmpty(exe))
                                {
                                    skipped.Add(new SkippedItemInfo(displayLabel, gamePath, "No Windows .exe found (Linux native build or empty folder)", isFlatpak));
                                    continue;
                                }

                                var (engine, estimatedShaders) = GameEngineDetector.Detect(gamePath);

                                // Locate banner artwork
                                string banner = Path.Combine(steamRoot, "appcache", "librarycache", $"{appId}_library_600x900.jpg");
                                if (!File.Exists(banner))
                                {
                                    banner = Path.Combine(steamRoot, "appcache", "librarycache", $"{appId}_header.jpg");
                                    if (!File.Exists(banner)) banner = string.Empty;
                                }

                                string gameId = isFlatpak ? $"flatpak_{appId}" : appId;

                                discovered.Add(new GameInfo(
                                    Id: gameId,
                                    Name: string.IsNullOrWhiteSpace(name) ? installDir : name,
                                    Source: GameSource.Steam,
                                    InstallPath: gamePath,
                                    ExecutablePath: exe,
                                    PrefixPath: string.IsNullOrEmpty(prefixPath) ? null : prefixPath,
                                    BannerPath: string.IsNullOrEmpty(banner) ? null : banner,
                                    Engine: engine,
                                    State: CacheState.NeedsCompilation,
                                    TotalShadersEstimated: estimatedShaders,
                                    CompiledShadersCount: 0,
                                    LastCompiledAt: null,
                                    IsFlatpak: isFlatpak
                                ));
                            }
                            catch (Exception ex)
                            {
                                skipped.Add(new SkippedItemInfo(Path.GetFileName(manifest), manifest, $"Manifest parse error: {ex.Message}", isFlatpak));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                topError = ex.Message;
            }

            return new SourceDiagnostic(GameSource.Steam, checkedPaths, discovered, skipped, topError);
        }, cancellationToken);
    }
}
