using SCSKiller.Linux.Core.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SCSKiller.Linux.Core.Drivers;

public record WarmerEvent(string Event, int? Threads = null, int? Done = null, int? Total = null, int? Failed = null, string? Message = null);

public class ProtonRunner
{
    private static readonly string HomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string GetRawGameId(string gameId) =>
        !string.IsNullOrEmpty(gameId) && gameId.StartsWith("flatpak_", StringComparison.OrdinalIgnoreCase)
            ? gameId["flatpak_".Length..]
            : gameId;

    public static bool IsFlatpakGame(string? gameId, string? installPath = null, string? prefixPath = null)
    {
        if (!string.IsNullOrEmpty(gameId) && gameId.StartsWith("flatpak_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(installPath) && installPath.Contains("/.var/app/", StringComparison.Ordinal))
            return true;
        if (!string.IsNullOrEmpty(prefixPath) && prefixPath.Contains("/.var/app/", StringComparison.Ordinal))
            return true;
        return false;
    }

    public static string ResolveSteamRoot(string? gameId = null, string? installPath = null, string? prefixPath = null)
    {
        string flatpakSteam = Path.Combine(HomeDir, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        if (IsFlatpakGame(gameId, installPath, prefixPath) && Directory.Exists(flatpakSteam))
        {
            return flatpakSteam;
        }

        string xdgSteam = Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgDataHome(HomeDir), "Steam");
        string localSteam = Path.Combine(HomeDir, ".local", "share", "Steam");
        string dotSteam = Path.Combine(HomeDir, ".steam", "steam");

        foreach (var candidate in new[] { xdgSteam, localSteam, dotSteam, flatpakSteam })
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return localSteam;
    }

    public static IReadOnlyList<string> GetAllSteamRoots()
    {
        var candidates = new[]
        {
            Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgDataHome(HomeDir), "Steam"),
            Path.Combine(HomeDir, ".local", "share", "Steam"),
            Path.Combine(HomeDir, ".steam", "steam"),
            Path.Combine(HomeDir, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
        };

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (!Directory.Exists(c)) continue;
            string canonical = SCSKiller.Linux.Core.AppPaths.GetCanonicalPath(c);
            if (seen.Add(canonical))
                result.Add(c);
        }
        return result;
    }

    public static IReadOnlyList<string> GetAllSteamShaderCacheRoots()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in GetAllSteamRoots())
        {
            string sc = Path.Combine(root, "steamapps", "shadercache");
            if (!Directory.Exists(sc)) continue;
            string canonical = SCSKiller.Linux.Core.AppPaths.GetCanonicalPath(sc);
            if (seen.Add(canonical))
                result.Add(sc);
        }
        return result;
    }

    public static string GetSteamShaderCachePath(string gameId, string? installPath = null, string? prefixPath = null)
    {
        string steamRoot = ResolveSteamRoot(gameId, installPath, prefixPath);
        string rawId = GetRawGameId(gameId);
        return Path.Combine(steamRoot, "steamapps", "shadercache", rawId);
    }

    public static string GetNonSteamShaderCachePath(string gameId, string? prefixPath)
    {
        if (!string.IsNullOrEmpty(prefixPath))
            return Path.Combine(prefixPath, "shadercache", gameId);
        return Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "prefixes", gameId, "shadercache", gameId);
    }

    public static string? GetFlatpakLauncherNvidiaGlCachePath(GameSource source)
    {
        string? appId = source switch
        {
            GameSource.Steam => "com.valvesoftware.Steam",
            GameSource.Heroic => "com.heroicgameslauncher.hgl",
            GameSource.Lutris => "net.lutris.Lutris",
            _ => null
        };
        if (appId == null) return null;

        string appRoot = Path.Combine(HomeDir, ".var", "app", appId);
        if (!Directory.Exists(appRoot)) return null;

        string cacheDir = source == GameSource.Steam
            ? Path.Combine(appRoot, ".cache")
            : Path.Combine(appRoot, "cache");
        return Path.Combine(cacheDir, "nvidia", "GLCache");
    }

    /// <summary>Returns true if a file inside a shadercache directory is an actual compiled GPU/VKD3D/DXVK cache file
    /// rather than an uncompiled Steam Fossilize download (`fozpipelines*` / `fozmedia*`) or metadata file.</summary>
    public static bool IsCompiledShaderCacheFile(string filePath)
    {
        string norm = filePath.Replace('\\', '/');
        if (norm.Contains("/fozpipelines", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/fozmedia", StringComparison.OrdinalIgnoreCase))
            return false;

        string fileName = Path.GetFileName(norm);
        if (fileName.EndsWith(".stamp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("TOUCH", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public static string? FindProtonBinaryForGame(GameInfo game, string? preferredVersion = null)
    {
        string rawId = game.RawId;

        if (game.Source == GameSource.Steam && !string.IsNullOrEmpty(rawId))
        {
            string steamRoot = ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath);
            string? resolvedSteam = ResolveSteamProtonForApp(steamRoot, rawId, game.PrefixPath);
            if (!string.IsNullOrEmpty(resolvedSteam))
                return resolvedSteam;
        }
        else if (game.Source == GameSource.Heroic && !string.IsNullOrEmpty(rawId))
        {
            var heroicDirs = game.IsFlatpak
                ? new[]
                {
                    Path.Combine(HomeDir, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"),
                    Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgConfigHome(HomeDir), "heroic"),
                    Path.Combine(HomeDir, ".config", "heroic")
                }
                : new[]
                {
                    Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgConfigHome(HomeDir), "heroic"),
                    Path.Combine(HomeDir, ".config", "heroic"),
                    Path.Combine(HomeDir, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic")
                };

            foreach (var hDir in heroicDirs.Distinct(StringComparer.Ordinal))
            {
                string gameCfg = Path.Combine(hDir, "GamesConfig", $"{rawId}.json");
                if (File.Exists(gameCfg))
                {
                    try
                    {
                        string? protonBin = SCSKiller.Linux.Core.Games.HeroicLinuxSource.ReadProtonBinary(File.ReadAllText(gameCfg), rawId);
                        if (!string.IsNullOrEmpty(protonBin) && File.Exists(protonBin))
                            return protonBin;
                    }
                    catch { }
                }

                string globalCfg = Path.Combine(hDir, "config.json");
                if (File.Exists(globalCfg))
                {
                    try
                    {
                        string? defaultProton = SCSKiller.Linux.Core.Games.HeroicLinuxSource.ReadProtonBinary(File.ReadAllText(globalCfg), "defaultSettings");
                        if (!string.IsNullOrEmpty(defaultProton) && File.Exists(defaultProton))
                            return defaultProton;
                    }
                    catch { }
                }

                string hToolsProton = Path.Combine(hDir, "tools", "proton");
                if (Directory.Exists(hToolsProton))
                {
                    foreach (var dir in Directory.GetDirectories(hToolsProton).OrderByDescending(d => d))
                    {
                        string p = Path.Combine(dir, "proton");
                        if (File.Exists(p))
                            return p;
                    }
                }
            }
        }
        else if (game.Source == GameSource.Lutris)
        {
            if (!string.IsNullOrEmpty(game.PrefixPath))
            {
                foreach (var cfgCandidate in new[]
                {
                    Path.Combine(game.PrefixPath, "config_info"),
                    Path.Combine(Path.GetDirectoryName(game.PrefixPath.TrimEnd(Path.DirectorySeparatorChar)) ?? "", "config_info")
                })
                {
                    if (!File.Exists(cfgCandidate)) continue;
                    try
                    {
                        foreach (var line in File.ReadLines(cfgCandidate))
                        {
                            int filesIdx = line.IndexOf("/files/", StringComparison.Ordinal);
                            if (filesIdx > 0)
                            {
                                string protonBin = Path.Combine(line.Substring(0, filesIdx), "proton");
                                if (File.Exists(protonBin))
                                    return protonBin;
                            }
                        }
                    }
                    catch { }
                }
            }

            var lutrisRunnerDirs = game.IsFlatpak
                ? new[]
                {
                    Path.Combine(HomeDir, ".var", "app", "net.lutris.Lutris", "data", "lutris", "runners", "proton"),
                    Path.Combine(HomeDir, ".var", "app", "net.lutris.Lutris", "data", "lutris", "runners", "wine"),
                    Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgDataHome(HomeDir), "lutris", "runners", "proton"),
                    Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgDataHome(HomeDir), "lutris", "runners", "wine"),
                    Path.Combine(HomeDir, ".local", "share", "lutris", "runners", "proton"),
                    Path.Combine(HomeDir, ".local", "share", "lutris", "runners", "wine")
                }
                : new[]
                {
                    Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgDataHome(HomeDir), "lutris", "runners", "proton"),
                    Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgDataHome(HomeDir), "lutris", "runners", "wine"),
                    Path.Combine(HomeDir, ".local", "share", "lutris", "runners", "proton"),
                    Path.Combine(HomeDir, ".local", "share", "lutris", "runners", "wine"),
                    Path.Combine(HomeDir, ".var", "app", "net.lutris.Lutris", "data", "lutris", "runners", "proton"),
                    Path.Combine(HomeDir, ".var", "app", "net.lutris.Lutris", "data", "lutris", "runners", "wine")
                };

            foreach (var rDir in lutrisRunnerDirs.Distinct(StringComparer.Ordinal))
            {
                if (!Directory.Exists(rDir)) continue;
                foreach (var dir in Directory.GetDirectories(rDir).OrderByDescending(d => d))
                {
                    string p = Path.Combine(dir, "proton");
                    if (File.Exists(p))
                        return p;
                }
            }
        }

        return FindProtonBinary(preferredVersion);
    }

    private static readonly Dictionary<string, (long LastWriteTicks, string CompatBlock)> _vdfCompatBlockCache = new(StringComparer.Ordinal);
    private static readonly object _vdfCompatBlockLock = new();

    public static string? ResolveSteamProtonForApp(
        string steamRoot,
        string rawId,
        string? prefixPath = null,
        bool allowSystemCompatTools = true)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || string.IsNullOrWhiteSpace(rawId))
            return null;

        var steamLibraries = GetSteamLibraryFolders(steamRoot);
        string? compatBlock = null;
        string configVdf = Path.Combine(steamRoot, "config", "config.vdf");
        if (File.Exists(configVdf))
        {
            try
            {
                string vdf = File.ReadAllText(configVdf);
                compatBlock = ExtractVdfBlock(vdf, "CompatToolMapping");
            }
            catch { }
        }

        // 1. Per-game Proton override in Steam's CompatToolMapping[rawId] ALWAYS wins first,
        //    so changing Proton in Steam's Compatibility dropdown is detected immediately without launching the game once.
        bool perGameOverrideCleared = false;
        if (!string.IsNullOrEmpty(compatBlock))
        {
            string? appBlock = ExtractVdfBlock(compatBlock, rawId);
            if (appBlock != null)
            {
                var nameMatch = Regex.Match(appBlock, @"""name""\s+""([^""]*)""", RegexOptions.Singleline);
                if (nameMatch.Success)
                {
                    string appToolName = nameMatch.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(appToolName))
                    {
                        string? resolvedPerGame = ResolveCompatToolName(appToolName, steamRoot, steamLibraries, allowSystemCompatTools);
                        if (!string.IsNullOrEmpty(resolvedPerGame))
                            return resolvedPerGame;
                    }
                    else
                    {
                        // User explicitly unchecked "Force the use of a specific Steam Play compatibility tool"
                        perGameOverrideCleared = true;
                    }
                }
            }
        }

        // 2. If the user explicitly unchecked a per-game override, honor global CompatToolMapping["0"] immediately
        if (perGameOverrideCleared && !string.IsNullOrEmpty(compatBlock))
        {
            string? globalProton = ResolveDefaultCompatToolFromBlock(compatBlock, steamRoot, steamLibraries, allowSystemCompatTools);
            if (!string.IsNullOrEmpty(globalProton))
                return globalProton;
        }

        // 3. Check compatdata/<AppID>/config_info across the game's prefix and Steam libraries
        var configInfoCandidates = new List<string>();
        if (!string.IsNullOrEmpty(prefixPath))
        {
            string trimmed = prefixPath.TrimEnd(Path.DirectorySeparatorChar);
            string? compatDir = trimmed.EndsWith("/pfx", StringComparison.Ordinal) || trimmed.EndsWith("\\pfx", StringComparison.Ordinal)
                ? Path.GetDirectoryName(trimmed)
                : trimmed;
            if (!string.IsNullOrEmpty(compatDir))
                configInfoCandidates.Add(Path.Combine(compatDir, "config_info"));
        }
        foreach (var lib in steamLibraries)
        {
            configInfoCandidates.Add(Path.Combine(lib, "steamapps", "compatdata", rawId, "config_info"));
        }

        foreach (var configInfo in configInfoCandidates.Distinct(StringComparer.Ordinal))
        {
            string? prefixProton = TryReadProtonBinaryFromConfigInfo(configInfo);
            if (!string.IsNullOrEmpty(prefixProton))
                return prefixProton;
        }

        // 4. Global default CompatToolMapping["0"] for newly installed games without a prefix yet
        if (!string.IsNullOrEmpty(compatBlock))
        {
            string? globalProton = ResolveDefaultCompatToolFromBlock(compatBlock, steamRoot, steamLibraries, allowSystemCompatTools);
            if (!string.IsNullOrEmpty(globalProton))
                return globalProton;
        }

        // 5. Fallback Proton installations inside this Steam root / libraries
        var rootCandidates = new List<string>();
        string rootTools = Path.Combine(steamRoot, "compatibilitytools.d");
        if (Directory.Exists(rootTools))
        {
            foreach (var dir in Directory.GetDirectories(rootTools).OrderByDescending(d => d))
                rootCandidates.Add(Path.Combine(dir, "proton"));
        }
        foreach (var lib in steamLibraries)
        {
            string rootCommon = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(rootCommon)) continue;
            rootCandidates.Add(Path.Combine(rootCommon, "Proton - Experimental", "proton"));
            rootCandidates.Add(Path.Combine(rootCommon, "Proton Hotfix", "proton"));
            rootCandidates.Add(Path.Combine(rootCommon, "Proton 10.0 (Beta)", "proton"));
            rootCandidates.Add(Path.Combine(rootCommon, "Proton 10.0", "proton"));
            rootCandidates.Add(Path.Combine(rootCommon, "Proton 9.0 (Beta)", "proton"));
            rootCandidates.Add(Path.Combine(rootCommon, "Proton 9.0", "proton"));
            rootCandidates.Add(Path.Combine(rootCommon, "Proton 8.0", "proton"));
            foreach (var dir in Directory.GetDirectories(rootCommon, "Proton*").OrderByDescending(d => d))
                rootCandidates.Add(Path.Combine(dir, "proton"));
        }
        return rootCandidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveDefaultCompatToolFromBlock(
        string compatBlock,
        string steamRoot,
        IReadOnlyList<string> steamLibraries,
        bool allowSystemCompatTools)
    {
        string? defaultBlock = ExtractVdfBlock(compatBlock, "0");
        if (string.IsNullOrEmpty(defaultBlock)) return null;

        var match = Regex.Match(defaultBlock, @"""name""\s+""([^""]*)""", RegexOptions.Singleline);
        if (!match.Success) return null;

        string toolName = match.Groups[1].Value.Trim();
        return string.IsNullOrEmpty(toolName)
            ? null
            : ResolveCompatToolName(toolName, steamRoot, steamLibraries, allowSystemCompatTools);
    }

    private static IReadOnlyList<string> GetSteamLibraryFolders(string steamRoot)
    {
        var libs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddLib(string p)
        {
            if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) return;
            string canon = SCSKiller.Linux.Core.AppPaths.GetCanonicalPath(p);
            if (seen.Add(canon)) libs.Add(p);
        }

        AddLib(steamRoot);
        foreach (var vdfPath in new[]
        {
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamRoot, "config", "libraryfolders.vdf")
        })
        {
            if (!File.Exists(vdfPath)) continue;
            try
            {
                string content = File.ReadAllText(vdfPath);
                foreach (Match m in Regex.Matches(content, @"""path""\s+""([^""]+)"""))
                {
                    if (m.Groups.Count > 1)
                        AddLib(m.Groups[1].Value.Replace(@"\\", "/"));
                }
            }
            catch { }
        }

        return libs;
    }

    public static string? TryReadProtonBinaryFromConfigInfo(string configInfoPath)
    {
        if (string.IsNullOrEmpty(configInfoPath) || !File.Exists(configInfoPath))
            return null;

        try
        {
            foreach (var line in File.ReadLines(configInfoPath))
            {
                int sepIdx = line.IndexOf("/files/", StringComparison.Ordinal);
                if (sepIdx < 0)
                    sepIdx = line.IndexOf("/dist/", StringComparison.Ordinal);

                if (sepIdx > 0)
                {
                    string protonRoot = line.Substring(0, sepIdx);
                    string protonBin = Path.Combine(protonRoot, "proton");
                    if (File.Exists(protonBin))
                        return protonBin;
                }
            }
        }
        catch { }

        return null;
    }

    public static bool DoesPrefixMatchProton(string compatDataDir, string protonPath)
    {
        if (string.IsNullOrEmpty(compatDataDir) || string.IsNullOrEmpty(protonPath))
            return false;

        string trimmed = compatDataDir.TrimEnd(Path.DirectorySeparatorChar);
        if (trimmed.EndsWith("/pfx", StringComparison.Ordinal) || trimmed.EndsWith("\\pfx", StringComparison.Ordinal))
            trimmed = Path.GetDirectoryName(trimmed) ?? trimmed;

        string? prefixProton = TryReadProtonBinaryFromConfigInfo(Path.Combine(trimmed, "config_info"));
        if (string.IsNullOrEmpty(prefixProton))
            return false;

        string canonPrefix = SCSKiller.Linux.Core.AppPaths.GetCanonicalPath(prefixProton);
        string canonTarget = SCSKiller.Linux.Core.AppPaths.GetCanonicalPath(protonPath);
        return string.Equals(canonPrefix, canonTarget, StringComparison.Ordinal);
    }

    /// <summary>Returns a lightweight signature of launcher Proton assignments (Steam's CompatToolMapping block +
    /// Heroic GamesConfig timestamps + Lutris game YAML timestamps) so the open GUI can detect Proton switches live.</summary>
    public static string GetLauncherProtonConfigSignature()
    {
        var sb = new System.Text.StringBuilder();

        foreach (var sr in GetAllSteamRoots())
        {
            string cfgVdf = Path.Combine(sr, "config", "config.vdf");
            if (!File.Exists(cfgVdf)) continue;
            try
            {
                long ticks = new FileInfo(cfgVdf).LastWriteTimeUtc.Ticks;
                string block;
                lock (_vdfCompatBlockLock)
                {
                    if (!_vdfCompatBlockCache.TryGetValue(cfgVdf, out var cached) || cached.LastWriteTicks != ticks)
                    {
                        string vdf = File.ReadAllText(cfgVdf);
                        block = ExtractVdfBlock(vdf, "CompatToolMapping") ?? "";
                        _vdfCompatBlockCache[cfgVdf] = (ticks, block);
                    }
                    else
                    {
                        block = cached.CompatBlock;
                    }
                }
                sb.Append("S:").Append(sr).Append('=').Append(block).Append(';');
            }
            catch { }
        }

        foreach (var hCfg in new[]
        {
            Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgConfigHome(HomeDir), "heroic", "GamesConfig"),
            Path.Combine(HomeDir, ".config", "heroic", "GamesConfig"),
            Path.Combine(HomeDir, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "GamesConfig")
        }.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(hCfg)) continue;
            try
            {
                long maxTicks = 0;
                int count = 0;
                foreach (var f in Directory.EnumerateFiles(hCfg, "*.json"))
                {
                    count++;
                    long t = new FileInfo(f).LastWriteTimeUtc.Ticks;
                    if (t > maxTicks) maxTicks = t;
                }
                sb.Append("H:").Append(hCfg).Append('=').Append(count).Append(':').Append(maxTicks).Append(';');
            }
            catch { }
        }

        return sb.ToString();
    }

    private static string? ExtractVdfBlock(string vdf, string blockName)
    {
        var match = Regex.Match(vdf, $@"""{Regex.Escape(blockName)}""\s*\{{", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        int startBrace = match.Index + match.Length - 1;
        int depth = 0;
        bool inQuotes = false;

        for (int i = startBrace; i < vdf.Length; i++)
        {
            char c = vdf[i];
            if (c == '"' && (i == 0 || vdf[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return vdf.Substring(startBrace, i - startBrace + 1);
                    }
                }
            }
        }

        return null;
    }

    public static string? ResolveCompatToolName(
        string toolName,
        string steamRoot,
        IReadOnlyList<string>? steamLibraries = null,
        bool allowSystemCompatTools = true)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;

        var libs = steamLibraries ?? GetSteamLibraryFolders(steamRoot);
        var compatToolsDirs = new List<string>
        {
            Path.Combine(steamRoot, "compatibilitytools.d")
        };

        if (allowSystemCompatTools)
        {
            compatToolsDirs.Add(Path.Combine(HomeDir, ".local", "share", "Steam", "compatibilitytools.d"));
            compatToolsDirs.Add(Path.Combine(HomeDir, ".steam", "root", "compatibilitytools.d"));
            compatToolsDirs.Add("/usr/share/steam/compatibilitytools.d");
            compatToolsDirs.Add("/usr/local/share/steam/compatibilitytools.d");
        }

        var distinctCompatDirs = compatToolsDirs.Distinct(StringComparer.Ordinal).ToList();

        // 1. Direct folder name match in compatibilitytools.d or steamapps/common
        var candidates = new List<string>();
        foreach (var cDir in distinctCompatDirs)
            candidates.Add(Path.Combine(cDir, toolName, "proton"));
        foreach (var lib in libs)
            candidates.Add(Path.Combine(lib, "steamapps", "common", toolName, "proton"));

        string? directHit = candidates.FirstOrDefault(File.Exists);
        if (directHit != null)
            return directHit;

        // 2. Scan compatibilitytool.vdf manifests (how Steam maps internal tool IDs like proton_experimental, proton_10, custom AUR tools)
        var vdfSearchRoots = new List<string>();
        foreach (var cDir in distinctCompatDirs)
        {
            if (!Directory.Exists(cDir)) continue;
            try { vdfSearchRoots.AddRange(Directory.GetDirectories(cDir)); } catch { }
        }
        foreach (var lib in libs)
        {
            string common = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            try { vdfSearchRoots.AddRange(Directory.GetDirectories(common, "Proton*")); } catch { }
        }

        foreach (var toolDir in vdfSearchRoots.Distinct(StringComparer.Ordinal))
        {
            string vdfFile = Path.Combine(toolDir, "compatibilitytool.vdf");
            if (!File.Exists(vdfFile)) continue;
            try
            {
                string vdfText = File.ReadAllText(vdfFile);
                string? toolsBlock = ExtractVdfBlock(vdfText, "compat_tools") ?? vdfText;
                string? matchedToolBlock = ExtractVdfBlock(toolsBlock, toolName);
                if (matchedToolBlock != null)
                {
                    var installMatch = Regex.Match(matchedToolBlock, @"""install_path""\s+""([^""]+)""");
                    string relInstall = installMatch.Success ? installMatch.Groups[1].Value : ".";
                    string resolvedProton = Path.GetFullPath(Path.Combine(toolDir, relInstall, "proton"));
                    if (File.Exists(resolvedProton))
                        return resolvedProton;
                }
            }
            catch { }
        }

        // 3. Built-in Valve Steam Play naming fallbacks across all Steam libraries
        var fallbackCandidates = new List<string>();
        var verMatch = Regex.Match(toolName, @"^proton_(\d+)", RegexOptions.IgnoreCase);
        foreach (var lib in libs)
        {
            string common = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common)) continue;

            if (toolName.Equals("proton_experimental", StringComparison.OrdinalIgnoreCase))
            {
                fallbackCandidates.Add(Path.Combine(common, "Proton - Experimental", "proton"));
            }
            else if (toolName.Equals("proton_hotfix", StringComparison.OrdinalIgnoreCase))
            {
                fallbackCandidates.Add(Path.Combine(common, "Proton Hotfix", "proton"));
            }
            else if (toolName.Equals("proton_next", StringComparison.OrdinalIgnoreCase))
            {
                fallbackCandidates.Add(Path.Combine(common, "Proton Next", "proton"));
            }
            else if (verMatch.Success)
            {
                string major = verMatch.Groups[1].Value;
                fallbackCandidates.Add(Path.Combine(common, $"Proton {major}.0 (Beta)", "proton"));
                fallbackCandidates.Add(Path.Combine(common, $"Proton {major}.0", "proton"));
                try
                {
                    foreach (var dir in Directory.GetDirectories(common, $"Proton {major}*").OrderByDescending(d => d))
                        fallbackCandidates.Add(Path.Combine(dir, "proton"));
                }
                catch { }
            }
        }

        return fallbackCandidates.FirstOrDefault(File.Exists);
    }

    public static string? FindProtonBinary(string? preferredVersion = null)
    {
        var candidates = new List<string>();
        var steamRoots = GetAllSteamRoots();

        // 1. User specified preferred Proton in compatibilitytools.d
        if (!string.IsNullOrEmpty(preferredVersion))
        {
            foreach (var sr in steamRoots)
            {
                candidates.Add(Path.Combine(sr, "compatibilitytools.d", preferredVersion, "proton"));
            }
            candidates.Add(Path.Combine("/usr/share/steam/compatibilitytools.d", preferredVersion, "proton"));
        }

        // 2. Custom GE-Proton / CachyOS Proton installations across Native & Flatpak Steam + Heroic tools
        foreach (var sr in steamRoots)
        {
            string userToolsDir = Path.Combine(sr, "compatibilitytools.d");
            if (Directory.Exists(userToolsDir))
            {
                foreach (var dir in Directory.GetDirectories(userToolsDir).OrderByDescending(d => d))
                {
                    candidates.Add(Path.Combine(dir, "proton"));
                }
            }
        }

        foreach (var heroicTools in new[]
        {
            Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgConfigHome(HomeDir), "heroic", "tools", "proton"),
            Path.Combine(HomeDir, ".config", "heroic", "tools", "proton"),
            Path.Combine(HomeDir, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "tools", "proton")
        }.Distinct(StringComparer.Ordinal))
        {
            if (Directory.Exists(heroicTools))
            {
                foreach (var dir in Directory.GetDirectories(heroicTools).OrderByDescending(d => d))
                {
                    candidates.Add(Path.Combine(dir, "proton"));
                }
            }
        }

        string sysToolsDir = "/usr/share/steam/compatibilitytools.d";
        if (Directory.Exists(sysToolsDir))
        {
            foreach (var dir in Directory.GetDirectories(sysToolsDir).OrderByDescending(d => d))
            {
                candidates.Add(Path.Combine(dir, "proton"));
            }
        }

        // 3. Official Steam Proton releases across Native & Flatpak Steam
        foreach (var sr in steamRoots)
        {
            string steamCommon = Path.Combine(sr, "steamapps", "common");
            if (Directory.Exists(steamCommon))
            {
                candidates.Add(Path.Combine(steamCommon, "Proton - Experimental", "proton"));
                candidates.Add(Path.Combine(steamCommon, "Proton 9.0 (Beta)", "proton"));
                candidates.Add(Path.Combine(steamCommon, "Proton 9.0", "proton"));
                candidates.Add(Path.Combine(steamCommon, "Proton 8.0", "proton"));
                foreach (var dir in Directory.GetDirectories(steamCommon, "Proton*").OrderByDescending(d => d))
                {
                    candidates.Add(Path.Combine(dir, "proton"));
                }
            }
        }

        // 4. System path proton
        candidates.Add("/usr/bin/proton");

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string? FindWarmerBinary()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Native", "shaderless_warm.exe"),
            Path.Combine(AppContext.BaseDirectory, "shaderless_warm.exe"),
            Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "bin", "shaderless_warm.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "SCSKiller.Linux.Core", "Native", "shaderless_warm.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public const long DefaultMaxNvidiaPartBytes = 1_850_000_000L;
    public const int MaxPipelinesPerWarmerBatch = 40_000;

    public static async Task<bool> RunWarmerAsync(
        GameInfo game,
        string workDir,
        Action<WarmerEvent> onEvent,
        CancellationToken cancellationToken = default,
        bool isIncrementalRun = false,
        int maxItemsPerBatch = MaxPipelinesPerWarmerBatch,
        long maxNvidiaPartBytes = DefaultMaxNvidiaPartBytes)
    {
        string recDbPath = Path.Combine(workDir, "shaderless.db");
        string genDbPath = Path.Combine(workDir, "shaderless_gen.db");

        if (maxItemsPerBatch > 0)
        {
            int totalItems = SCSKiller.Linux.Core.Games.RecorderManager.CountExecutableItems(recDbPath, genDbPath);
            if (totalItems > maxItemsPerBatch)
            {
                string batchRoot = Path.Combine(workDir, "_warmer_batches");
                var slices = SCSKiller.Linux.Core.Games.RecorderManager.SplitDatabasesIntoBatches(
                    recDbPath, genDbPath, batchRoot, maxItemsPerBatch);

                if (slices.Count > 1)
                {
                    string recBak = Path.Combine(workDir, "shaderless.db.full_bak");
                    string genBak = Path.Combine(workDir, "shaderless_gen.db.full_bak");
                    bool hadRec = File.Exists(recDbPath);
                    bool hadGen = File.Exists(genDbPath);

                    try
                    {
                        if (hadRec) File.Move(recDbPath, recBak, overwrite: true);
                        if (hadGen) File.Move(genDbPath, genBak, overwrite: true);

                        int grandTotal = slices.Sum(s => s.ExecutableItemCount);
                        int cumulativeProcessed = 0;
                        int cumulativeSucceeded = 0;
                        int cumulativeFailed = 0;
                        bool allOk = true;

                        for (int b = 0; b < slices.Count; b++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var slice = slices[b];
                            File.Copy(slice.BatchDbPath, recDbPath, overwrite: true);
                            if (File.Exists(genDbPath)) File.Delete(genDbPath);

                            int batchLastProcessed = 0;
                            int batchLastSucceeded = 0;
                            int batchLastFailed = 0;
                            bool isFirstBatch = (b == 0);
                            bool isLastBatch = (b == slices.Count - 1);

                            bool batchOk = await RunSingleWarmerPassAsync(
                                game,
                                workDir,
                                ev =>
                                {
                                    if (ev.Event == "init")
                                    {
                                        if (isFirstBatch) onEvent(ev);
                                        return;
                                    }
                                    if (ev.Event == "start")
                                    {
                                        if (isFirstBatch) onEvent(new WarmerEvent("start", Total: grandTotal));
                                        return;
                                    }
                                    if (ev.Event == "error")
                                    {
                                        onEvent(ev);
                                        return;
                                    }
                                    if (ev.Event == "progress")
                                    {
                                        int rawDone = ev.Done ?? 0;
                                        int adjProcessed = Math.Clamp(rawDone - slice.SkipPrefixCount, 0, slice.ExecutableItemCount);
                                        int adjFail = Math.Max(0, ev.Failed ?? 0);
                                        batchLastProcessed = adjProcessed;
                                        batchLastFailed = adjFail;
                                        batchLastSucceeded = Math.Max(0, adjProcessed - adjFail);
                                        onEvent(new WarmerEvent(
                                            "progress",
                                            Done: cumulativeProcessed + adjProcessed,
                                            Total: grandTotal,
                                            Failed: cumulativeFailed + adjFail));
                                        return;
                                    }
                                    if (ev.Event == "done")
                                    {
                                        int rawSucceeded = ev.Done ?? 0;
                                        int adjFail = Math.Max(0, ev.Failed ?? 0);
                                        int adjSucceeded = Math.Clamp(rawSucceeded - slice.SkipPrefixCount, 0, slice.ExecutableItemCount);
                                        batchLastSucceeded = adjSucceeded;
                                        batchLastFailed = adjFail;
                                        batchLastProcessed = Math.Min(slice.ExecutableItemCount, adjSucceeded + adjFail);
                                        return;
                                    }
                                },
                                cancellationToken,
                                isIncrementalRun: isFirstBatch ? isIncrementalRun : true,
                                startAt: slice.SkipPrefixCount,
                                maxNvidiaPartBytes: maxNvidiaPartBytes);

                            cumulativeProcessed += batchLastProcessed;
                            cumulativeSucceeded += batchLastSucceeded;
                            cumulativeFailed += batchLastFailed;

                            if (!batchOk)
                            {
                                allOk = false;
                                break;
                            }

                            if (!isLastBatch)
                            {
                                onEvent(new WarmerEvent(
                                    "progress",
                                    Done: cumulativeProcessed,
                                    Total: grandTotal,
                                    Failed: cumulativeFailed));
                            }
                        }

                        onEvent(new WarmerEvent(
                            "done",
                            Done: cumulativeSucceeded,
                            Total: grandTotal,
                            Failed: cumulativeFailed));

                        return allOk;
                    }
                    finally
                    {
                        try { if (File.Exists(recDbPath)) File.Delete(recDbPath); } catch { }
                        try { if (hadRec && File.Exists(recBak)) File.Move(recBak, recDbPath, overwrite: true); } catch { }
                        try { if (hadGen && File.Exists(genBak)) File.Move(genBak, genDbPath, overwrite: true); } catch { }
                        try { if (Directory.Exists(batchRoot)) Directory.Delete(batchRoot, recursive: true); } catch { }
                    }
                }
            }
        }

        return await RunSingleWarmerPassAsync(
            game,
            workDir,
            onEvent,
            cancellationToken,
            isIncrementalRun,
            startAt: 0,
            maxNvidiaPartBytes: maxNvidiaPartBytes);
    }

    private static async Task<bool> RunSingleWarmerPassAsync(
        GameInfo game,
        string workDir,
        Action<WarmerEvent> onEvent,
        CancellationToken cancellationToken,
        bool isIncrementalRun,
        int startAt,
        long maxNvidiaPartBytes)
    {
        string? warmerExe = FindWarmerBinary();
        if (warmerExe == null)
        {
            onEvent(new WarmerEvent("error", Message: "Native warmer executable (shaderless_warm.exe) not found."));
            return false;
        }

        string? protonPath = FindProtonBinaryForGame(game);
        if (protonPath == null)
        {
            onEvent(new WarmerEvent("error", Message: "Proton runtime not found on this system."));
            return false;
        }

        string compatClient = ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath);
        string rawId = game.RawId;
        string compatData;
        if (!string.IsNullOrEmpty(game.PrefixPath))
        {
            // Proton expects STEAM_COMPAT_DATA_PATH to be the parent of 'pfx' if 'pfx' is passed
            compatData = game.PrefixPath.EndsWith("/pfx", StringComparison.Ordinal) || game.PrefixPath.EndsWith("\\pfx", StringComparison.Ordinal)
                ? Path.GetDirectoryName(game.PrefixPath)!
                : game.PrefixPath;
        }
        else if (game.Source == GameSource.Steam)
        {
            compatData = Path.Combine(compatClient, "steamapps", "compatdata", rawId);
        }
        else
        {
            compatData = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "prefixes", game.Id);
        }

        string gameCompatData = compatData;
        bool prefixInitialized = File.Exists(Path.Combine(compatData, "pfx", "system.reg"))
            || File.Exists(Path.Combine(compatData, "system.reg"));
        bool prefixMatchesProton = prefixInitialized &&
            (!File.Exists(Path.Combine(compatData, "config_info")) || DoesPrefixMatchProton(compatData, protonPath));
        bool usingIsolatedProtonPrefix = false;
        if (!prefixInitialized || !prefixMatchesProton)
        {
            string protonKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(SCSKiller.Linux.Core.AppPaths.GetCanonicalPath(protonPath))))[..12].ToLowerInvariant();
            compatData = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "proton_prefix", protonKey);
            usingIsolatedProtonPrefix = true;
            prefixInitialized = File.Exists(Path.Combine(compatData, "pfx", "system.reg"))
                && (!File.Exists(Path.Combine(compatData, "config_info")) || DoesPrefixMatchProton(compatData, protonPath));
        }
        Directory.CreateDirectory(compatData);

        if (!prefixInitialized)
        {
            await InitializeProtonPrefixHeadlessAsync(protonPath, compatClient, compatData, workDir, cancellationToken);
        }

        // Target cache directory where driver will write compiled pipelines
        string targetCachePath = game.Source == GameSource.Steam
            ? GetSteamShaderCachePath(game.Id, game.InstallPath, game.PrefixPath)
            : GetNonSteamShaderCachePath(game.Id, game.PrefixPath);

        Directory.CreateDirectory(targetCachePath);

        string vkd3dCache = Path.Combine(targetCachePath, "VKD3D_shader_cache");
        string dxvkCache = Path.Combine(targetCachePath, "DXVK_state_cache");
        string nvidiaBase = Path.Combine(targetCachePath, "nvidiav1");
        Directory.CreateDirectory(nvidiaBase);
        string glCache = Path.Combine(nvidiaBase, "GLCache");
        Directory.CreateDirectory(vkd3dCache);
        Directory.CreateDirectory(dxvkCache);
        Directory.CreateDirectory(glCache);

        string amdCache = Path.Combine(targetCachePath, "AMDv1");
        Directory.CreateDirectory(amdCache);
        string mesaCache = targetCachePath;
        Directory.CreateDirectory(mesaCache);

        string exeName = !string.IsNullOrEmpty(game.ExecutablePath) && game.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(game.ExecutablePath)
            : "game.exe";

        // If the game ships with Agility SDK D3D12 directory, link it into workDir
        string? gameExeDir = !string.IsNullOrEmpty(game.ExecutablePath) ? Path.GetDirectoryName(game.ExecutablePath) : null;
        string workD3D12 = Path.Combine(workDir, "D3D12");
        if (!string.IsNullOrEmpty(gameExeDir))
        {
            string gameD3D12 = Path.Combine(gameExeDir, "D3D12");
            if (Directory.Exists(gameD3D12) && !Directory.Exists(workD3D12))
            {
                try
                {
                    Directory.CreateSymbolicLink(workD3D12, gameD3D12);
                }
                catch
                {
                    try { CopyDirectory(gameD3D12, workD3D12); } catch { }
                }
            }
        }

        // Ensure the warmer uses the exact same VKD3D-Proton and DXVK DLLs as the target Proton runtime / game prefix
        var stagedDlls = StageProtonRuntimeDlls(game, protonPath, gameCompatData, compatData, workDir);

        bool hasAntiCheat = SCSKiller.Linux.Core.Games.RecorderManager.DetectAntiCheat(game) != SCSKiller.Core.AntiCheat.None;

        // Prepare existing driver caches so incremental runs append to multi-part read-only caches while full runs start clean
        PrepareDriverCachesForWarmer(targetCachePath, vkd3dCache, glCache, protonPath, workDir, isIncrementalRun, hasAntiCheat ? null : gameExeDir, maxNvidiaPartBytes);

        // Copy warmer binary to match the game's executable name so VKD3D-Proton and drivers key the cache identically
        string targetExe = Path.Combine(workDir, exeName);
        try
        {
            File.Copy(warmerExe, targetExe, overwrite: true);
        }
        catch
        {
            targetExe = warmerExe;
        }

        int threads = Math.Max(2, Environment.ProcessorCount);
        // Always launch shaderless_warm.exe via "runinprefix" (direct wine invocation) rather than "run"
        // ("run" wraps via GUI steam.exe, causing Wine to spawn a visible conhost.exe window that steals stdout).
        const string protonVerb = "runinprefix";
        string startArg = startAt > 0 ? $" --start {startAt}" : "";
        var appSettings = SCSKiller.Linux.Core.SettingsManager.Load();
        string nvidiaReadOnlyNames = BuildNvidiaReadOnlyAppNames(glCache);

        var startInfo = new ProcessStartInfo
        {
            FileName = "nice",
            Arguments = $"-n 10 \"{protonPath}\" {protonVerb} \"{targetExe}\" \"{workDir}\" \"{exeName}\" --threads {threads}{startArg}",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.EnvironmentVariables["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = compatClient;
        startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"] = compatData;
        startInfo.EnvironmentVariables["STEAM_COMPAT_SHADER_PATH"] = targetCachePath;
        if (game.Source == GameSource.Steam)
        {
            startInfo.EnvironmentVariables["SteamAppId"] = rawId;
            startInfo.EnvironmentVariables["SteamGameId"] = rawId;
        }
        startInfo.EnvironmentVariables["WINEDEBUG"] = "-all";
        startInfo.EnvironmentVariables["WINEDLLOVERRIDES"] = "d3d12,d3d12core,dxgi,d3d11=n,b;winemenubuilder.exe=d;mscoree=d;mshtml=d";
        startInfo.EnvironmentVariables["PROTON_USE_XALIA"] = "0";
        startInfo.EnvironmentVariables["PROTONFIXES_DISABLE"] = "1";
        if (usingIsolatedProtonPrefix)
        {
            startInfo.EnvironmentVariables["PROTON_FSR4_UPGRADE"] = "0";
            startInfo.EnvironmentVariables["PROTON_DLSS_UPGRADE"] = "0";
            startInfo.EnvironmentVariables["PROTON_XESS_UPGRADE"] = "0";
            startInfo.EnvironmentVariables["PROTON_USE_OPTISCALER"] = "0";
        }
        if (IsLowLatencyConfigured(game, "PROTON_VKD3D_LOWLATENCY"))
            startInfo.EnvironmentVariables["PROTON_VKD3D_LOWLATENCY"] = "1";
        if (IsLowLatencyConfigured(game, "PROTON_DXVK_LOWLATENCY"))
            startInfo.EnvironmentVariables["PROTON_DXVK_LOWLATENCY"] = "1";
        startInfo.EnvironmentVariables["VKD3D_SHADER_CACHE_PATH"] = vkd3dCache;
        startInfo.EnvironmentVariables["DXVK_STATE_CACHE_PATH"] = dxvkCache;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_PATH"] = nvidiaBase;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE"] = "1";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_APP_NAME"] = "steamapp_shader_cache";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = nvidiaReadOnlyNames;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_SKIP_CLEANUP"] = "1";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_SIZE"] = appSettings.MaxCacheSizeBytes.ToString();
        startInfo.EnvironmentVariables["AMD_VK_PIPELINE_CACHE_PATH"] = amdCache;
        startInfo.EnvironmentVariables["AMD_VK_PIPELINE_CACHE_FILENAME"] = "steamapp_shader_cache";
        startInfo.EnvironmentVariables["AMD_VK_USE_PIPELINE_CACHE"] = "1";
        startInfo.EnvironmentVariables["MESA_SHADER_CACHE_DIR"] = mesaCache;
        startInfo.EnvironmentVariables["MESA_SHADER_CACHE_MAX_SIZE"] = appSettings.MesaMaxCacheSize;
        startInfo.EnvironmentVariables["MESA_GLSL_CACHE_DIR"] = mesaCache;
        startInfo.EnvironmentVariables["MESA_GLSL_CACHE_MAX_SIZE"] = appSettings.MesaMaxCacheSize;
        if (game.Source == GameSource.Steam)
        {
            startInfo.EnvironmentVariables["MESA_DISK_CACHE_SINGLE_FILE"] = "1";
        }

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            string line = e.Data.Trim();
            if (line.StartsWith('{') && line.EndsWith('}'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string ev = root.GetProperty("event").GetString() ?? "";

                    int? th = root.TryGetProperty("threads", out var thProp) ? thProp.GetInt32() : null;
                    int? done = root.TryGetProperty("done", out var dProp) ? dProp.GetInt32() : null;
                    int? total = root.TryGetProperty("total", out var tProp) ? tProp.GetInt32() : null;
                    int? failed = root.TryGetProperty("failed", out var fProp) ? fProp.GetInt32() : null;
                    string? msg = root.TryGetProperty("message", out var mProp) ? root.GetProperty("message").GetString() : null;

                    onEvent(new WarmerEvent(ev, th, done, total, failed, msg));
                }
                catch
                {
                    // Ignore non-json lines
                }
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            // Wine / Proton diagnostic logs
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
            });

            await process.WaitForExitAsync(cancellationToken);

            // Games with anti-cheat: leave the game's folder untouched (no vkd3d-proton.cache copied next to the exe)
            FinalizeDriverCaches(targetCachePath, vkd3dCache, glCache, hasAntiCheat ? null : gameExeDir, maxNvidiaPartBytes);
            InstallCachesWhereGameReads(game, vkd3dCache, glCache);

            try
            {
                EnsureProtonLocalShaderCacheSetting(gameCompatData, protonPath);
                if (!string.Equals(compatData, gameCompatData, StringComparison.Ordinal))
                {
                    EnsureProtonLocalShaderCacheSetting(compatData, protonPath);
                }
            }
            catch { }

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            onEvent(new WarmerEvent("error", Message: ex.Message));
            return false;
        }
        finally
        {
            try
            {
                if (targetExe != warmerExe && File.Exists(targetExe))
                    File.Delete(targetExe);
            }
            catch { }

            foreach (var dll in stagedDlls)
            {
                try { if (File.Exists(dll)) File.Delete(dll); } catch { }
            }
        }
    }

    /// <summary>
    /// Initializes or upgrades an isolated Proton prefix completely headlessly via "getcompatpath /".
    /// Proton's init_session(True) populates pfx/system.reg, config_info, and DXVK/VKD3D DLLs without
    /// launching steam.exe, avoiding any Wine conhost.exe or wineboot GUI popup windows.
    /// </summary>
    private static async Task InitializeProtonPrefixHeadlessAsync(
        string protonPath,
        string compatClient,
        string compatData,
        string workDir,
        CancellationToken cancellationToken)
    {
        var initInfo = new ProcessStartInfo
        {
            FileName = "nice",
            Arguments = $"-n 10 \"{protonPath}\" getcompatpath \"/\"",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        initInfo.EnvironmentVariables.Remove("DISPLAY");
        initInfo.EnvironmentVariables.Remove("WAYLAND_DISPLAY");
        initInfo.EnvironmentVariables["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = compatClient;
        initInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"] = compatData;
        initInfo.EnvironmentVariables["WINEDEBUG"] = "-all";
        initInfo.EnvironmentVariables["WINEDLLOVERRIDES"] = "winemenubuilder.exe=d;mscoree=d;mshtml=d";
        initInfo.EnvironmentVariables["PROTON_USE_XALIA"] = "0";
        initInfo.EnvironmentVariables["PROTONFIXES_DISABLE"] = "1";
        initInfo.EnvironmentVariables["PROTON_FSR4_UPGRADE"] = "0";
        initInfo.EnvironmentVariables["PROTON_DLSS_UPGRADE"] = "0";
        initInfo.EnvironmentVariables["PROTON_XESS_UPGRADE"] = "0";
        initInfo.EnvironmentVariables["PROTON_USE_OPTISCALER"] = "0";

        try
        {
            using var initProc = new Process { StartInfo = initInfo };
            initProc.OutputDataReceived += static (_, _) => { };
            initProc.ErrorDataReceived += static (_, _) => { };
            initProc.Start();
            initProc.BeginOutputReadLine();
            initProc.BeginErrorReadLine();

            using var reg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!initProc.HasExited)
                        initProc.Kill(entireProcessTree: true);
                }
                catch { }
            });

            await initProc.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort headless prefix setup; RunWarmerAsync will still attempt runinprefix.
        }
    }

    private static List<string> StageProtonRuntimeDlls(GameInfo game, string protonPath, string gameCompatData, string activeCompatData, string workDir)
    {
        var staged = new List<string>();
        string? protonDir = Path.GetDirectoryName(protonPath);
        if (string.IsNullOrEmpty(protonDir)) return staged;

        string? wineLib64 = new[]
        {
            Path.Combine(protonDir, "files", "lib64", "wine"),
            Path.Combine(protonDir, "dist", "lib64", "wine"),
            Path.Combine(protonDir, "files", "lib", "wine"),
            Path.Combine(protonDir, "dist", "lib", "wine")
        }.FirstOrDefault(Directory.Exists);

        string gameSys32 = Path.Combine(gameCompatData, "pfx", "drive_c", "windows", "system32");
        string activeSys32 = Path.Combine(activeCompatData, "pfx", "drive_c", "windows", "system32");

        bool useVkd3dLowLatency = IsLowLatencyConfigured(game, "PROTON_VKD3D_LOWLATENCY");
        bool useDxvkLowLatency = IsLowLatencyConfigured(game, "PROTON_DXVK_LOWLATENCY");

        string vkd3dSubDir = useVkd3dLowLatency && wineLib64 != null && Directory.Exists(Path.Combine(wineLib64, "vkd3d-low-latency", "x86_64-windows"))
            ? Path.Combine("vkd3d-low-latency", "x86_64-windows")
            : Path.Combine("vkd3d-proton", "x86_64-windows");

        string dxvkSubDir = useDxvkLowLatency && wineLib64 != null && Directory.Exists(Path.Combine(wineLib64, "dxvk-low-latency", "x86_64-windows"))
            ? Path.Combine("dxvk-low-latency", "x86_64-windows")
            : Path.Combine("dxvk", "x86_64-windows");

        var dllMappings = new (string DllName, string ProtonSubDir, bool PreferProtonSubDir)[]
        {
            ("d3d12.dll", vkd3dSubDir, useVkd3dLowLatency),
            ("d3d12core.dll", vkd3dSubDir, useVkd3dLowLatency),
            ("dxgi.dll", dxvkSubDir, useDxvkLowLatency),
            ("d3d11.dll", dxvkSubDir, useDxvkLowLatency)
        };

        foreach (var (dllName, subDir, preferProtonSubDir) in dllMappings)
        {
            string? sourceDll = null;

            // 1. If low-latency is explicitly configured, prefer the exact low-latency DLL from the Proton runtime
            if (preferProtonSubDir && wineLib64 != null)
            {
                string candidate = Path.Combine(wineLib64, subDir, dllName);
                if (File.Exists(candidate))
                    sourceDll = candidate;
            }

            // 2. Otherwise prefer the game prefix's own system32 DLL ONLY when the prefix already matches the selected Proton
            //    (if the user just switched Proton in Steam before launching the game, gameSys32 still holds the OLD Proton's DLLs!)
            bool prefixMatchesTargetProton = !File.Exists(Path.Combine(gameCompatData, "config_info"))
                                          || DoesPrefixMatchProton(gameCompatData, protonPath);
            if (sourceDll == null && prefixMatchesTargetProton && Directory.Exists(gameSys32))
            {
                string candidate = Path.Combine(gameSys32, dllName);
                var fi = new FileInfo(candidate);
                if (fi.Exists && fi.LinkTarget == null && fi.Length > 65536)
                    sourceDll = candidate;
            }

            // 3. Otherwise resolve from the target Proton runtime directory
            if (sourceDll == null && wineLib64 != null)
            {
                string candidate = Path.Combine(wineLib64, subDir, dllName);
                if (File.Exists(candidate))
                    sourceDll = candidate;
            }

            if (sourceDll == null) continue;

            // Keep active prefix system32 in sync when using the fallback shared prefix
            if (!string.Equals(activeCompatData, gameCompatData, StringComparison.Ordinal) && Directory.Exists(activeSys32))
            {
                try
                {
                    File.Copy(sourceDll, Path.Combine(activeSys32, dllName), overwrite: true);
                }
                catch { }
            }

            // Stage directly into workDir so Wine loads the exact matching DLL for shaderless_warm.exe
            string destDll = Path.Combine(workDir, dllName);
            if (!File.Exists(destDll))
            {
                try
                {
                    File.Copy(sourceDll, destDll, overwrite: false);
                    staged.Add(destDll);
                }
                catch { }
            }
        }

        return staged;
    }

    private static bool IsLowLatencyConfigured(GameInfo game, string envVarName)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(envVarName), "1", StringComparison.Ordinal))
            return true;

        var bgmodCandidates = new List<string>();
        if (!string.IsNullOrEmpty(game.Name))
        {
            bgmodCandidates.Add(Path.Combine(HomeDir, ".local", "share", "goverlay", "gameconfig", game.Name, "bgmod.conf"));
        }
        bgmodCandidates.Add(Path.Combine(HomeDir, ".local", "share", "goverlay", "gameconfig", "global", "bgmod.conf"));

        foreach (var conf in bgmodCandidates)
        {
            if (File.Exists(conf))
            {
                try
                {
                    string text = File.ReadAllText(conf);
                    if (Regex.IsMatch(text, $@"^\s*{Regex.Escape(envVarName)}\s*=\s*1\b", RegexOptions.Multiline))
                        return true;
                }
                catch { }
            }
        }

        return false;
    }

    public static string GetProtonStamp(GameInfo game)
    {
        try
        {
            string? protonPath = FindProtonBinaryForGame(game);
            if (string.IsNullOrEmpty(protonPath)) return "unknown";

            string? protonDir = Path.GetDirectoryName(protonPath);
            string? wineLib64 = protonDir == null ? null : new[]
            {
                Path.Combine(protonDir, "files", "lib64", "wine"),
                Path.Combine(protonDir, "dist", "lib64", "wine"),
                Path.Combine(protonDir, "files", "lib", "wine"),
                Path.Combine(protonDir, "dist", "lib", "wine")
            }.FirstOrDefault(Directory.Exists);

            long vkd3dLen = 0;
            long dxvkLen = 0;
            if (wineLib64 != null)
            {
                string d3d12Core = Path.Combine(wineLib64, "vkd3d-proton", "x86_64-windows", "d3d12core.dll");
                if (File.Exists(d3d12Core)) vkd3dLen = new FileInfo(d3d12Core).Length;

                string d3d11 = Path.Combine(wineLib64, "dxvk", "x86_64-windows", "d3d11.dll");
                if (File.Exists(d3d11)) dxvkLen = new FileInfo(d3d11).Length;
            }

            return $"{protonPath}|{vkd3dLen}|{dxvkLen}";
        }
        catch
        {
            return "unknown";
        }
    }

    public static bool IsRuntimeCacheStampValid(GameInfo game) =>
        GetRuntimeCacheStampMismatchReason(game) == null;

    /// <summary>
    /// Returns true if Shaderless itself has compiled and stamped shader caches for <paramref name="game"/>
    /// (distinguishing Shaderless pre-compilations from Steam's own downloaded or gameplay-generated shader caches).
    /// </summary>
    public static bool HasShaderlessCompiledArtifacts(GameInfo game, string? steamRoot = null)
    {
        try
        {
            string targetCachePath = game.Source == GameSource.Steam
                ? Path.Combine(steamRoot ?? ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath), "steamapps", "shadercache", game.RawId)
                : GetNonSteamShaderCachePath(game.Id, game.PrefixPath);

            if (!string.IsNullOrEmpty(targetCachePath) && Directory.Exists(targetCachePath))
            {
                if (File.Exists(Path.Combine(targetCachePath, "shaderless_runtime.stamp")) ||
                    File.Exists(Path.Combine(targetCachePath, "scskiller_runtime.stamp")) ||
                    File.Exists(Path.Combine(targetCachePath, InstalledManifest)))
                {
                    return true;
                }
            }

            if (File.Exists(SCSKiller.Linux.Core.Games.RecorderManager.WarmedKeysPath(game.Id)) ||
                File.Exists(Path.Combine(SCSKiller.Linux.Core.Games.RecorderManager.GameDir(game.Id), SCSKiller.Core.App.Recordings.WarmedFile)))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    public static string? GetRuntimeCacheStampMismatchReason(GameInfo game)
    {
        try
        {
            string targetCachePath = game.Source == GameSource.Steam
                ? GetSteamShaderCachePath(game.Id, game.InstallPath, game.PrefixPath)
                : GetNonSteamShaderCachePath(game.Id, game.PrefixPath);
            string stampFile = Path.Combine(targetCachePath, "shaderless_runtime.stamp");
            string legacyStampFile = Path.Combine(targetCachePath, "scskiller_runtime.stamp");
            if (!File.Exists(stampFile) && File.Exists(legacyStampFile))
            {
                try { File.Move(legacyStampFile, stampFile, overwrite: false); } catch { }
            }
            if (!File.Exists(stampFile)) return null;

            string? protonPath = FindProtonBinaryForGame(game);
            if (string.IsNullOrEmpty(protonPath)) return null;

            string? protonDir = Path.GetDirectoryName(protonPath);
            string? wineLib64 = protonDir == null ? null : new[]
            {
                Path.Combine(protonDir, "files", "lib64", "wine"),
                Path.Combine(protonDir, "dist", "lib64", "wine"),
                Path.Combine(protonDir, "files", "lib", "wine"),
                Path.Combine(protonDir, "dist", "lib", "wine")
            }.FirstOrDefault(Directory.Exists);

            bool useVkd3dLowLatency = IsLowLatencyConfigured(game, "PROTON_VKD3D_LOWLATENCY");
            string vkd3dSubDir = useVkd3dLowLatency && wineLib64 != null && Directory.Exists(Path.Combine(wineLib64, "vkd3d-low-latency", "x86_64-windows"))
                ? Path.Combine("vkd3d-low-latency", "x86_64-windows")
                : Path.Combine("vkd3d-proton", "x86_64-windows");

            string d3d12CoreSig = "default";
            if (wineLib64 != null)
            {
                string d3d12Core = Path.Combine(wineLib64, vkd3dSubDir, "d3d12core.dll");
                if (File.Exists(d3d12Core))
                    d3d12CoreSig = $"{new FileInfo(d3d12Core).Length}";
            }

            string nvidiaVer = File.Exists("/sys/module/nvidia/version")
                ? File.ReadAllText("/sys/module/nvidia/version").Trim()
                : "default";

            string baseStamp = $"{protonPath}|{d3d12CoreSig}";
            string currentStamp = $"{baseStamp}|{nvidiaVer}";
            string oldStamp = File.ReadAllText(stampFile).Trim();

            if (string.Equals(oldStamp, currentStamp, StringComparison.Ordinal) ||
                string.Equals(oldStamp, baseStamp, StringComparison.Ordinal))
            {
                return null;
            }

            var parts = oldStamp.Split('|');
            if (parts.Length >= 3)
            {
                string oldNvidia = parts[2];
                if (!string.Equals(oldNvidia, "default", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(nvidiaVer, "default", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(oldNvidia, nvidiaVer, StringComparison.Ordinal))
                {
                    return "driver updated";
                }
            }

            return "Proton updated";
        }
        catch
        {
            return null;
        }
    }

    public static string GetGameBuildStamp(GameInfo game)
    {
        try
        {
            long exeLen = 0;
            long exeTicks = 0;
            string exe = game.ExecutablePath;
            if (string.IsNullOrEmpty(exe) && Directory.Exists(game.InstallPath))
                exe = SCSKiller.Linux.Core.Games.GameEngineDetector.FindExecutable(game.InstallPath) ?? "";

            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var fi = new FileInfo(exe);
                exeLen = fi.Length;
                exeTicks = fi.LastWriteTimeUtc.Ticks;
            }

            long archiveCount = 0;
            long latestArchiveTicks = 0;
            long totalArchiveBytes = 0;
            if (!string.IsNullOrEmpty(game.InstallPath) && Directory.Exists(game.InstallPath))
            {
                var opts = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4
                };
                foreach (var file in Directory.EnumerateFiles(game.InstallPath, "*", opts))
                {
                    string ext = Path.GetExtension(file);
                    if (ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ucas", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".bundle", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".archive", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ba2", StringComparison.OrdinalIgnoreCase))
                    {
                        var afi = new FileInfo(file);
                        archiveCount++;
                        totalArchiveBytes += afi.Length;
                        if (afi.LastWriteTimeUtc.Ticks > latestArchiveTicks)
                            latestArchiveTicks = afi.LastWriteTimeUtc.Ticks;
                    }
                }
            }

            return $"{exeLen}:{exeTicks}|{archiveCount}:{totalArchiveBytes}:{latestArchiveTicks}";
        }
        catch
        {
            return "unknown";
        }
    }

    public static DateTime? GetLatestGameContentWriteUtc(string? gameBuildStamp)
    {
        if (string.IsNullOrEmpty(gameBuildStamp) || string.Equals(gameBuildStamp, "unknown", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            long maxTicks = 0;
            var parts = gameBuildStamp.Split('|');
            if (parts.Length >= 1)
            {
                var exeParts = parts[0].Split(':');
                if (exeParts.Length == 2 && long.TryParse(exeParts[1], out long exeTicks) && exeTicks > maxTicks)
                    maxTicks = exeTicks;
            }
            if (parts.Length >= 2)
            {
                var archParts = parts[1].Split(':');
                if (archParts.Length == 3 && long.TryParse(archParts[2], out long archTicks) && archTicks > maxTicks)
                    maxTicks = archTicks;
            }
            return maxTicks > 0 ? new DateTime(maxTicks, DateTimeKind.Utc) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static void PrepareDriverCachesForWarmer(
        string targetCachePath,
        string vkd3dCache,
        string glCache,
        string protonPath,
        string workDir,
        bool isIncrementalRun = false,
        string? gameExeDir = null,
        long maxNvidiaPartBytes = DefaultMaxNvidiaPartBytes)
    {
        try
        {
            string d3d12CorePath = Path.Combine(workDir, "d3d12core.dll");
            string d3d12CoreSig = File.Exists(d3d12CorePath)
                ? $"{new FileInfo(d3d12CorePath).Length}"
                : "default";
            string nvidiaVer = File.Exists("/sys/module/nvidia/version")
                ? File.ReadAllText("/sys/module/nvidia/version").Trim()
                : "default";
            string baseStamp = $"{protonPath}|{d3d12CoreSig}";
            string currentStamp = $"{baseStamp}|{nvidiaVer}";
            string stampFile = Path.Combine(targetCachePath, "shaderless_runtime.stamp");
            string legacyStampFile = Path.Combine(targetCachePath, "scskiller_runtime.stamp");
            if (!File.Exists(stampFile) && File.Exists(legacyStampFile))
            {
                try { File.Move(legacyStampFile, stampFile, overwrite: false); } catch { }
            }

            bool stampChanged = true;
            if (File.Exists(stampFile))
            {
                string oldStamp = File.ReadAllText(stampFile).Trim();
                if (string.Equals(oldStamp, currentStamp, StringComparison.Ordinal) ||
                    string.Equals(oldStamp, baseStamp, StringComparison.Ordinal))
                {
                    stampChanged = false;
                }
            }

            File.WriteAllText(stampFile, currentStamp);

            // A full compile (!isIncrementalRun) or runtime stamp change must start from a clean cache.
            bool resetCache = stampChanged || !isIncrementalRun;

            if (Directory.Exists(glCache))
            {
                var leafDirs = Directory.GetFiles(glCache, "*.bin", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToList();

                foreach (var dir in leafDirs)
                {
                    if (dir == null) continue;

                    if (resetCache)
                    {
                        foreach (var file in Directory.GetFiles(dir))
                        {
                            string fn = Path.GetFileName(file);
                            if (IsNvidiaReadOnlyCacheFileName(fn) ||
                                fn.StartsWith("steamapp_shader_cache", StringComparison.Ordinal))
                            {
                                try { File.Delete(file); } catch { }
                            }
                        }
                        continue;
                    }

                    // Incremental / subsequent batch run: pack any existing runtime steamapp_shader_cache*.bin buckets
                    // into deduplicated read-only parts (steamapp_merged_shader_cache + shaderless_part1..N) and leave
                    // writable buckets empty so shaderless_warm.exe has the full 1.85 GB headroom for new shaders while
                    // hitting all existing shaders lock-free via __GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME.
                    PackNvidiaGlCacheDirectory(dir, maxNvidiaPartBytes);
                }
            }

            if (Directory.Exists(vkd3dCache))
            {
                if (resetCache)
                {
                    foreach (var cacheFile in Directory.GetFiles(vkd3dCache, "*.cache*"))
                    {
                        try { File.Delete(cacheFile); } catch { }
                    }
                    if (!string.IsNullOrEmpty(gameExeDir) && Directory.Exists(gameExeDir))
                    {
                        try
                        {
                            string exeCache = Path.Combine(gameExeDir, "vkd3d-proton.cache");
                            if (File.Exists(exeCache)) File.Delete(exeCache);
                            string exeCacheWrite = Path.Combine(gameExeDir, "vkd3d-proton.cache.write");
                            if (File.Exists(exeCacheWrite)) File.Delete(exeCacheWrite);
                        }
                        catch { }
                    }
                }
                else
                {
                    foreach (var writeFile in Directory.GetFiles(vkd3dCache, "*.cache.write"))
                    {
                        if (new FileInfo(writeFile).Length > 48)
                        {
                            string finalCacheFile = writeFile.Substring(0, writeFile.Length - ".write".Length);
                            try
                            {
                                MergeOrReplaceVkd3dCache(writeFile, finalCacheFile);
                                File.Delete(writeFile);
                            }
                            catch { }
                        }
                    }
                }
            }

            if (resetCache && Directory.Exists(targetCachePath))
            {
                foreach (var subName in new[] { "DXVK_state_cache", "AMDv1", "mesa_shader_cache_sf", "mesa_shader_cache_db", "mesa_shader_cache" })
                {
                    string subDir = Path.Combine(targetCachePath, subName);
                    if (!Directory.Exists(subDir)) continue;
                    try
                    {
                        Directory.Delete(subDir, recursive: true);
                        if (subName is "DXVK_state_cache" or "AMDv1")
                            Directory.CreateDirectory(subDir);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public static void FinalizeDriverCaches(
        string targetCachePath,
        string vkd3dCache,
        string glCache,
        string? gameExeDir = null,
        long maxNvidiaPartBytes = DefaultMaxNvidiaPartBytes)
    {
        // 1. Flatten nested GLCache/GLCache if NVIDIA driver created one
        try
        {
            string nestedGlCache = Path.Combine(targetCachePath, "nvidiav1", "GLCache", "GLCache");
            if (Directory.Exists(nestedGlCache))
            {
                CopyDirectory(nestedGlCache, glCache);
                Directory.Delete(nestedGlCache, recursive: true);
            }
        }
        catch { }

        // 2. Pack NVIDIA per-process writable buckets (steamapp_shader_cache0/1/2.bin/.toc)
        // into lock-free, deduplicated, size-capped read-only caches (steamapp_merged_shader_cache + shaderless_part1..N).
        try
        {
            if (Directory.Exists(glCache))
            {
                var leafDirs = Directory.GetFiles(glCache, "*.bin", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToList();

                foreach (var dir in leafDirs)
                {
                    if (dir == null) continue;
                    PackNvidiaGlCacheDirectory(dir, maxNvidiaPartBytes);
                }
            }
        }
        catch { }

        // 3. Promote VKD3D-Proton .cache.write files into finalized .cache files and sync to gameExeDir
        try
        {
            if (Directory.Exists(vkd3dCache))
            {
                foreach (var writeFile in Directory.GetFiles(vkd3dCache, "*.cache.write"))
                {
                    var writeInfo = new FileInfo(writeFile);
                    if (writeInfo.Length == 0) continue;

                    string finalCacheFile = writeFile.Substring(0, writeFile.Length - ".write".Length);
                    try
                    {
                        MergeOrReplaceVkd3dCache(writeFile, finalCacheFile);
                        File.Delete(writeFile);
                    }
                    catch
                    {
                        try { File.Copy(writeFile, finalCacheFile, overwrite: true); } catch { }
                    }
                }

                if (!string.IsNullOrEmpty(gameExeDir) && Directory.Exists(gameExeDir))
                {
                    var largestCache = Directory.GetFiles(vkd3dCache, "*.cache")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Length)
                        .FirstOrDefault();

                    if (largestCache != null && largestCache.Length > 48)
                    {
                        try
                        {
                            string gameCachePath = Path.Combine(gameExeDir, "vkd3d-proton.cache");
                            MergeOrReplaceVkd3dCache(largestCache.FullName, gameCachePath);
                            // Keep the shadercache copy in sync with the merged gameExeDir copy so future incremental runs don't re-append
                            if (File.Exists(gameCachePath) && new FileInfo(gameCachePath).Length > largestCache.Length)
                            {
                                File.Copy(gameCachePath, largestCache.FullName, overwrite: true);
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    public static void FinalizeGameDriverCaches(GameInfo game)
    {
        try
        {
            string targetCachePath = game.Source == GameSource.Steam
                ? GetSteamShaderCachePath(game.Id, game.InstallPath, game.PrefixPath)
                : GetNonSteamShaderCachePath(game.Id, game.PrefixPath);
            if (string.IsNullOrEmpty(targetCachePath) || !Directory.Exists(targetCachePath))
                return;

            string vkd3dCache = Path.Combine(targetCachePath, "VKD3D_shader_cache");
            string glCache = Path.Combine(targetCachePath, "nvidiav1", "GLCache");
            bool hasAntiCheat = SCSKiller.Linux.Core.Games.RecorderManager.DetectAntiCheat(game) != SCSKiller.Core.AntiCheat.None;
            string? gameExeDir = !hasAntiCheat && !string.IsNullOrEmpty(game.ExecutablePath)
                ? Path.GetDirectoryName(game.ExecutablePath)
                : null;

            FinalizeDriverCaches(targetCachePath, vkd3dCache, glCache, gameExeDir);
            InstallCachesWhereGameReads(game, vkd3dCache, glCache);
        }
        catch { }
    }

    public static int TryParseShaderlessPartIndex(string pathOrName)
    {
        if (string.IsNullOrEmpty(pathOrName)) return -1;
        string name = Path.GetFileName(pathOrName);
        if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".toc", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        const string prefix = "shaderless_part";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length)
            return -1;

        ReadOnlySpan<char> digits = name.AsSpan(prefix.Length);
        for (int i = 0; i < digits.Length; i++)
        {
            if (digits[i] < '0' || digits[i] > '9')
                return -1;
        }

        return int.TryParse(digits, out int idx) && idx > 0 ? idx : -1;
    }

    public static bool IsNvidiaReadOnlyCacheFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (fileName.Equals("steam_shader_cache.bin", StringComparison.Ordinal) ||
            fileName.Equals("steam_shader_cache.toc", StringComparison.Ordinal) ||
            fileName.Equals("steamapp_merged_shader_cache.bin", StringComparison.Ordinal) ||
            fileName.Equals("steamapp_merged_shader_cache.toc", StringComparison.Ordinal))
        {
            return true;
        }

        return (fileName.EndsWith(".bin", StringComparison.Ordinal) || fileName.EndsWith(".toc", StringComparison.Ordinal))
            && TryParseShaderlessPartIndex(fileName) > 0;
    }

    public static string BuildNvidiaReadOnlyAppNames(string? glCacheRoot, string? existingEnvValue = null)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddName(string n)
        {
            if (!string.IsNullOrWhiteSpace(n) && seen.Add(n))
                names.Add(n);
        }

        if (!string.IsNullOrWhiteSpace(existingEnvValue))
        {
            foreach (var tok in existingEnvValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseShaderlessPartIndex(tok) < 0)
                    AddName(tok);
            }
        }

        AddName("steam_shader_cache");
        AddName("steamapp_merged_shader_cache");

        if (!string.IsNullOrEmpty(glCacheRoot) && Directory.Exists(glCacheRoot))
        {
            try
            {
                var partIndices = new SortedSet<int>();
                foreach (var binFile in Directory.EnumerateFiles(glCacheRoot, "shaderless_part*.bin", SearchOption.AllDirectories))
                {
                    int idx = TryParseShaderlessPartIndex(binFile);
                    if (idx > 0 && new FileInfo(binFile).Length > 64 && File.Exists(Path.ChangeExtension(binFile, ".toc")))
                    {
                        partIndices.Add(idx);
                    }
                }

                foreach (int idx in partIndices)
                    AddName($"shaderless_part{idx}");
            }
            catch { }
        }

        return string.Join(';', names);
    }

    /// <summary>
    /// Detects whether a game's NVIDIA GLCache hit NVIDIA's legacy single-file ~2.09 GiB signed 32-bit offset ceiling
    /// before multi-part cache splitting was introduced, so Shaderless can automatically flag and heal it on re-compile.
    /// </summary>
    public static bool IsNvidiaCacheTruncated(GameInfo game, string? steamRoot = null)
    {
        try
        {
            string targetCachePath = game.Source == GameSource.Steam
                ? Path.Combine(steamRoot ?? ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath), "steamapps", "shadercache", game.RawId)
                : GetNonSteamShaderCachePath(game.Id, game.PrefixPath);

            string glCache = Path.Combine(targetCachePath, "nvidiav1", "GLCache");
            if (!Directory.Exists(glCache)) return false;

            var leafDirs = Directory.GetFiles(glCache, "*.bin", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToList();

            foreach (var dir in leafDirs)
            {
                if (dir == null) continue;

                bool hasParts = Directory.EnumerateFiles(dir, "shaderless_part*.bin")
                    .Any(f => TryParseShaderlessPartIndex(f) > 0 && new FileInfo(f).Length > 64 && File.Exists(Path.ChangeExtension(f, ".toc")));

                foreach (var file in Directory.EnumerateFiles(dir, "*.bin"))
                {
                    string fn = Path.GetFileName(file);
                    if (!IsNvidiaReadOnlyCacheFileName(fn)) continue;
                    long len = new FileInfo(file).Length;
                    if (!hasParts && len >= 2_000_000_000L)
                        return true;
                    if (len >= 2_050_000_000L)
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    public static bool IsNvdcHeader(ReadOnlySpan<byte> tocBytes) =>
        tocBytes.Length >= 32 &&
        (tocBytes.Length - 32) % 24 == 0 &&
        ((tocBytes[0] == (byte)'C' && tocBytes[1] == (byte)'D' && tocBytes[2] == (byte)'V' && tocBytes[3] == (byte)'N') ||
         (tocBytes[0] == (byte)'N' && tocBytes[1] == (byte)'V' && tocBytes[2] == (byte)'D' && tocBytes[3] == (byte)'C'));

    [System.Runtime.InteropServices.DllImport("libc.so.6", EntryPoint = "link", SetLastError = true)]
    private static extern int HardLink(string existing, string newLink);

    [System.Runtime.InteropServices.DllImport("libc.so.6", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int dirfd,
        string pathname,
        int flags,
        uint mask,
        [System.Runtime.InteropServices.Out] byte[] statxbuf);

    [ThreadStatic]
    private static byte[]? t_statxBuf;

    private static bool TryGetLinuxFileIdentity(string filePath, out (uint DevMajor, uint DevMinor, ulong Ino) id, out long sizeBytes)
    {
        id = default;
        sizeBytes = 0;
        if (!OperatingSystem.IsLinux()) return false;

        try
        {
            byte[] buf = t_statxBuf ??= new byte[256];
            const int AT_FDCWD = -100;
            const uint STATX_INO_SIZE = 0x100U | 0x200U;
            if (Statx(AT_FDCWD, filePath, 0, STATX_INO_SIZE, buf) == 0)
            {
                ReadOnlySpan<byte> span = buf;
                ulong ino = BitConverter.ToUInt64(span.Slice(32, 8));
                ulong size = BitConverter.ToUInt64(span.Slice(40, 8));
                uint devMaj = BitConverter.ToUInt32(span.Slice(136, 4));
                uint devMin = BitConverter.ToUInt32(span.Slice(140, 4));
                if (ino != 0)
                {
                    id = (devMaj, devMin, ino);
                    sizeBytes = (long)size;
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    public static bool AreSameFileOnDisk(string pathA, string pathB)
    {
        if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB)) return false;
        if (!File.Exists(pathA) || !File.Exists(pathB)) return false;
        if (TryGetLinuxFileIdentity(pathA, out var idA, out _) &&
            TryGetLinuxFileIdentity(pathB, out var idB, out _))
        {
            return idA == idB;
        }
        return false;
    }

    /// <summary>Sums file sizes while deduplicating hard-linked files that share the same physical Linux inode.</summary>
    public static (long Bytes, int UniqueFiles) SumUniqueFileBytes(IEnumerable<string> filePaths)
    {
        long totalBytes = 0;
        int uniqueCount = 0;
        var seenInodes = new HashSet<(uint DevMajor, uint DevMinor, ulong Ino)>();

        foreach (var path in filePaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                if (TryGetLinuxFileIdentity(path, out var inode, out long statSize))
                {
                    if (seenInodes.Add(inode))
                    {
                        totalBytes += statSize;
                        uniqueCount++;
                    }
                }
                else if (File.Exists(path))
                {
                    totalBytes += new FileInfo(path).Length;
                    uniqueCount++;
                }
            }
            catch { }
        }

        return (totalBytes, uniqueCount);
    }

    /// <summary>Hard link (no extra disk space), else a copy.</summary>
    static void LinkOrCopy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) File.Delete(destination);
        bool linked = false;
        try { linked = HardLink(source, destination) == 0; } catch { }
        if (!linked) File.Copy(source, destination, overwrite: true);
    }

    /// <summary>Where a game Lutris starts reads its caches. Measured on a running Lutris game (Epic Games Store, UMU-Proton):
    /// vkd3d-proton: &lt;prefix&gt;/shadercache/VKD3D_shader_cache; NVIDIA: &lt;__GL_SHADER_DISK_CACHE_PATH&gt;/GLCache, which Lutris
    /// sets to the game's folder (the prefix for a launcher-installed game). The compile writes into a folder of its own
    /// (&lt;prefix&gt;/shadercache/&lt;id&gt;), so what it built is installed there. Other launchers are left as they are.</summary>
    const string InstalledManifest = "installed-files.txt";
    private static readonly string[] NonSteamDriverCacheSubDirs =
        { "AMDv1", "mesa_shader_cache_sf", "mesa_shader_cache_db", "mesa_shader_cache", "DXVK_state_cache" };

    internal static void InstallCachesWhereGameReads(GameInfo game, string vkd3dCache, string glCache)
    {
        var installed = new List<string>();
        try
        {
            string targetCachePath = Path.GetDirectoryName(vkd3dCache)!;
            string manifestPath = Path.Combine(targetCachePath, InstalledManifest);
            var previousInstalled = File.Exists(manifestPath)
                ? File.ReadAllLines(manifestPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                : new List<string>();

            void PruneRemovedNvidiaLinks()
            {
                if (previousInstalled.Count == 0) return;
                var currentSet = new HashSet<string>(installed, StringComparer.Ordinal);
                foreach (var oldPath in previousInstalled)
                {
                    if (!currentSet.Contains(oldPath) && IsNvidiaReadOnlyCacheFileName(Path.GetFileName(oldPath)))
                    {
                        try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
                    }
                }
            }

            // If this is a Flatpak game, also link the read-only NVIDIA caches into the Flatpak launcher's XDG_CACHE_HOME/nvidia/GLCache
            bool isFlatpak = game.IsFlatpak || IsFlatpakGame(game.Id, game.InstallPath, game.PrefixPath);
            if (isFlatpak && Directory.Exists(glCache))
            {
                string? fpGlCache = GetFlatpakLauncherNvidiaGlCachePath(game.Source);
                if (!string.IsNullOrEmpty(fpGlCache))
                {
                    foreach (var file in Directory.GetFiles(glCache, "*", SearchOption.AllDirectories)
                                 .Where(f => IsNvidiaReadOnlyCacheFileName(Path.GetFileName(f)) && new FileInfo(f).Length > 0))
                    {
                        string dest = Path.Combine(fpGlCache, Path.GetRelativePath(glCache, file));
                        try { LinkOrCopy(file, dest); installed.Add(dest); } catch { }
                    }
                }
            }

            if (game.Source == GameSource.Steam)
            {
                PruneRemovedNvidiaLinks();
                if (installed.Count > 0)
                {
                    File.WriteAllLines(manifestPath, installed.Distinct().ToList());
                }
                else if (File.Exists(manifestPath))
                {
                    try { File.Delete(manifestPath); } catch { }
                }
                return;
            }

            if (string.IsNullOrEmpty(game.PrefixPath)) return;
            string prefix = game.PrefixPath;
            string compatData = prefix.EndsWith("/pfx", StringComparison.Ordinal) || prefix.EndsWith("\\pfx", StringComparison.Ordinal)
                ? Path.GetDirectoryName(prefix)!
                : prefix;

            var shadercacheRoots = new HashSet<string>(StringComparer.Ordinal)
            {
                Path.Combine(compatData, "shadercache"),
                Path.Combine(prefix, "shadercache")
            };

            // 1. vkd3d-proton: the folder the game's Proton settings give it (see EnsureProtonLocalShaderCacheSetting)
            if (Directory.Exists(vkd3dCache))
            {
                foreach (var scRoot in shadercacheRoots)
                {
                    string gameVkd3d = Path.Combine(scRoot, "VKD3D_shader_cache");
                    Directory.CreateDirectory(gameVkd3d);
                    foreach (var cache in Directory.GetFiles(vkd3dCache, "*.cache").Where(f => new FileInfo(f).Length > 0))
                    {
                        string dest = Path.Combine(gameVkd3d, Path.GetFileName(cache));
                        try { MergeOrReplaceVkd3dCache(cache, dest); installed.Add(dest); } catch { }
                    }
                }
            }

            // 2. AMD / Mesa RADV (mesa_shader_cache_sf, mesa_shader_cache_db, mesa_shader_cache, AMDv1) & DXVK_state_cache:
            //    EnsureProtonLocalShaderCacheSetting points MESA_SHADER_CACHE_DIR at <compatData>/shadercache (and AMD_VK_PIPELINE_CACHE_PATH
            //    at <compatData>/shadercache/AMDv1), so link/copy the warmer's compiled files from <prefix>/shadercache/<gameId>/ into <scRoot>/.
            if (Directory.Exists(targetCachePath))
            {
                foreach (var subName in NonSteamDriverCacheSubDirs)
                {
                    string srcSub = Path.Combine(targetCachePath, subName);
                    if (!Directory.Exists(srcSub)) continue;

                    var files = Directory.GetFiles(srcSub, "*", SearchOption.AllDirectories)
                        .Where(f => new FileInfo(f).Length > 0)
                        .ToList();
                    if (files.Count == 0) continue;

                    foreach (var scRoot in shadercacheRoots)
                    {
                        string dstSub = Path.Combine(scRoot, subName);
                        foreach (var file in files)
                        {
                            string dest = Path.Combine(dstSub, Path.GetRelativePath(srcSub, file));
                            try { LinkOrCopy(file, dest); installed.Add(dest); } catch { }
                        }
                    }
                }
            }

            // 3. NVIDIA: <the game's __GL_SHADER_DISK_CACHE_PATH>/GLCache; Lutris sets that path to the prefix for a launcher-installed
            //    game and to the exe's folder for a game added by its exe; Heroic/Proton gets <compatData>/shadercache/nvidiav1.
            var nvidiaTargets = new HashSet<string>(StringComparer.Ordinal);
            string primaryBase = !string.IsNullOrEmpty(game.GlCachePath) ? game.GlCachePath
                : game.Source == GameSource.Heroic ? Path.Combine(compatData, "shadercache", "nvidiav1") : prefix;
            nvidiaTargets.Add(Path.Combine(primaryBase, "GLCache"));
            foreach (var scRoot in shadercacheRoots)
                nvidiaTargets.Add(Path.Combine(scRoot, "nvidiav1", "GLCache"));

            if (Directory.Exists(glCache))
            {
                foreach (var gameGl in nvidiaTargets)
                {
                    foreach (var file in Directory.GetFiles(glCache, "*", SearchOption.AllDirectories)
                                 .Where(f => IsNvidiaReadOnlyCacheFileName(Path.GetFileName(f)) && new FileInfo(f).Length > 0))
                    {
                        string dest = Path.Combine(gameGl, Path.GetRelativePath(glCache, file));
                        try { LinkOrCopy(file, dest); installed.Add(dest); } catch { }
                    }
                }
            }

            // Record every installed destination so Clear Cache can remove exact files cleanly
            PruneRemovedNvidiaLinks();
            File.WriteAllLines(manifestPath, installed.Distinct().ToList());
        }
        catch { }
    }

    /// <summary>Verifies that the compiled cache files and Proton settings actually landed where the game reads them.</summary>
    public static (bool Verified, string Detail) VerifyCacheInstalled(GameInfo game, string? steamRoot = null)
    {
        try
        {
            string compatClient = steamRoot ?? ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath);
            string rawId = game.RawId;
            string targetCachePath = game.Source == GameSource.Steam
                ? Path.Combine(compatClient, "steamapps", "shadercache", rawId)
                : GetNonSteamShaderCachePath(game.Id, game.PrefixPath);

            // 1. Check non-Steam installed manifest files if present
            if (game.Source != GameSource.Steam && !string.IsNullOrEmpty(game.PrefixPath))
            {
                string manifest = Path.Combine(targetCachePath, InstalledManifest);
                if (!File.Exists(manifest))
                    return (false, "installed cache manifest missing");

                var installedFiles = File.ReadAllLines(manifest).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                if (installedFiles.Count == 0 || installedFiles.Any(f => !File.Exists(f) || new FileInfo(f).Length == 0))
                    return (false, "installed cache files missing or empty in game prefix");

                string compatData = game.PrefixPath.EndsWith("/pfx", StringComparison.Ordinal) || game.PrefixPath.EndsWith("\\pfx", StringComparison.Ordinal)
                    ? Path.GetDirectoryName(game.PrefixPath)!
                    : game.PrefixPath;
                string settingsFile = Path.Combine(compatData, "user_settings.py");
                if (Directory.Exists(compatData) && !File.Exists(settingsFile))
                    return (false, "Proton user_settings.py missing in prefix");

                return (true, "cache verified");
            }

            // 2. Steam game (or prefixless fallback): check targetCachePath has non-empty VKD3D or driver cache files
            if (!Directory.Exists(targetCachePath))
                return (false, "shader cache folder missing");

            bool hasNonEmptyCache = Directory.EnumerateFiles(targetCachePath, "*", SearchOption.AllDirectories)
                .Any(f => IsCompiledShaderCacheFile(f) && new FileInfo(f).Length > 64);

            if (!hasNonEmptyCache)
                return (false, "no non-empty cache files found in shader cache folder");

            return (true, "cache verified");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Checks whether the game's executable is currently running in /proc/*/cmdline (excluding Shaderless's own warmer).</summary>
    public static bool IsGameRunning(GameInfo game, string procRoot = "/proc")
    {
        try
        {
            var exeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(game.ExecutablePath) && game.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exeNames.Add(Path.GetFileName(game.ExecutablePath));

            string? resolvedDir = SCSKiller.Linux.Core.Games.RecorderManager.ExeDir(game);
            if (!string.IsNullOrEmpty(resolvedDir) && Directory.Exists(resolvedDir))
            {
                foreach (var exe in Directory.EnumerateFiles(resolvedDir, "*.exe"))
                {
                    string name = Path.GetFileName(exe);
                    if (!name.Contains("CrashReport", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains(" Prerequisite", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("UEPrereq", StringComparison.OrdinalIgnoreCase))
                    {
                        exeNames.Add(name);
                    }
                }
            }

            if (exeNames.Count == 0 || !Directory.Exists(procRoot)) return false;

            string workRoot = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "work");

            foreach (var dir in Directory.EnumerateDirectories(procRoot))
            {
                string pidStr = Path.GetFileName(dir);
                if (!pidStr.All(char.IsDigit)) continue;

                string cmdlineFile = Path.Combine(dir, "cmdline");
                if (!File.Exists(cmdlineFile)) continue;

                string raw;
                try { raw = File.ReadAllText(cmdlineFile); }
                catch { continue; }

                if (string.IsNullOrEmpty(raw)) continue;

                // Ignore our own staged warmer inside ~/.local/share/shaderless/work/<id>/
                if (raw.Contains(workRoot, StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("/shaderless/work/", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("\\shaderless\\work\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var arg in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    string fileName = arg.Replace('\\', '/');
                    int slash = fileName.LastIndexOf('/');
                    if (slash >= 0) fileName = fileName[(slash + 1)..];
                    if (exeNames.Contains(fileName))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Removes what <see cref="InstallCachesWhereGameReads"/> put in the game's cache folders (called before the
    /// compile's own folder &lt;prefix&gt;/shadercache/&lt;id&gt; is deleted, which lists what was installed).</summary>
    public static void RemoveInstalledCaches(string prefix, string gameId)
    {
        try
        {
            string own = Path.Combine(prefix, "shadercache", gameId);
            string manifest = Path.Combine(own, InstalledManifest);
            if (File.Exists(manifest))
            {
                var deletedDirs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in File.ReadAllLines(manifest).Where(l => l.Length > 0))
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            string? parent = Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(parent))
                                deletedDirs.Add(parent);
                        }
                    }
                    catch { }
                }

                // Prune any empty leaf folders left behind inside shadercache
                foreach (var dir in deletedDirs.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                            Directory.Delete(dir, false);
                    }
                    catch { }
                }
                return;
            }

            // Fallback for compiles created before installed-files.txt existed
            string ownGl = Path.Combine(own, "nvidiav1", "GLCache");
            if (Directory.Exists(ownGl))
            {
                foreach (var file in Directory.GetFiles(ownGl, "*", SearchOption.AllDirectories)
                             .Where(f => IsNvidiaReadOnlyCacheFileName(Path.GetFileName(f))))
                    try { File.Delete(Path.Combine(prefix, "GLCache", Path.GetRelativePath(ownGl, file))); } catch { }
            }
            string ownVkd3d = Path.Combine(own, "VKD3D_shader_cache");
            if (Directory.Exists(ownVkd3d))
            {
                foreach (var cache in Directory.GetFiles(ownVkd3d, "*.cache"))
                    try { File.Delete(Path.Combine(prefix, "shadercache", "VKD3D_shader_cache", Path.GetFileName(cache))); } catch { }
            }
            foreach (var subName in NonSteamDriverCacheSubDirs)
            {
                string ownSub = Path.Combine(own, subName);
                if (!Directory.Exists(ownSub)) continue;
                foreach (var file in Directory.GetFiles(ownSub, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(Path.Combine(prefix, "shadercache", subName, Path.GetRelativePath(ownSub, file))); } catch { }
                }
            }
        }
        catch { }
    }

    private static byte[]? TryReadValidNvdcHeader(string tocPath)
    {
        if (!File.Exists(tocPath)) return null;
        try
        {
            long len = new FileInfo(tocPath).Length;
            if (len < 32 || (len - 32) % 24 != 0) return null;
            byte[] hdr = new byte[32];
            using var fs = new FileStream(tocPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = 0;
            while (read < 32)
            {
                int n = fs.Read(hdr, read, 32 - read);
                if (n <= 0) return null;
                read += n;
            }
            return IsNvdcHeader(hdr) ? hdr : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AddTocKeysToSet(byte[] tocBytes, HashSet<(ulong K0, ulong K1)> keys)
    {
        for (int pos = 32; pos + 24 <= tocBytes.Length; pos += 24)
        {
            ulong k0 = BitConverter.ToUInt64(tocBytes, pos);
            ulong k1 = BitConverter.ToUInt64(tocBytes, pos + 8);
            keys.Add((k0, k1));
        }
    }

    /// <summary>
    /// Packs all NVIDIA per-process writable buckets (<c>steamapp_shader_cache*.bin/.toc</c>) and any oversized or
    /// duplicate legacy read-only files in <paramref name="leafDir"/> into deduplicated, size-capped read-only slots
    /// (<c>steamapp_merged_shader_cache</c>, <c>shaderless_part1</c>, <c>shaderless_part2</c>, ...), keeping every
    /// <c>.bin</c> file strictly &lt;= <paramref name="maxPartBytes"/> (~1.72 GiB default, well below NVIDIA's 2 GiB
    /// signed 32-bit offset truncation limit).
    /// </summary>
    public static void PackNvidiaGlCacheDirectory(string leafDir, long maxPartBytes = DefaultMaxNvidiaPartBytes)
    {
        if (string.IsNullOrEmpty(leafDir) || !Directory.Exists(leafDir))
            return;

        if (maxPartBytes < 256)
            maxPartBytes = 256;

        var writableBins = Directory.GetFiles(leafDir, "steamapp_shader_cache*.bin")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Length)
            .ToList();

        // 1. Delete empty 32-byte stub writable buckets
        foreach (var stub in writableBins.Where(f => f.Length <= 64).ToList())
        {
            string stubToc = Path.ChangeExtension(stub.FullName, ".toc");
            try { stub.Delete(); } catch { }
            try { if (File.Exists(stubToc)) File.Delete(stubToc); } catch { }
            writableBins.Remove(stub);
        }

        string mergedBin = Path.Combine(leafDir, "steamapp_merged_shader_cache.bin");
        string mergedToc = Path.Combine(leafDir, "steamapp_merged_shader_cache.toc");
        string steamRoBin = Path.Combine(leafDir, "steam_shader_cache.bin");
        string steamRoToc = Path.Combine(leafDir, "steam_shader_cache.toc");

        // 2. Clean up legacy redundant hard-link between steam_shader_cache and steamapp_merged_shader_cache
        if (File.Exists(mergedBin) && File.Exists(steamRoBin) && AreSameFileOnDisk(mergedBin, steamRoBin))
        {
            try { File.Delete(steamRoBin); } catch { }
            try { if (File.Exists(steamRoToc)) File.Delete(steamRoToc); } catch { }
        }

        // 3. Separate non-NVDC opaque writable buckets (synthetic tests / non-NVDC formats) from real NVDC buckets
        var nvdcWritable = new List<FileInfo>();
        var nonNvdcWritable = new List<FileInfo>();
        foreach (var wb in writableBins)
        {
            string wtoc = Path.ChangeExtension(wb.FullName, ".toc");
            if (TryReadValidNvdcHeader(wtoc) != null)
                nvdcWritable.Add(wb);
            else if (File.Exists(wtoc))
                nonNvdcWritable.Add(wb);
            else
                try { wb.Delete(); } catch { }
        }

        if (nonNvdcWritable.Count > 0)
        {
            long existingMergedLen = File.Exists(mergedBin) ? new FileInfo(mergedBin).Length : 0;
            var primary = nonNvdcWritable[0];
            string primaryToc = Path.ChangeExtension(primary.FullName, ".toc");
            if (primary.Length >= existingMergedLen)
            {
                try
                {
                    File.Move(primary.FullName, mergedBin, overwrite: true);
                    File.Move(primaryToc, mergedToc, overwrite: true);
                }
                catch { }
            }

            if (nonNvdcWritable.Count > 1 && nvdcWritable.Count == 0)
            {
                var secondary = nonNvdcWritable[1];
                string secToc = Path.ChangeExtension(secondary.FullName, ".toc");
                long existingRoLen = File.Exists(steamRoBin) ? new FileInfo(steamRoBin).Length : 0;
                if (secondary.Length >= existingRoLen)
                {
                    try
                    {
                        File.Move(secondary.FullName, steamRoBin, overwrite: true);
                        File.Move(secToc, steamRoToc, overwrite: true);
                    }
                    catch { }
                }
            }

            foreach (var rem in nonNvdcWritable)
            {
                string rtoc = Path.ChangeExtension(rem.FullName, ".toc");
                try { if (File.Exists(rem.FullName)) File.Delete(rem.FullName); } catch { }
                try { if (File.Exists(rtoc)) File.Delete(rtoc); } catch { }
            }
        }

        // 4. Determine canonical 32-byte NVDC header from newest NVDC writable bucket or existing read-only NVDC files
        byte[]? canonicalHeader = null;
        foreach (var wb in nvdcWritable)
        {
            canonicalHeader = TryReadValidNvdcHeader(Path.ChangeExtension(wb.FullName, ".toc"));
            if (canonicalHeader != null) break;
        }
        canonicalHeader ??= TryReadValidNvdcHeader(mergedToc);
        if (canonicalHeader == null)
        {
            foreach (var partToc in Directory.EnumerateFiles(leafDir, "shaderless_part*.toc").OrderBy(TryParseShaderlessPartIndex))
            {
                canonicalHeader = TryReadValidNvdcHeader(partToc);
                if (canonicalHeader != null) break;
            }
        }
        canonicalHeader ??= TryReadValidNvdcHeader(steamRoToc);

        if (canonicalHeader == null)
            return;

        // Determine whether steamapp_merged_shader_cache is occupied by a non-NVDC file (e.g. synthetic test)
        bool mergedIsNonNvdc = File.Exists(mergedBin) && new FileInfo(mergedBin).Length > 64 && TryReadValidNvdcHeader(mergedToc) == null;

        // 5. Purge any NVDC read-only part from an older driver version (mismatched 32B header),
        //    and stage any oversized (> maxPartBytes) read-only files to be re-split cleanly.
        var sourcesToPack = new List<(string BinPath, string TocPath)>();
        var existingKeys = new HashSet<(ulong K0, ulong K1)>();

        void StageForRepack(string binPath, string tocPath)
        {
            string tmpTag = Path.Combine(leafDir, $".repack_{Guid.NewGuid():N}");
            string tmpBin = tmpTag + ".bin";
            string tmpToc = tmpTag + ".toc";
            File.Move(binPath, tmpBin, overwrite: true);
            File.Move(tocPath, tmpToc, overwrite: true);
            sourcesToPack.Add((tmpBin, tmpToc));
        }

        if (!mergedIsNonNvdc && File.Exists(mergedBin) && File.Exists(mergedToc))
        {
            byte[]? mHdr = TryReadValidNvdcHeader(mergedToc);
            long mLen = new FileInfo(mergedBin).Length;
            long mTocLen = new FileInfo(mergedToc).Length;
            if (mHdr == null || mLen <= 64 || !mHdr.AsSpan().SequenceEqual(canonicalHeader))
            {
                try { File.Delete(mergedBin); } catch { }
                try { File.Delete(mergedToc); } catch { }
            }
            else if (mLen > maxPartBytes && mTocLen >= 32 + 48)
            {
                StageForRepack(mergedBin, mergedToc);
            }
            else
            {
                AddTocKeysToSet(File.ReadAllBytes(mergedToc), existingKeys);
            }
        }

        var existingParts = Directory.GetFiles(leafDir, "shaderless_part*.bin")
            .Select(f => (Bin: f, Toc: Path.ChangeExtension(f, ".toc"), Index: TryParseShaderlessPartIndex(f)))
            .Where(p => p.Index > 0)
            .OrderBy(p => p.Index)
            .ToList();

        foreach (var p in existingParts)
        {
            byte[]? pHdr = TryReadValidNvdcHeader(p.Toc);
            long pLen = File.Exists(p.Bin) ? new FileInfo(p.Bin).Length : 0;
            long pTocLen = File.Exists(p.Toc) ? new FileInfo(p.Toc).Length : 0;
            if (pHdr == null || pLen <= 64 || !pHdr.AsSpan().SequenceEqual(canonicalHeader))
            {
                try { if (File.Exists(p.Bin)) File.Delete(p.Bin); } catch { }
                try { if (File.Exists(p.Toc)) File.Delete(p.Toc); } catch { }
            }
            else if (pLen > maxPartBytes && pTocLen >= 32 + 48)
            {
                StageForRepack(p.Bin, p.Toc);
            }
            else
            {
                AddTocKeysToSet(File.ReadAllBytes(p.Toc), existingKeys);
            }
        }

        // Check steam_shader_cache.bin/.toc:
        // - If oversized (> maxPartBytes) OR >= 50% duplicate of existingKeys (like legacy 2 GB duplicate), stage for repack & delete duplicate!
        // - Otherwise (distinct Steam cache <= maxPartBytes), preserve it and register its keys in existingKeys.
        bool useSteamRoAsPrimarySlot = false;
        if (File.Exists(steamRoBin) && File.Exists(steamRoToc))
        {
            byte[]? rHdr = TryReadValidNvdcHeader(steamRoToc);
            long rLen = new FileInfo(steamRoBin).Length;
            if (rHdr != null && rLen > 64)
            {
                if (!rHdr.AsSpan().SequenceEqual(canonicalHeader))
                {
                    try { File.Delete(steamRoBin); } catch { }
                    try { File.Delete(steamRoToc); } catch { }
                }
                else
                {
                    byte[] rTocBytes = File.ReadAllBytes(steamRoToc);
                    int totalRoKeys = (rTocBytes.Length - 32) / 24;
                    int overlap = 0;
                    if (existingKeys.Count > 0)
                    {
                        for (int pos = 32; pos + 24 <= rTocBytes.Length; pos += 24)
                        {
                            ulong k0 = BitConverter.ToUInt64(rTocBytes, pos);
                            ulong k1 = BitConverter.ToUInt64(rTocBytes, pos + 8);
                            if (existingKeys.Contains((k0, k1))) overlap++;
                        }
                    }

                    if ((rLen > maxPartBytes && rTocBytes.Length >= 32 + 48) ||
                        (existingKeys.Count > 0 && totalRoKeys > 0 && overlap * 2 >= totalRoKeys))
                    {
                        StageForRepack(steamRoBin, steamRoToc);
                    }
                    else
                    {
                        AddTocKeysToSet(rTocBytes, existingKeys);
                        if (mergedIsNonNvdc)
                            useSteamRoAsPrimarySlot = true;
                    }
                }
            }
        }

        foreach (var wb in nvdcWritable)
        {
            sourcesToPack.Add((wb.FullName, Path.ChangeExtension(wb.FullName, ".toc")));
        }

        if (sourcesToPack.Count == 0)
            return;

        // 6. O(1) fast-move when slot 0 (steamapp_merged_shader_cache) is empty, no parts exist yet, and first source fits in maxPartBytes
        string GetSlotBaseName(int slotIdx)
        {
            if (slotIdx == 0)
                return useSteamRoAsPrimarySlot ? "steam_shader_cache" : (mergedIsNonNvdc ? "shaderless_part1" : "steamapp_merged_shader_cache");
            int partNum = (useSteamRoAsPrimarySlot || !mergedIsNonNvdc) ? slotIdx : slotIdx + 1;
            return $"shaderless_part{partNum}";
        }

        string slot0Base = GetSlotBaseName(0);
        string slot0Bin = Path.Combine(leafDir, slot0Base + ".bin");
        string slot0Toc = Path.Combine(leafDir, slot0Base + ".toc");

        if (existingKeys.Count == 0 &&
            !File.Exists(slot0Bin) &&
            new FileInfo(sourcesToPack[0].BinPath).Length <= maxPartBytes)
        {
            var first = sourcesToPack[0];
            File.Move(first.BinPath, slot0Bin, overwrite: true);
            File.Move(first.TocPath, slot0Toc, overwrite: true);
            AddTocKeysToSet(File.ReadAllBytes(slot0Toc), existingKeys);
            sourcesToPack.RemoveAt(0);
            if (sourcesToPack.Count == 0)
                return;
        }

        // 7. Stream all remaining unique records across size-capped read-only slots (starting from slot 0 so parts stay 100% contiguous)
        int currentSlot = 0;

        FileStream? dstBinStream = null;
        FileStream? dstTocStream = null;
        long currentDstBinLen = 0;
        bool currentSlotNewlyCreated = false;
        string currentDstBinPath = "";
        string currentDstTocPath = "";

        void CloseCurrentSlot()
        {
            dstBinStream?.Dispose();
            dstTocStream?.Dispose();
            dstBinStream = null;
            dstTocStream = null;
            if (currentSlotNewlyCreated && currentDstBinLen <= 32)
            {
                try { if (File.Exists(currentDstBinPath)) File.Delete(currentDstBinPath); } catch { }
                try { if (File.Exists(currentDstTocPath)) File.Delete(currentDstTocPath); } catch { }
            }
        }

        void EnsureWritableSlotForRecord(long recordLen)
        {
            if (dstBinStream != null && dstTocStream != null)
            {
                if (currentDstBinLen <= 32 || currentDstBinLen + recordLen <= maxPartBytes)
                    return;

                CloseCurrentSlot();
                currentSlot++;
            }

            while (true)
            {
                string baseName = GetSlotBaseName(currentSlot);
                currentDstBinPath = Path.Combine(leafDir, baseName + ".bin");
                currentDstTocPath = Path.Combine(leafDir, baseName + ".toc");

                bool exists = File.Exists(currentDstBinPath) && File.Exists(currentDstTocPath) && new FileInfo(currentDstBinPath).Length >= 32;
                if (exists)
                {
                    long existingLen = new FileInfo(currentDstBinPath).Length;
                    if (existingLen > 32 && existingLen + recordLen > maxPartBytes)
                    {
                        currentSlot++;
                        continue;
                    }

                    dstBinStream = new FileStream(currentDstBinPath, FileMode.Append, FileAccess.Write, FileShare.None);
                    dstTocStream = new FileStream(currentDstTocPath, FileMode.Append, FileAccess.Write, FileShare.None);
                    currentDstBinLen = dstBinStream.Length;
                    currentSlotNewlyCreated = false;
                    return;
                }

                dstBinStream = new FileStream(currentDstBinPath, FileMode.Create, FileAccess.Write, FileShare.None);
                dstTocStream = new FileStream(currentDstTocPath, FileMode.Create, FileAccess.Write, FileShare.None);
                dstBinStream.Write(canonicalHeader, 0, 32);
                dstTocStream.Write(canonicalHeader, 0, 32);
                currentDstBinLen = 32;
                currentSlotNewlyCreated = true;
                return;
            }
        }

        byte[] tocEntryBuf = new byte[24];
        try
        {
            foreach (var (srcBin, srcToc) in sourcesToPack)
            {
                if (!File.Exists(srcBin) || !File.Exists(srcToc))
                    continue;

                byte[] srcTocBytes = File.ReadAllBytes(srcToc);
                long srcBinLen = new FileInfo(srcBin).Length;
                if (IsNvdcHeader(srcTocBytes) && srcTocBytes.AsSpan(0, 32).SequenceEqual(canonicalHeader))
                {
                    using var srcBinStream = new FileStream(srcBin, FileMode.Open, FileAccess.Read, FileShare.Read);
                    for (int pos = 32; pos + 24 <= srcTocBytes.Length; pos += 24)
                    {
                        ulong k0 = BitConverter.ToUInt64(srcTocBytes, pos);
                        ulong k1 = BitConverter.ToUInt64(srcTocBytes, pos + 8);
                        if (existingKeys.Contains((k0, k1)))
                            continue;

                        uint srcOff = BitConverter.ToUInt32(srcTocBytes, pos + 16);
                        uint payloadLen = BitConverter.ToUInt32(srcTocBytes, pos + 20);
                        long recordLen = 32L + payloadLen;

                        if (srcOff < 32 || srcOff + recordLen > srcBinLen || recordLen > 64 * 1024 * 1024)
                            continue;

                        byte[] recordBuf = new byte[recordLen];
                        srcBinStream.Position = srcOff;
                        int read = 0;
                        while (read < recordBuf.Length)
                        {
                            int n = srcBinStream.Read(recordBuf, read, recordBuf.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        if (read != recordBuf.Length)
                            continue;

                        // Verify record magic 0x9846a19d (9d a1 46 98) and payload length at offset 28
                        if (recordBuf[0] != 0x9d || recordBuf[1] != 0xa1 || recordBuf[2] != 0x46 || recordBuf[3] != 0x98)
                            continue;
                        if (BitConverter.ToUInt32(recordBuf, 28) != payloadLen)
                            continue;

                        EnsureWritableSlotForRecord(recordLen);

                        uint newOff = (uint)currentDstBinLen;
                        dstBinStream!.Write(recordBuf, 0, recordBuf.Length);
                        currentDstBinLen += recordBuf.Length;

                        Buffer.BlockCopy(srcTocBytes, pos, tocEntryBuf, 0, 16);
                        BitConverter.TryWriteBytes(tocEntryBuf.AsSpan(16, 4), newOff);
                        BitConverter.TryWriteBytes(tocEntryBuf.AsSpan(20, 4), payloadLen);
                        dstTocStream!.Write(tocEntryBuf, 0, 24);

                        existingKeys.Add((k0, k1));
                    }
                }

                try { File.Delete(srcBin); } catch { }
                try { File.Delete(srcToc); } catch { }
            }
        }
        finally
        {
            CloseCurrentSlot();
        }
    }

    public static void MergeOrPromoteNvidiaGlCache(
        string sourceBin,
        string sourceToc,
        string destBin,
        string destToc,
        long mergedBinLen = 0)
    {
        if (!File.Exists(sourceBin) || !File.Exists(sourceToc))
            return;

        long sourceBinLen = new FileInfo(sourceBin).Length;
        if (sourceBinLen <= 64)
            return;

        bool destExists = File.Exists(destBin) && File.Exists(destToc);
        long destBinLen = destExists ? new FileInfo(destBin).Length : 0;

        if (!destExists || destBinLen <= 64 || (mergedBinLen > 64 && destBinLen == mergedBinLen))
        {
            try { if (File.Exists(destBin)) File.Delete(destBin); } catch { }
            try { if (File.Exists(destToc)) File.Delete(destToc); } catch { }
            File.Move(sourceBin, destBin, overwrite: true);
            File.Move(sourceToc, destToc, overwrite: true);
            return;
        }

        byte[] srcTocBytes = File.ReadAllBytes(sourceToc);
        byte[] dstTocBytes = File.ReadAllBytes(destToc);

        bool validNvdc =
            IsNvdcHeader(srcTocBytes) &&
            IsNvdcHeader(dstTocBytes) &&
            srcTocBytes.AsSpan(0, 32).SequenceEqual(dstTocBytes.AsSpan(0, 32));

        if (!validNvdc)
        {
            if (sourceBinLen >= destBinLen)
            {
                try { File.Delete(destBin); } catch { }
                try { File.Delete(destToc); } catch { }
                File.Move(sourceBin, destBin, overwrite: true);
                File.Move(sourceToc, destToc, overwrite: true);
            }
            return;
        }

        var existingKeys = new HashSet<(ulong K0, ulong K1)>();
        AddTocKeysToSet(dstTocBytes, existingKeys);

        using (var srcBinStream = new FileStream(sourceBin, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var dstBinStream = new FileStream(destBin, FileMode.Append, FileAccess.Write, FileShare.None))
        using (var dstTocStream = new FileStream(destToc, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            long currentDstBinLen = dstBinStream.Length;
            byte[] tocEntryBuf = new byte[24];

            for (int pos = 32; pos + 24 <= srcTocBytes.Length; pos += 24)
            {
                ulong k0 = BitConverter.ToUInt64(srcTocBytes, pos);
                ulong k1 = BitConverter.ToUInt64(srcTocBytes, pos + 8);
                if (existingKeys.Contains((k0, k1)))
                    continue;

                uint srcOff = BitConverter.ToUInt32(srcTocBytes, pos + 16);
                uint payloadLen = BitConverter.ToUInt32(srcTocBytes, pos + 20);
                long recordLen = 32L + payloadLen;

                if (srcOff < 32 || srcOff + recordLen > sourceBinLen || recordLen > 64 * 1024 * 1024)
                    continue;
                if (currentDstBinLen + recordLen > DefaultMaxNvidiaPartBytes)
                    break;

                byte[] recordBuf = new byte[recordLen];
                srcBinStream.Position = srcOff;
                int read = 0;
                while (read < recordBuf.Length)
                {
                    int n = srcBinStream.Read(recordBuf, read, recordBuf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != recordBuf.Length)
                    continue;

                if (recordBuf[0] != 0x9d || recordBuf[1] != 0xa1 || recordBuf[2] != 0x46 || recordBuf[3] != 0x98)
                    continue;
                if (BitConverter.ToUInt32(recordBuf, 28) != payloadLen)
                    continue;

                uint newOff = (uint)currentDstBinLen;
                dstBinStream.Write(recordBuf, 0, recordBuf.Length);
                currentDstBinLen += recordBuf.Length;

                Buffer.BlockCopy(srcTocBytes, pos, tocEntryBuf, 0, 16);
                BitConverter.TryWriteBytes(tocEntryBuf.AsSpan(16, 4), newOff);
                BitConverter.TryWriteBytes(tocEntryBuf.AsSpan(20, 4), payloadLen);
                dstTocStream.Write(tocEntryBuf, 0, 24);

                existingKeys.Add((k0, k1));
            }
        }

        try { File.Delete(sourceBin); } catch { }
        try { File.Delete(sourceToc); } catch { }
    }

    public static void MergeOrReplaceVkd3dCache(string writeFile, string finalCacheFile)
    {
        if (!File.Exists(finalCacheFile))
        {
            File.Copy(writeFile, finalCacheFile, overwrite: true);
            return;
        }

        byte[] writeBytes = File.ReadAllBytes(writeFile);
        byte[] finalBytes = File.ReadAllBytes(finalCacheFile);

        if (writeBytes.Length <= 48) return;
        if (finalBytes.Length <= 48)
        {
            File.WriteAllBytes(finalCacheFile, writeBytes);
            return;
        }

        // Compare 48-byte vkd3d_pipeline_blob header (magic, version, vkd3d_build, vendor, device, driver_version, uuid)
        bool sameHeader = writeBytes.AsSpan(0, 48).SequenceEqual(finalBytes.AsSpan(0, 48));
        if (!sameHeader)
        {
            // Runtime / driver changed: replace with the fresh cache
            File.WriteAllBytes(finalCacheFile, writeBytes);
            return;
        }

        // Same header: VKD3D-Proton cache after byte 48 is a concatenation of stream chunks.
        if (writeBytes.AsSpan().SequenceEqual(finalBytes))
        {
            return;
        }

        // If writeBytes already starts with the entire finalBytes, only append the new tail
        if (writeBytes.Length > finalBytes.Length &&
            writeBytes.AsSpan(0, finalBytes.Length).SequenceEqual(finalBytes))
        {
            using var fs = new FileStream(finalCacheFile, FileMode.Append, FileAccess.Write, FileShare.None);
            fs.Write(writeBytes, finalBytes.Length, writeBytes.Length - finalBytes.Length);
            return;
        }

        // If finalBytes already ends with writeBytes's payload, it was already merged
        int payloadLen = writeBytes.Length - 48;
        if (finalBytes.Length >= 48 + payloadLen &&
            finalBytes.AsSpan(finalBytes.Length - payloadLen, payloadLen).SequenceEqual(writeBytes.AsSpan(48, payloadLen)))
        {
            return;
        }

        using (var fs = new FileStream(finalCacheFile, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            fs.Write(writeBytes, 48, payloadLen);
        }
    }

    public static string BuildProtonUserSettingsScript(AppSettings? settings = null, long maxNvidiaPartBytes = DefaultMaxNvidiaPartBytes)
    {
        var s = settings ?? SCSKiller.Linux.Core.SettingsManager.Load();
        int maxGb = s.EffectiveMaxCacheSizeGb;
        long maxBytes = s.MaxCacheSizeBytes;
        string mesaMax = s.MesaMaxCacheSize;
        long partLimit = maxNvidiaPartBytes >= 256 ? maxNvidiaPartBytes : DefaultMaxNvidiaPartBytes;

        return
            "import os, sys, struct, glob, shutil\n\n" +
            $"# shaderless-glcache-merge-v5 max_gb={maxGb}\n" +
            $"_MAX_PART_BYTES = {partLimit}\n" +
            "_shader_path = os.environ.get(\"STEAM_COMPAT_SHADER_PATH\", \"\")\n" +
            "_from_steam = bool(_shader_path)\n" +
            "if not _shader_path:\n" +
            "    _compat_data = os.environ.get(\"STEAM_COMPAT_DATA_PATH\", \"\").rstrip(\"/\\\\\")\n" +
            "    if _compat_data.endswith((\"pfx\",)) and os.path.basename(_compat_data) == \"pfx\":\n" +
            "        _compat_data = os.path.dirname(_compat_data)\n" +
            "    if _compat_data:\n" +
            "        _cand = os.path.join(_compat_data, \"shadercache\")\n" +
            "        if os.path.isdir(_cand):\n" +
            "            _shader_path = _cand\n" +
            "user_settings = {\n" +
            "    \"PROTON_LOCAL_SHADER_CACHE\": \"1\",\n" +
            "    \"__GL_SHADER_DISK_CACHE\": \"1\",\n" +
            $"    \"__GL_SHADER_DISK_CACHE_SIZE\": \"{maxBytes}\",\n" +
            "    \"__GL_SHADER_DISK_CACHE_SKIP_CLEANUP\": \"1\",\n" +
            $"    \"MESA_SHADER_CACHE_MAX_SIZE\": \"{mesaMax}\",\n" +
            $"    \"MESA_GLSL_CACHE_MAX_SIZE\": \"{mesaMax}\",\n" +
            "}\n" +
            "def _is_nvdc(b):\n" +
            "    return len(b) >= 32 and (len(b) - 32) % 24 == 0 and b[:4] in (b\"CDVN\", b\"NVDC\")\n" +
            "def _part_idx(name):\n" +
            "    if name.endswith((\".bin\", \".toc\")): name = name[:-4]\n" +
            "    if name.startswith(\"shaderless_part\") and name[15:].isdigit():\n" +
            "        v = int(name[15:])\n" +
            "        return v if v > 0 else -1\n" +
            "    return -1\n" +
            "def _merge_nv_runtime_buckets(gl_root):\n" +
            "    try:\n" +
            "        if not os.path.isdir(gl_root): return\n" +
            "        by_dir = {}\n" +
            "        for bpath in glob.glob(os.path.join(gl_root, \"**\", \"steamapp_shader_cache*.bin\"), recursive=True):\n" +
            "            by_dir.setdefault(os.path.dirname(bpath), []).append(bpath)\n" +
            "        for bdir, bpaths in by_dir.items():\n" +
            "            valid_wb = []\n" +
            "            for bp in sorted(bpaths, key=lambda p: -os.path.getsize(p) if os.path.exists(p) else 0):\n" +
            "                tp = bp[:-4] + \".toc\"\n" +
            "                blen = os.path.getsize(bp) if os.path.exists(bp) else 0\n" +
            "                if blen <= 64 or not os.path.exists(tp):\n" +
            "                    for p in (bp, tp):\n" +
            "                        try:\n" +
            "                            if os.path.exists(p): os.remove(p)\n" +
            "                        except Exception: pass\n" +
            "                    continue\n" +
            "                valid_wb.append((bp, tp, blen))\n" +
            "            if not valid_wb: continue\n" +
            "            mbin = os.path.join(bdir, \"steamapp_merged_shader_cache.bin\")\n" +
            "            mtoc = os.path.join(bdir, \"steamapp_merged_shader_cache.toc\")\n" +
            "            rbin = os.path.join(bdir, \"steam_shader_cache.bin\")\n" +
            "            rtoc = os.path.join(bdir, \"steam_shader_cache.toc\")\n" +
            "            if os.path.exists(mbin) and os.path.exists(rbin):\n" +
            "                try:\n" +
            "                    if os.path.samefile(mbin, rbin):\n" +
            "                        os.remove(rbin)\n" +
            "                        if os.path.exists(rtoc): os.remove(rtoc)\n" +
            "                except Exception: pass\n" +
            "            hdr = None\n" +
            "            for bp, tp, _ in valid_wb:\n" +
            "                with open(tp, \"rb\") as f: h = f.read(32)\n" +
            "                if len(h) == 32 and h[:4] in (b\"CDVN\", b\"NVDC\") and (os.path.getsize(tp) - 32) % 24 == 0:\n" +
            "                    hdr = h; break\n" +
            "            if hdr is None: continue\n" +
            "            keys = set()\n" +
            "            ro_tocs = [mtoc, rtoc] + sorted(\n" +
            "                [p for p in glob.glob(os.path.join(bdir, \"shaderless_part*.toc\")) if _part_idx(os.path.basename(p)) > 0],\n" +
            "                key=lambda p: _part_idx(os.path.basename(p)))\n" +
            "            for rt in ro_tocs:\n" +
            "                rb = rt[:-4] + \".bin\"\n" +
            "                if os.path.exists(rt) and os.path.exists(rb) and os.path.getsize(rb) > 64:\n" +
            "                    with open(rt, \"rb\") as f: dt = f.read()\n" +
            "                    if _is_nvdc(dt) and dt[:32] == hdr:\n" +
            "                        keys.update(dt[i:i+16] for i in range(32, len(dt), 24))\n" +
            "            if not keys and not os.path.exists(mbin) and valid_wb[0][2] <= _MAX_PART_BYTES:\n" +
            "                bp0, tp0, _ = valid_wb.pop(0)\n" +
            "                shutil.move(bp0, mbin)\n" +
            "                shutil.move(tp0, mtoc)\n" +
            "                with open(mtoc, \"rb\") as f: dt = f.read()\n" +
            "                if _is_nvdc(dt): keys.update(dt[i:i+16] for i in range(32, len(dt), 24))\n" +
            "            def _slot_name(s):\n" +
            "                return \"steamapp_merged_shader_cache\" if s == 0 else f\"shaderless_part{s}\"\n" +
            "            slot = 0\n" +
            "            db = dt = None\n" +
            "            dpos = 0\n" +
            "            created_new = False\n" +
            "            db_path = dt_path = \"\"\n" +
            "            def _close_slot():\n" +
            "                nonlocal db, dt, created_new\n" +
            "                if db: db.close(); db = None\n" +
            "                if dt: dt.close(); dt = None\n" +
            "                if created_new and dpos <= 32:\n" +
            "                    for p in (db_path, dt_path):\n" +
            "                        if p and os.path.exists(p): os.remove(p)\n" +
            "            def _ensure_slot(rlen_rec):\n" +
            "                nonlocal slot, db, dt, dpos, created_new, db_path, dt_path\n" +
            "                if db is not None and (dpos <= 32 or dpos + rlen_rec <= _MAX_PART_BYTES):\n" +
            "                    return\n" +
            "                if db is not None:\n" +
            "                    _close_slot()\n" +
            "                    slot += 1\n" +
            "                while True:\n" +
            "                    sn = _slot_name(slot)\n" +
            "                    db_path = os.path.join(bdir, sn + \".bin\")\n" +
            "                    dt_path = os.path.join(bdir, sn + \".toc\")\n" +
            "                    if os.path.exists(db_path) and os.path.exists(dt_path) and os.path.getsize(db_path) >= 32:\n" +
            "                        elen = os.path.getsize(db_path)\n" +
            "                        with open(dt_path, \"rb\") as fh: ehdr = fh.read(32)\n" +
            "                        if ehdr == hdr and (elen <= 32 or elen + rlen_rec <= _MAX_PART_BYTES):\n" +
            "                            db = open(db_path, \"ab\")\n" +
            "                            dt = open(dt_path, \"ab\")\n" +
            "                            dpos = db.tell()\n" +
            "                            created_new = False\n" +
            "                            return\n" +
            "                        slot += 1\n" +
            "                        continue\n" +
            "                    db = open(db_path, \"wb\")\n" +
            "                    dt = open(dt_path, \"wb\")\n" +
            "                    db.write(hdr)\n" +
            "                    dt.write(hdr)\n" +
            "                    dpos = 32\n" +
            "                    created_new = True\n" +
            "                    return\n" +
            "            try:\n" +
            "                for bp, tp, blen in valid_wb:\n" +
            "                    with open(tp, \"rb\") as f: stoc = f.read()\n" +
            "                    if _is_nvdc(stoc) and stoc[:32] == hdr:\n" +
            "                        with open(bp, \"rb\") as sb:\n" +
            "                            for i in range(32, len(stoc), 24):\n" +
            "                                k = stoc[i:i+16]\n" +
            "                                if k in keys: continue\n" +
            "                                off, plen = struct.unpack_from(\"<II\", stoc, i+16)\n" +
            "                                rlen_rec = 32 + plen\n" +
            "                                if off < 32 or off + rlen_rec > blen or rlen_rec > 67108864: continue\n" +
            "                                sb.seek(off)\n" +
            "                                rec = sb.read(rlen_rec)\n" +
            "                                if len(rec) == rlen_rec and rec[:4] == b\"\\x9d\\xa1\\x46\\x98\" and struct.unpack_from(\"<I\", rec, 28)[0] == plen:\n" +
            "                                    _ensure_slot(rlen_rec)\n" +
            "                                    db.write(rec)\n" +
            "                                    dt.write(k + struct.pack(\"<II\", dpos, plen))\n" +
            "                                    dpos += rlen_rec\n" +
            "                                    keys.add(k)\n" +
            "                    os.remove(bp)\n" +
            "                    os.remove(tp)\n" +
            "            finally:\n" +
            "                _close_slot()\n" +
            "    except Exception:\n" +
            "        pass\n" +
            "def _build_ro_names(gl_roots, existing_ro=\"\"):\n" +
            "    names = []\n" +
            "    seen = set()\n" +
            "    def _add(n):\n" +
            "        n = n.strip()\n" +
            "        if n and n not in seen:\n" +
            "            seen.add(n); names.append(n)\n" +
            "    for tok in (existing_ro or \"\").split(\";\"):\n" +
            "        if _part_idx(tok) < 0: _add(tok)\n" +
            "    _add(\"steam_shader_cache\")\n" +
            "    _add(\"steamapp_merged_shader_cache\")\n" +
            "    try:\n" +
            "        pidxs = set()\n" +
            "        for gl_root in gl_roots:\n" +
            "            if gl_root and os.path.isdir(gl_root):\n" +
            "                for p in glob.glob(os.path.join(gl_root, \"**\", \"shaderless_part*.bin\"), recursive=True):\n" +
            "                    pi = _part_idx(os.path.basename(p))\n" +
            "                    if pi > 0 and os.path.getsize(p) > 64 and os.path.exists(p[:-4] + \".toc\"):\n" +
            "                        pidxs.add(pi)\n" +
            "        for pi in sorted(pidxs):\n" +
            "            _add(f\"shaderless_part{pi}\")\n" +
            "    except Exception:\n" +
            "        pass\n" +
            "    return \";\".join(names)\n" +
            "_env_gl_base = os.environ.get(\"__GL_SHADER_DISK_CACHE_PATH\", \"\")\n" +
            "_env_gl_root = os.path.join(_env_gl_base, \"GLCache\") if _env_gl_base else \"\"\n" +
            "_gl_roots = []\n" +
            "if _shader_path:\n" +
            "    for _sub in (\"nvidiav1\", \"VKD3D_shader_cache\", \"DXVK_state_cache\", \"AMDv1\", \"mesa\"):\n" +
            "        try:\n" +
            "            os.makedirs(os.path.join(_shader_path, _sub), exist_ok=True)\n" +
            "        except Exception:\n" +
            "            pass\n" +
            "    _gl_root = os.path.join(_shader_path, \"nvidiav1\", \"GLCache\")\n" +
            "    _gl_roots.append(_gl_root)\n" +
            "    _merge_nv_runtime_buckets(_gl_root)\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_PATH\"] = os.path.join(_shader_path, \"nvidiav1\")\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_APP_NAME\"] = \"steamapp_shader_cache\"\n" +
            "    user_settings[\"VKD3D_SHADER_CACHE_PATH\"] = os.path.join(_shader_path, \"VKD3D_shader_cache\")\n" +
            "    user_settings[\"DXVK_STATE_CACHE_PATH\"] = os.path.join(_shader_path, \"DXVK_state_cache\")\n" +
            "    user_settings[\"AMD_VK_PIPELINE_CACHE_PATH\"] = os.path.join(_shader_path, \"AMDv1\")\n" +
            "    user_settings[\"AMD_VK_PIPELINE_CACHE_FILENAME\"] = \"steamapp_shader_cache\"\n" +
            "    user_settings[\"AMD_VK_USE_PIPELINE_CACHE\"] = \"1\"\n" +
            "    user_settings[\"MESA_SHADER_CACHE_DIR\"] = _shader_path\n" +
            "    user_settings[\"MESA_GLSL_CACHE_DIR\"] = _shader_path\n" +
            "    if _from_steam:\n" +
            "        user_settings[\"MESA_DISK_CACHE_SINGLE_FILE\"] = \"1\"\n" +
            "if _env_gl_root and _env_gl_root not in _gl_roots and os.path.isdir(_env_gl_root):\n" +
            "    _gl_roots.append(_env_gl_root)\n" +
            "    _merge_nv_runtime_buckets(_env_gl_root)\n" +
            "if _gl_roots:\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME\"] = _build_ro_names(_gl_roots, os.environ.get(\"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME\", \"\"))\n" +
            "try:\n" +
            "    _main = sys.modules.get(\"__main__\")\n" +
            "    _sess = getattr(_main, \"g_session\", None)\n" +
            "    if _sess is not None and hasattr(_sess, \"env\"):\n" +
            "        for _k in (\n" +
            "            \"__GL_SHADER_DISK_CACHE\",\n" +
            "            \"__GL_SHADER_DISK_CACHE_SIZE\",\n" +
            "            \"__GL_SHADER_DISK_CACHE_SKIP_CLEANUP\",\n" +
            "            \"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME\",\n" +
            "            \"MESA_SHADER_CACHE_MAX_SIZE\",\n" +
            "            \"MESA_GLSL_CACHE_MAX_SIZE\",\n" +
            "        ):\n" +
            "            if _k in user_settings:\n" +
            "                _sess.env.pop(_k, None)\n" +
            "except Exception:\n" +
            "    pass\n";
    }

    public static void RefreshInstalledProtonShaderCacheSettings(AppSettings? settings = null) =>
        EnsureProtonLocalShaderCacheSetting(string.Empty, null, settings);

    internal static void EnsureProtonLocalShaderCacheSetting(string compatData, string? protonPath = null, AppSettings? settings = null)
    {
        var effectiveSettings = settings ?? SCSKiller.Linux.Core.SettingsManager.Load();
        string versionTag = $"shaderless-glcache-merge-v5 max_gb={effectiveSettings.EffectiveMaxCacheSizeGb}";
        string settingsContent = BuildProtonUserSettingsScript(effectiveSettings);

        var targetDirs = new List<string>();
        if (!string.IsNullOrEmpty(compatData) && Directory.Exists(compatData))
        {
            targetDirs.Add(compatData);
        }

        // Only enumerate global Proton installations when not running under isolated test overrides without a real compatData
        if (string.IsNullOrEmpty(SCSKiller.Linux.Core.SettingsManager.TestSettingsPathOverride) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHADERLESS_SETTINGS_PATH")))
        {
            if (!string.IsNullOrEmpty(protonPath))
            {
                string? protonDir = Path.GetDirectoryName(protonPath);
                if (!string.IsNullOrEmpty(protonDir) && Directory.Exists(protonDir))
                    targetDirs.Add(protonDir);
            }

            foreach (var sr in GetAllSteamRoots())
            {
                string userCompatTools = Path.Combine(sr, "compatibilitytools.d");
                if (Directory.Exists(userCompatTools))
                {
                    targetDirs.AddRange(Directory.GetDirectories(userCompatTools));
                }

                string steamCommon = Path.Combine(sr, "steamapps", "common");
                if (Directory.Exists(steamCommon))
                {
                    foreach (var dir in Directory.GetDirectories(steamCommon, "Proton*"))
                    {
                        if (File.Exists(Path.Combine(dir, "proton")))
                            targetDirs.Add(dir);
                    }
                }
            }

            foreach (var launcherProtonRoot in new[]
            {
                Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgConfigHome(HomeDir), "heroic", "tools", "proton"),
                Path.Combine(HomeDir, ".config", "heroic", "tools", "proton"),
                Path.Combine(HomeDir, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "tools", "proton"),
                Path.Combine(SCSKiller.Linux.Core.AppPaths.GetXdgDataHome(HomeDir), "lutris", "runners", "proton"),
                Path.Combine(HomeDir, ".local", "share", "lutris", "runners", "proton"),
                Path.Combine(HomeDir, ".var", "app", "net.lutris.Lutris", "data", "lutris", "runners", "proton")
            }.Distinct(StringComparer.Ordinal))
            {
                if (!Directory.Exists(launcherProtonRoot)) continue;
                try
                {
                    foreach (var dir in Directory.GetDirectories(launcherProtonRoot))
                    {
                        if (File.Exists(Path.Combine(dir, "proton")))
                            targetDirs.Add(dir);
                    }
                }
                catch { }
            }
        }
        else if (!string.IsNullOrEmpty(protonPath))
        {
            string? protonDir = Path.GetDirectoryName(protonPath);
            if (!string.IsNullOrEmpty(protonDir) && Directory.Exists(protonDir))
                targetDirs.Add(protonDir);
        }

        foreach (var dir in targetDirs.Distinct())
        {
            try
            {
                string settingsFile = Path.Combine(dir, "user_settings.py");
                if (!File.Exists(settingsFile) || !File.ReadAllText(settingsFile).Contains(versionTag, StringComparison.Ordinal))
                {
                    File.WriteAllText(settingsFile, settingsContent);
                }
            }
            catch { }
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectory(dir, dest);
        }
    }
}
