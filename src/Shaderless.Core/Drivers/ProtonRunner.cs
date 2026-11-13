using Shaderless.Core.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shaderless.Core.Drivers;

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

        string xdgSteam = Path.Combine(Shaderless.Core.AppPaths.GetXdgDataHome(HomeDir), "Steam");
        string localSteam = Path.Combine(HomeDir, ".local", "share", "Steam");
        string dotSteam = Path.Combine(HomeDir, ".steam", "steam");

        foreach (var candidate in new[] { xdgSteam, localSteam, dotSteam, flatpakSteam })
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return localSteam;
    }

    public static IReadOnlyList<string> GetAllSteamRoots(string? homeDir = null, string? xdgDataHome = null)
    {
        string home = homeDir ?? HomeDir;
        var candidates = new[]
        {
            Path.Combine(Shaderless.Core.AppPaths.GetXdgDataHome(home, xdgDataHome), "Steam"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
        };

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (!Directory.Exists(c)) continue;
            string canonical = Shaderless.Core.AppPaths.GetCanonicalPath(c);
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
            string canonical = Shaderless.Core.AppPaths.GetCanonicalPath(sc);
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
        return Path.Combine(Shaderless.Core.AppPaths.DataDir, "prefixes", gameId, "shadercache", gameId);
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
            fileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("TOUCH", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public static string? FindProtonBinaryForGame(
        GameInfo game,
        string? preferredVersion = null,
        string? homeDir = null,
        string? xdgDataHome = null,
        string? xdgConfigHome = null)
    {
        string rawId = game.RawId;
        string home = homeDir ?? HomeDir;

        if (game.Source == GameSource.Steam && !string.IsNullOrEmpty(rawId))
        {
            if (homeDir == null && xdgDataHome == null)
            {
                string steamRoot = ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath);
                string? resolvedSteam = ResolveSteamProtonForApp(steamRoot, rawId, game.PrefixPath);
                if (!string.IsNullOrEmpty(resolvedSteam))
                    return resolvedSteam;
            }
            else
            {
                var steamRoots = GetAllSteamRoots(home, xdgDataHome).ToList();
                if (IsFlatpakGame(game.Id, game.InstallPath, game.PrefixPath))
                {
                    steamRoots = steamRoots
                        .OrderByDescending(r => r.Contains("/.var/app/", StringComparison.Ordinal))
                        .ToList();
                }

                foreach (var sr in steamRoots)
                {
                    string? resolvedSteam = ResolveSteamProtonForApp(sr, rawId, game.PrefixPath, allowSystemCompatTools: false, homeDir: home);
                    if (!string.IsNullOrEmpty(resolvedSteam))
                        return resolvedSteam;
                }
            }
        }
        else if (game.Source == GameSource.Heroic && !string.IsNullOrEmpty(rawId))
        {
            string xdgConfig = Shaderless.Core.AppPaths.GetXdgConfigHome(home, xdgConfigHome);
            var heroicDirs = game.IsFlatpak
                ? new[]
                {
                    Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"),
                    Path.Combine(xdgConfig, "heroic"),
                    Path.Combine(home, ".config", "heroic")
                }
                : new[]
                {
                    Path.Combine(xdgConfig, "heroic"),
                    Path.Combine(home, ".config", "heroic"),
                    Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic")
                };

            foreach (var hDir in heroicDirs.Distinct(StringComparer.Ordinal))
            {
                string gameCfg = Path.Combine(hDir, "GamesConfig", $"{rawId}.json");
                if (File.Exists(gameCfg))
                {
                    try
                    {
                        string? protonBin = Shaderless.Core.Games.HeroicLinuxSource.ReadProtonBinary(File.ReadAllText(gameCfg), rawId);
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
                        string? defaultProton = Shaderless.Core.Games.HeroicLinuxSource.ReadProtonBinary(File.ReadAllText(globalCfg), "defaultSettings");
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
            string? resolvedLutris = ResolveLutrisProtonForGame(
                game,
                preferredVersion,
                homeDir,
                xdgDataHome,
                xdgConfigHome,
                allowSystemCompatTools: homeDir == null);
            if (!string.IsNullOrEmpty(resolvedLutris))
                return resolvedLutris;
        }

        if (homeDir != null)
        {
            if (!string.IsNullOrEmpty(game.PrefixPath))
            {
                string trimmed = game.PrefixPath.TrimEnd(Path.DirectorySeparatorChar);
                if (trimmed.EndsWith("/pfx", StringComparison.Ordinal) || trimmed.EndsWith("\\pfx", StringComparison.Ordinal))
                    trimmed = Path.GetDirectoryName(trimmed) ?? trimmed;
                string? fromPrefix = TryReadProtonBinaryFromConfigInfo(Path.Combine(trimmed, "config_info"));
                if (!string.IsNullOrEmpty(fromPrefix))
                    return fromPrefix;
            }
            return null;
        }

        return FindProtonBinary(preferredVersion);
    }

    /// <summary>
    /// Resolves the Proton binary configured for a Lutris game directly from Lutris's YAML configs
    /// (`games/&lt;configpath&gt;.yml` → `runners/wine.yml`), falling back to `&lt;prefix&gt;/config_info` and Lutris runner directories
    /// only when no Proton version is explicitly configured in Lutris.
    /// </summary>
    public static string? ResolveLutrisProtonForGame(
        GameInfo game,
        string? preferredVersion = null,
        string? homeDir = null,
        string? xdgDataHome = null,
        string? xdgConfigHome = null,
        bool allowSystemCompatTools = true)
    {
        string home = homeDir ?? HomeDir;
        string xdgData = Shaderless.Core.AppPaths.GetXdgDataHome(home, xdgDataHome);

        var cfg = Shaderless.Core.Games.LutrisLinuxSource.TryReadGameRunnerConfig(
            game.Id,
            game.IsFlatpak,
            home,
            xdgDataHome,
            xdgConfigHome);

        var umuCompatDirs = new[]
        {
            Path.Combine(xdgData, "umu", "compatibilitytools"),
            Path.Combine(home, ".local", "share", "umu", "compatibilitytools")
        }.Distinct(StringComparer.Ordinal).ToList();

        var steamRoots = GetAllSteamRoots(home, xdgDataHome);

        List<string> lutrisRunnerDirs;
        if (cfg != null && cfg.RunnerRootDirs.Count > 0)
        {
            lutrisRunnerDirs = cfg.RunnerRootDirs.ToList();
        }
        else
        {
            lutrisRunnerDirs = new List<string>();
            foreach (var inst in Shaderless.Core.Games.LutrisLinuxSource
                         .GetCandidateInstances(home, xdgDataHome, xdgConfigHome)
                         .OrderByDescending(i => i.IsFlatpak == game.IsFlatpak))
            {
                lutrisRunnerDirs.Add(Path.Combine(inst.DataDir, "runners", "proton"));
                lutrisRunnerDirs.Add(Path.Combine(inst.DataDir, "runners", "wine"));
            }
            lutrisRunnerDirs = lutrisRunnerDirs.Distinct(StringComparer.Ordinal).ToList();
        }

        // 1. Explicit PROTONPATH override in Lutris system.env
        if (cfg != null && cfg.Env.TryGetValue("PROTONPATH", out var envProtonPath) && !string.IsNullOrWhiteSpace(envProtonPath))
        {
            string? fromEnv = ResolveLutrisProtonToken(envProtonPath.Trim(), lutrisRunnerDirs, umuCompatDirs, steamRoots, home, allowSystemCompatTools);
            if (!string.IsNullOrEmpty(fromEnv))
                return fromEnv;
        }

        // 2. Explicit custom Wine/Proton binary path (`version: custom` + `custom_wine_path: ...`)
        if (cfg != null &&
            string.Equals(cfg.Version, "custom", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cfg.CustomWinePath))
        {
            string? fromCustom = TryResolveProtonFromPath(ExpandHomePath(cfg.CustomWinePath, home));
            if (!string.IsNullOrEmpty(fromCustom))
                return fromCustom;
        }

        // 3. Explicit per-game or global Lutris runner version (`wine: version: ...`) ALWAYS wins over stale <prefix>/config_info
        if (cfg != null && !string.IsNullOrWhiteSpace(cfg.Version) &&
            !string.Equals(cfg.Version, "custom", StringComparison.OrdinalIgnoreCase))
        {
            string? fromVersion = ResolveLutrisProtonToken(cfg.Version.Trim(), lutrisRunnerDirs, umuCompatDirs, steamRoots, home, allowSystemCompatTools);
            if (!string.IsNullOrEmpty(fromVersion))
                return fromVersion;
        }

        // 4. Fallback to <prefix>/config_info when no explicit Proton runner version resolved from Lutris YAML
        if (!string.IsNullOrEmpty(game.PrefixPath))
        {
            string trimmedPrefix = game.PrefixPath.TrimEnd(Path.DirectorySeparatorChar);
            string? parentDir = Path.GetDirectoryName(trimmedPrefix);
            var cfgCandidates = new List<string> { Path.Combine(trimmedPrefix, "config_info") };
            if (!string.IsNullOrEmpty(parentDir))
                cfgCandidates.Add(Path.Combine(parentDir, "config_info"));

            foreach (var cfgCandidate in cfgCandidates.Distinct(StringComparer.Ordinal))
            {
                string? prefixProton = TryReadProtonBinaryFromConfigInfo(cfgCandidate);
                if (!string.IsNullOrEmpty(prefixProton))
                    return prefixProton;
            }
        }

        // 5. When Lutris has a DB entry with no explicit runner version and no initialized prefix yet, Lutris defaults to `ge-proton`
        if (cfg != null && string.IsNullOrWhiteSpace(cfg.Version))
        {
            string? defaultGe = ResolveLutrisProtonToken("ge-proton", lutrisRunnerDirs, umuCompatDirs, steamRoots, home, allowSystemCompatTools);
            if (!string.IsNullOrEmpty(defaultGe))
                return defaultGe;
        }

        // 6. Fallback to installed Lutris Proton/Wine-Proton runners sorted naturally (newest first)
        foreach (var rDir in lutrisRunnerDirs)
        {
            if (!Directory.Exists(rDir)) continue;
            var subDirs = Directory.GetDirectories(rDir).ToList();
            subDirs.Sort((a, b) => CompareNaturalDescending(Path.GetFileName(a), Path.GetFileName(b)));
            foreach (var dir in subDirs)
            {
                string p = Path.Combine(dir, "proton");
                if (File.Exists(p))
                    return p;
            }
        }

        return null;
    }

    private static string? ResolveLutrisProtonToken(
        string token,
        IReadOnlyList<string> lutrisRunnerDirs,
        IReadOnlyList<string> umuCompatDirs,
        IReadOnlyList<string> steamRoots,
        string home,
        bool allowSystemCompatTools)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        // Direct path or ~/... path
        if (token.Contains('/') || token.StartsWith('~'))
        {
            return TryResolveProtonFromPath(ExpandHomePath(token, home));
        }

        // Dynamic Lutris / UMU aliases (`ge-proton`, `GE-Proton`, `GE-Latest`, `UMU-Proton`, `UMU-Latest`)
        if (token.Equals("ge-proton", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("ge-latest", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("umu-proton", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("umu-latest", StringComparison.OrdinalIgnoreCase))
        {
            var prefixes = token.StartsWith("umu-", StringComparison.OrdinalIgnoreCase)
                ? new[] { "UMU-Proton", "GE-Proton" }
                : new[] { "GE-Proton", "UMU-Proton" };

            var searchDirs = new List<string>();
            searchDirs.AddRange(umuCompatDirs);
            foreach (var sr in steamRoots)
                searchDirs.Add(Path.Combine(sr, "compatibilitytools.d"));
            searchDirs.Add(Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"));
            searchDirs.AddRange(lutrisRunnerDirs);

            var distinctDirs = searchDirs.Distinct(StringComparer.Ordinal).Where(Directory.Exists).ToList();
            foreach (var prefix in prefixes)
            {
                foreach (var compatDir in distinctDirs)
                {
                    var matches = Directory.GetDirectories(compatDir)
                        .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                    File.Exists(Path.Combine(d, "proton")))
                        .ToList();
                    if (matches.Count > 0)
                    {
                        matches.Sort((a, b) => CompareNaturalDescending(Path.GetFileName(a), Path.GetFileName(b)));
                        return Path.Combine(matches[0], "proton");
                    }
                }
            }

            return null;
        }

        var candidateNames = new List<string> { token };
        if (token.EndsWith(" (Steam)", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = token[..^" (Steam)".Length].TrimEnd();
            if (!string.IsNullOrEmpty(stripped))
                candidateNames.Add(stripped);
        }

        foreach (var name in candidateNames)
        {
            // 1. Lutris's own runner directories (`settings.WINE_DIR` is checked first in Lutris `_iter_proton_locations`)
            foreach (var rDir in lutrisRunnerDirs)
            {
                string p = Path.Combine(rDir, name, "proton");
                if (File.Exists(p))
                    return p;
            }

            // 2. UMU compatibilitytools directory
            foreach (var uDir in umuCompatDirs)
            {
                string p = Path.Combine(uDir, name, "proton");
                if (File.Exists(p))
                    return p;
            }

            // 3. Steam compatibilitytools.d, steamapps/common, compatibilitytool.vdf aliases, and system compatibilitytools.d
            var effectiveSteamRoots = steamRoots.Count > 0
                ? steamRoots
                : new[] { Path.Combine(home, ".local", "share", "Steam") };

            foreach (var sr in effectiveSteamRoots)
            {
                string? resolved = ResolveCompatToolName(name, sr, null, allowSystemCompatTools, home);
                if (!string.IsNullOrEmpty(resolved))
                    return resolved;
            }
        }

        return null;
    }

    private static string ExpandHomePath(string path, string home)
    {
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(home, path[2..]);
        return path;
    }

    private static string? TryResolveProtonFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (File.Exists(path) && string.Equals(Path.GetFileName(path), "proton", StringComparison.OrdinalIgnoreCase))
            return path;

        if (Directory.Exists(path))
        {
            string directProton = Path.Combine(path, "proton");
            if (File.Exists(directProton))
                return directProton;
        }

        int sepIdx = path.IndexOf("/files/", StringComparison.Ordinal);
        if (sepIdx < 0)
            sepIdx = path.IndexOf("/dist/", StringComparison.Ordinal);
        if (sepIdx > 0)
        {
            string protonBin = Path.Combine(path[..sepIdx], "proton");
            if (File.Exists(protonBin))
                return protonBin;
        }

        return null;
    }

    internal static int CompareNaturalDescending(string? a, string? b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
        if (a == null) return 1;
        if (b == null) return -1;

        int ia = 0, ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            bool digitA = char.IsAsciiDigit(a[ia]);
            bool digitB = char.IsAsciiDigit(b[ib]);
            if (digitA && digitB)
            {
                int startA = ia;
                while (ia < a.Length && char.IsAsciiDigit(a[ia])) ia++;
                int startB = ib;
                while (ib < b.Length && char.IsAsciiDigit(b[ib])) ib++;

                long.TryParse(a.AsSpan(startA, ia - startA), out long numA);
                long.TryParse(b.AsSpan(startB, ib - startB), out long numB);
                int cmpNum = numB.CompareTo(numA);
                if (cmpNum != 0) return cmpNum;
            }
            else
            {
                char ca = char.ToLowerInvariant(a[ia]);
                char cb = char.ToLowerInvariant(b[ib]);
                if (ca != cb) return cb.CompareTo(ca);
                ia++;
                ib++;
            }
        }

        return b.Length.CompareTo(a.Length);
    }

    private static readonly Dictionary<string, (long LastWriteTicks, string CompatBlock)> _vdfCompatBlockCache = new(StringComparer.Ordinal);
    private static readonly object _vdfCompatBlockLock = new();

    public static string? ResolveSteamProtonForApp(
        string steamRoot,
        string rawId,
        string? prefixPath = null,
        bool allowSystemCompatTools = true,
        string? homeDir = null)
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
                        string? resolvedPerGame = ResolveCompatToolName(appToolName, steamRoot, steamLibraries, allowSystemCompatTools, homeDir);
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
            string? globalProton = ResolveDefaultCompatToolFromBlock(compatBlock, steamRoot, steamLibraries, allowSystemCompatTools, homeDir);
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
            string? globalProton = ResolveDefaultCompatToolFromBlock(compatBlock, steamRoot, steamLibraries, allowSystemCompatTools, homeDir);
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
        bool allowSystemCompatTools,
        string? homeDir = null)
    {
        string? defaultBlock = ExtractVdfBlock(compatBlock, "0");
        if (string.IsNullOrEmpty(defaultBlock)) return null;

        var match = Regex.Match(defaultBlock, @"""name""\s+""([^""]*)""", RegexOptions.Singleline);
        if (!match.Success) return null;

        string toolName = match.Groups[1].Value.Trim();
        return string.IsNullOrEmpty(toolName)
            ? null
            : ResolveCompatToolName(toolName, steamRoot, steamLibraries, allowSystemCompatTools, homeDir);
    }

    private static IReadOnlyList<string> GetSteamLibraryFolders(string steamRoot)
    {
        var libs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddLib(string p)
        {
            if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) return;
            string canon = Shaderless.Core.AppPaths.GetCanonicalPath(p);
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

        string canonPrefix = Shaderless.Core.AppPaths.GetCanonicalPath(prefixProton);
        string canonTarget = Shaderless.Core.AppPaths.GetCanonicalPath(protonPath);
        return string.Equals(canonPrefix, canonTarget, StringComparison.Ordinal);
    }

    /// <summary>Returns a lightweight signature of launcher Proton &amp; launch-env assignments (Steam's CompatToolMapping +
    /// userdata localconfig.vdf timestamps + Heroic GamesConfig/config.json timestamps + Lutris pga.db/game/runner/system YAML
    /// timestamps + Goverlay bgmod.conf timestamps) so the open GUI can detect Proton and launch-option switches live.</summary>
    public static string GetLauncherProtonConfigSignature(
        string? homeDir = null,
        string? xdgDataHome = null,
        string? xdgConfigHome = null)
    {
        string home = homeDir ?? HomeDir;
        string xdgData = Shaderless.Core.AppPaths.GetXdgDataHome(home, xdgDataHome);
        string xdgConfig = Shaderless.Core.AppPaths.GetXdgConfigHome(home, xdgConfigHome);
        var sb = new System.Text.StringBuilder();

        foreach (var sr in GetAllSteamRoots(home, xdgDataHome))
        {
            string cfgVdf = Path.Combine(sr, "config", "config.vdf");
            if (File.Exists(cfgVdf))
            {
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

            string userdataDir = Path.Combine(sr, "userdata");
            if (Directory.Exists(userdataDir))
            {
                try
                {
                    long localHash = 17;
                    int localCount = 0;
                    foreach (var userDir in Directory.EnumerateDirectories(userdataDir).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        string localVdf = Path.Combine(userDir, "config", "localconfig.vdf");
                        if (!File.Exists(localVdf)) continue;
                        localCount++;
                        var fi = new FileInfo(localVdf);
                        unchecked
                        {
                            localHash = localHash * 31 + StringComparer.Ordinal.GetHashCode(Path.GetFileName(userDir));
                            localHash = localHash * 31 + fi.LastWriteTimeUtc.Ticks;
                            localHash = localHash * 31 + fi.Length;
                        }
                    }
                    if (localCount > 0)
                        sb.Append("SU:").Append(sr).Append('=').Append(localCount).Append(':').Append(localHash).Append(';');
                }
                catch { }
            }
        }

        foreach (var hRoot in new[]
        {
            Path.Combine(xdgConfig, "heroic"),
            Path.Combine(home, ".config", "heroic"),
            Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic")
        }.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(hRoot)) continue;
            try
            {
                long globalTicks = 0;
                long globalLen = 0;
                string globalCfg = Path.Combine(hRoot, "config.json");
                if (File.Exists(globalCfg))
                {
                    var gfi = new FileInfo(globalCfg);
                    globalTicks = gfi.LastWriteTimeUtc.Ticks;
                    globalLen = gfi.Length;
                }

                long maxTicks = 0;
                long rolling = 17;
                int count = 0;
                string hCfg = Path.Combine(hRoot, "GamesConfig");
                if (Directory.Exists(hCfg))
                {
                    foreach (var f in Directory.EnumerateFiles(hCfg, "*.json").OrderBy(p => p, StringComparer.Ordinal))
                    {
                        count++;
                        var fi = new FileInfo(f);
                        long t = fi.LastWriteTimeUtc.Ticks;
                        if (t > maxTicks) maxTicks = t;
                        unchecked
                        {
                            rolling = rolling * 31 + StringComparer.Ordinal.GetHashCode(fi.Name);
                            rolling = rolling * 31 + t;
                            rolling = rolling * 31 + fi.Length;
                        }
                    }
                }
                sb.Append("H:").Append(hRoot).Append('=').Append(globalTicks).Append(':').Append(globalLen).Append(':')
                  .Append(count).Append(':').Append(maxTicks).Append(':').Append(rolling).Append(';');
            }
            catch { }
        }

        var seenLutrisDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inst in Shaderless.Core.Games.LutrisLinuxSource.GetCandidateInstances(home, xdgDataHome, xdgConfigHome))
        {
            foreach (var lDir in new[] { inst.DataDir, inst.ConfigDir })
            {
                if (!Directory.Exists(lDir)) continue;
                string canonDir = Shaderless.Core.AppPaths.GetCanonicalPath(lDir);
                if (!seenLutrisDirs.Add(canonDir)) continue;

                try
                {
                    long singleFilesHash = 17;
                    foreach (var relFile in new[] { "pga.db", "pga.db-wal", "system.yml", "lutris.conf" })
                    {
                        string fp = Path.Combine(lDir, relFile);
                        if (!File.Exists(fp)) continue;
                        var fi = new FileInfo(fp);
                        unchecked
                        {
                            singleFilesHash = singleFilesHash * 31 + StringComparer.Ordinal.GetHashCode(relFile);
                            singleFilesHash = singleFilesHash * 31 + fi.LastWriteTimeUtc.Ticks;
                            singleFilesHash = singleFilesHash * 31 + fi.Length;
                        }
                    }

                    long ymlHash = 17;
                    int ymlCount = 0;
                    foreach (var subFolder in new[] { "runners", "games" })
                    {
                        string subDir = Path.Combine(lDir, subFolder);
                        if (!Directory.Exists(subDir)) continue;
                        foreach (var f in Directory.EnumerateFiles(subDir, "*.yml").OrderBy(p => p, StringComparer.Ordinal))
                        {
                            ymlCount++;
                            var fi = new FileInfo(f);
                            unchecked
                            {
                                ymlHash = ymlHash * 31 + StringComparer.Ordinal.GetHashCode(subFolder);
                                ymlHash = ymlHash * 31 + StringComparer.Ordinal.GetHashCode(fi.Name);
                                ymlHash = ymlHash * 31 + fi.LastWriteTimeUtc.Ticks;
                                ymlHash = ymlHash * 31 + fi.Length;
                            }
                        }
                    }

                    sb.Append("L:").Append(canonDir).Append('=').Append(singleFilesHash).Append(':')
                      .Append(ymlCount).Append(':').Append(ymlHash).Append(';');
                }
                catch { }
            }
        }

        var seenGovDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var govRoot in new[]
        {
            Path.Combine(xdgData, "goverlay", "gameconfig"),
            Path.Combine(home, ".local", "share", "goverlay", "gameconfig")
        })
        {
            if (!Directory.Exists(govRoot)) continue;
            string canonGov = Shaderless.Core.AppPaths.GetCanonicalPath(govRoot);
            if (!seenGovDirs.Add(canonGov)) continue;

            try
            {
                long govHash = 17;
                int govCount = 0;
                foreach (var sub in Directory.EnumerateDirectories(govRoot).OrderBy(p => p, StringComparer.Ordinal))
                {
                    string bgConf = Path.Combine(sub, "bgmod.conf");
                    if (!File.Exists(bgConf)) continue;
                    govCount++;
                    var fi = new FileInfo(bgConf);
                    unchecked
                    {
                        govHash = govHash * 31 + StringComparer.Ordinal.GetHashCode(Path.GetFileName(sub));
                        govHash = govHash * 31 + fi.LastWriteTimeUtc.Ticks;
                        govHash = govHash * 31 + fi.Length;
                    }
                }
                if (govCount > 0)
                    sb.Append("G:").Append(canonGov).Append('=').Append(govCount).Append(':').Append(govHash).Append(';');
            }
            catch { }
        }

        return sb.ToString();
    }

    private static bool IsUnescapedVdfQuote(string vdf, int index)
    {
        if (index < 0 || index >= vdf.Length || vdf[index] != '"')
            return false;
        int backslashes = 0;
        for (int j = index - 1; j >= 0 && vdf[j] == '\\'; j--)
            backslashes++;
        return (backslashes & 1) == 0;
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
            if (c == '"' && IsUnescapedVdfQuote(vdf, i))
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
        bool allowSystemCompatTools = true,
        string? homeDir = null)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;

        string home = homeDir ?? HomeDir;
        var libs = steamLibraries ?? GetSteamLibraryFolders(steamRoot);
        var compatToolsDirs = new List<string>
        {
            Path.Combine(steamRoot, "compatibilitytools.d")
        };

        if (allowSystemCompatTools)
        {
            compatToolsDirs.Add(Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"));
            compatToolsDirs.Add(Path.Combine(home, ".steam", "root", "compatibilitytools.d"));
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
            Path.Combine(Shaderless.Core.AppPaths.GetXdgConfigHome(HomeDir), "heroic", "tools", "proton"),
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
            Path.Combine(Shaderless.Core.AppPaths.DataDir, "bin", "shaderless_warm.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Shaderless.Core", "Native", "shaderless_warm.exe")
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
        long maxNvidiaPartBytes = DefaultMaxNvidiaPartBytes,
        bool preserveExistingCache = false)
    {
        string recDbPath = Path.Combine(workDir, "shaderless.db");
        string genDbPath = Path.Combine(workDir, "shaderless_gen.db");

        if (maxItemsPerBatch > 0)
        {
            int totalItems = Shaderless.Core.Games.RecorderManager.CountExecutableItems(recDbPath, genDbPath);
            if (totalItems > maxItemsPerBatch)
            {
                string batchRoot = Path.Combine(workDir, "_warmer_batches");
                var slices = Shaderless.Core.Games.RecorderManager.SplitDatabasesIntoBatches(
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
                                maxNvidiaPartBytes: maxNvidiaPartBytes,
                                preserveExistingCache: preserveExistingCache);

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
            maxNvidiaPartBytes: maxNvidiaPartBytes,
            preserveExistingCache: preserveExistingCache);
    }

    private static async Task<bool> RunSingleWarmerPassAsync(
        GameInfo game,
        string workDir,
        Action<WarmerEvent> onEvent,
        CancellationToken cancellationToken,
        bool isIncrementalRun,
        int startAt,
        long maxNvidiaPartBytes,
        bool preserveExistingCache = false)
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
            compatData = Path.Combine(Shaderless.Core.AppPaths.DataDir, "prefixes", game.Id);
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
                System.Text.Encoding.UTF8.GetBytes(Shaderless.Core.AppPaths.GetCanonicalPath(protonPath))))[..12].ToLowerInvariant();
            compatData = Path.Combine(Shaderless.Core.AppPaths.DataDir, "proton_prefix", protonKey);
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

        string? resolvedExePath = Shaderless.Core.Games.GameEngineDetector.ResolveRealGameExecutable(game.InstallPath, game.ExecutablePath) ?? game.ExecutablePath;
        string exeName = !string.IsNullOrEmpty(resolvedExePath) && resolvedExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(resolvedExePath)
            : "game.exe";

        // If the game ships with Agility SDK D3D12 directory, link it into workDir
        string? gameExeDir = !string.IsNullOrEmpty(resolvedExePath) ? Path.GetDirectoryName(resolvedExePath) : null;
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

        // Resolve the game's effective launcher environment (Steam LaunchOptions / Heroic enviromentOptions / Lutris system.env / active bgmod.conf)
        var launcherEnv = GetEffectiveGameLauncherEnvironment(game);

        // Ensure the warmer uses the exact same VKD3D-Proton and DXVK DLLs as the target Proton runtime / game prefix
        var stagedDlls = StageProtonRuntimeDlls(game, protonPath, gameCompatData, compatData, workDir, launcherEnv);

        bool hasAntiCheat = Shaderless.Core.Games.RecorderManager.DetectAntiCheat(game) != Shaderless.Engine.AntiCheat.None;

        // Capture any existing NVIDIA Zstandard dictionary (_NVDICT_) and descriptor-promotion compiler mode before a full reset
        // deletes old cache files, so newly compiled shaders produce 100% matching (k0, k1) TOC keys and dictionary IDs.
        var nvidiaDonorRoots = GetNvidiaDictionaryDonorRoots(game, glCache, gameCompatData);
        var nvidiaDictSeed = CaptureNvidiaDictionarySeed(nvidiaDonorRoots);
        bool disableNvidiaPromo = ShouldDisableNvidiaDescriptorPromotion(game, glCache, nvidiaDonorRoots, gameCompatData);
        string? explicitNvPromo = null;
        if (launcherEnv.TryGetValue("__GL_0x9e7d4a", out string? rawNvPromo) && !string.IsNullOrWhiteSpace(rawNvPromo))
        {
            explicitNvPromo = rawNvPromo.Trim();
            disableNvidiaPromo = string.Equals(explicitNvPromo, "0", StringComparison.Ordinal);
        }

        string shaderCodegenEnvSuffix = BuildShaderCodegenEnvStampSuffix(launcherEnv);

        // Prepare existing driver caches so incremental runs append to multi-part read-only caches while full runs start clean
        PrepareDriverCachesForWarmer(
            targetCachePath,
            vkd3dCache,
            glCache,
            protonPath,
            workDir,
            isIncrementalRun,
            hasAntiCheat ? null : gameExeDir,
            maxNvidiaPartBytes,
            hasAntiCheat ? null : game.InstallPath,
            preserveExistingCache,
            disableNvidiaPromo,
            shaderCodegenEnvSuffix);

        // If PrepareDriverCachesForWarmer reset glCache (or glCache is brand-new), seed the captured _NVDICT_ so
        // libnvidia-glcore.so compresses every newly compiled shader with the matching dictionary ID.
        ApplyNvidiaDictionarySeed(glCache, nvidiaDictSeed);

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
        var appSettings = Shaderless.Core.SettingsManager.Load();
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
        if (IsVkd3dLowLatencyEnabled(launcherEnv))
            startInfo.EnvironmentVariables["PROTON_VKD3D_LOWLATENCY"] = "1";
        if (IsDxvkLowLatencyEnabled(launcherEnv))
            startInfo.EnvironmentVariables["PROTON_DXVK_LOWLATENCY"] = "1";
        if (IsDxvkSarekEnabled(launcherEnv))
            startInfo.EnvironmentVariables["PROTON_DXVK_SAREK"] = "1";
        if (IsWineD3dEnabled(launcherEnv))
            startInfo.EnvironmentVariables["PROTON_USE_WINED3D"] = "1";
        if (IsVkd3dHeapEnabled(launcherEnv))
            startInfo.EnvironmentVariables["PROTON_VKD3D_HEAP"] = "1";
        foreach (var (codegenKey, codegenVal) in GetWarmerShaderEnvPairs(launcherEnv))
        {
            startInfo.EnvironmentVariables[codegenKey] = codegenVal;
        }
        startInfo.EnvironmentVariables["VKD3D_SHADER_CACHE_PATH"] = vkd3dCache;
        startInfo.EnvironmentVariables["DXVK_STATE_CACHE_PATH"] = dxvkCache;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_PATH"] = nvidiaBase;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE"] = "1";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_APP_NAME"] = "steamapp_shader_cache";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = nvidiaReadOnlyNames;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_SKIP_CLEANUP"] = "1";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_SIZE"] = appSettings.MaxCacheSizeBytes.ToString();
        if (explicitNvPromo != null)
        {
            startInfo.EnvironmentVariables["__GL_0x9e7d4a"] = explicitNvPromo;
        }
        else if (disableNvidiaPromo)
        {
            startInfo.EnvironmentVariables["__GL_0x9e7d4a"] = "0";
        }
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
            FinalizeDriverCaches(targetCachePath, vkd3dCache, glCache, hasAntiCheat ? null : gameExeDir, maxNvidiaPartBytes, hasAntiCheat ? null : game.InstallPath);
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

    internal static List<string> StageProtonRuntimeDlls(
        GameInfo game,
        string protonPath,
        string gameCompatData,
        string activeCompatData,
        string workDir,
        IReadOnlyDictionary<string, string>? launcherEnv = null)
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

        var env = launcherEnv ?? GetEffectiveGameLauncherEnvironment(game);
        string vkd3dSubDir = ResolveProtonVkd3dSubDir(wineLib64, env);
        string dxvkSubDir = ResolveProtonDxvkSubDir(wineLib64, env);

        var dllMappings = new (string DllName, string ProtonSubDir)[]
        {
            ("d3d12.dll", vkd3dSubDir),
            ("d3d12core.dll", vkd3dSubDir),
            ("dxgi.dll", dxvkSubDir),
            ("d3d11.dll", dxvkSubDir)
        };

        foreach (var (dllName, subDir) in dllMappings)
        {
            string? sourceDll = null;

            // 1. Always prefer the target Proton runtime's exact subDir (vkd3d-proton vs vkd3d-low-latency, dxvk vs dxvk-low-latency)
            //    when present, matching what Proton itself deploys on game launch and avoiding stale DLLs left in gameSys32.
            if (wineLib64 != null)
            {
                string candidate = Path.Combine(wineLib64, subDir, dllName);
                if (File.Exists(candidate))
                    sourceDll = candidate;
            }

            // 2. Fallback to the game prefix's own system32 DLL (e.g. standalone Wine runner with manually installed DXVK/VKD3D)
            //    ONLY when the prefix matches the target runner.
            bool prefixMatchesTargetProton = !File.Exists(Path.Combine(gameCompatData, "config_info"))
                                          || DoesPrefixMatchProton(gameCompatData, protonPath);
            if (sourceDll == null && prefixMatchesTargetProton && Directory.Exists(gameSys32))
            {
                string candidate = Path.Combine(gameSys32, dllName);
                var fi = new FileInfo(candidate);
                if (fi.Exists && fi.LinkTarget == null && fi.Length > 65536)
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

    private static readonly string[] ShaderCodegenEnvKeys =
    [
        "DXVK_CONFIG",
        "RADV_DEBUG",
        "RADV_PERFTEST",
        "VKD3D_CONFIG",
        "VKD3D_DISABLE_EXTENSIONS",
        "VKD3D_FEATURE_LEVEL",
        "VKD3D_SHADER_CONFIG",
        "VKD3D_SHADER_MODEL"
    ];

    private static readonly string[] TrackedProcessEnvKeys =
    [
        "DXVK_CONFIG",
        "PROTON_DXVK_LOWLATENCY",
        "PROTON_DXVK_SAREK",
        "PROTON_USE_WINED3D",
        "PROTON_USE_WINED3D11",
        "PROTON_VKD3D_HEAP",
        "PROTON_VKD3D_LOWLATENCY",
        "RADV_DEBUG",
        "RADV_PERFTEST",
        "STEAM_COMPAT_CONFIG",
        "VKD3D_CONFIG",
        "VKD3D_DISABLE_EXTENSIONS",
        "VKD3D_FEATURE_LEVEL",
        "VKD3D_SHADER_CONFIG",
        "VKD3D_SHADER_MODEL",
        "__GL_0x9e7d4a"
    ];

    // Runtime-only VKD3D_CONFIG flags (queue selection, VRAM allocation, debug markers, sync) extracted from vkd3d-proton
    // that do NOT alter SPIR-V generation or VkPipeline compilation hashes.
    private static readonly HashSet<string> NonCodegenVkd3dConfigFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "vk_debug",
        "debug_utils",
        "app_debug_marker_only",
        "single_queue",
        "no_upload_hvv",
        "force_host_cached",
        "log_memory_budget",
        "host_import_fallback",
        "disallow_committed_texture_suballocation",
        "avoid_image_buffer_aliasing",
        "fault",
        "defer_resource_destruction",
        "shader_cache_sync",
        "global_pipeline_cache",
        "extra_rtas_sync",
        "zero_fill_blp",
        "rtas_allow_blas_rebuild_sizes",
        "disable_simultaneous_uav_compression",
        "prefer_thin_uav_tiling",
        "force_initial_transition"
    };

    // Runtime-only DXVK_CONFIG keys (HUD, FPS limiters, vsync, compiler thread count, latency sleep, GPU ID spoofing)
    // that do NOT alter compiled SPIR-V or VkPipeline state.
    private static readonly HashSet<string> NonCodegenDxvkConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "dxvk.hud",
        "dxvk.numCompilerThreads",
        "dxvk.numAsyncThreads",
        "dxvk.numDyasyncThreads",
        "dxvk.enableAsync",
        "dxvk.maxFrameRate",
        "d3d11.maxFrameRate",
        "d3d9.maxFrameRate",
        "dxgi.maxFrameRate",
        "dxgi.syncInterval",
        "d3d9.presentInterval",
        "dxvk.tearFree",
        "dxvk.latencySleep",
        "dxvk.latencyTolerance",
        "dxvk.nvapiHack",
        "dxgi.customDeviceId",
        "dxgi.customVendorId",
        "dxgi.hideNvidiaGpu",
        "dxgi.hideAmdGpu",
        "dxgi.hideIntelGpu",
        "dxgi.maxDeviceMemory",
        "dxgi.maxSharedMemory"
    };

    // Logging/dump-only RADV_DEBUG tokens that neither alter AMD ISA codegen nor belong in headless warmer runs.
    private static readonly HashSet<string> NonCodegenRadvDebugFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "info",
        "vm",
        "spirv",
        "nir",
        "shaders",
        "metashaders",
        "hang",
        "checkir",
        "syncshaders",
        "startup",
        "allbos",
        "prologs",
        "epilogs",
        "zerovram"
    };

    // No-op / non-shader-codegen RADV_PERFTEST tokens ("aco" has been Mesa RADV's default compiler since Mesa 20.2
    // and was previously exported by run-gui.sh / run-cli.sh; queue/BO/video tokens do not alter shader ISA).
    private static readonly HashSet<string> NonCodegenRadvPerfTestFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "aco",
        "localbos",
        "dccmsaa",
        "bolist",
        "sparse",
        "nobindless",
        "transfer_queue",
        "video_decode",
        "video_encode"
    };

    private sealed record HeroicLaunchSection(
        IReadOnlyList<(string Key, string Value)> EnvOptions,
        bool HasWrappersProperty,
        IReadOnlyList<string> WrapperCommands,
        string? BeforeLaunchScriptPath);

    private static readonly Dictionary<string, (long LastWriteTicks, long Length, Dictionary<string, string> AppLaunchOptions)> _steamLocalConfigCache = new(StringComparer.Ordinal);
    private static readonly object _steamLocalConfigLock = new();

    private static readonly Dictionary<string, (long LastWriteTicks, long Length, HeroicLaunchSection Section)> _heroicLaunchSectionCache = new(StringComparer.Ordinal);
    private static readonly object _heroicLaunchSectionLock = new();

    private static readonly Dictionary<string, (long LastWriteTicks, long Length, IReadOnlyDictionary<string, string> Env)> _bgmodConfCache = new(StringComparer.Ordinal);
    private static readonly object _bgmodConfLock = new();

    private static bool IsTruthyLauncherEnvValue(IReadOnlyDictionary<string, string> env, string key) =>
        env.TryGetValue(key, out string? val) &&
        !string.IsNullOrWhiteSpace(val) &&
        !string.Equals(val, "0", StringComparison.Ordinal) &&
        !string.Equals(val, "false", StringComparison.OrdinalIgnoreCase);

    private static bool HasSteamCompatConfigToken(IReadOnlyDictionary<string, string> env, string token)
    {
        if (!env.TryGetValue("STEAM_COMPAT_CONFIG", out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static bool IsVkd3dLowLatencyEnabled(IReadOnlyDictionary<string, string> env) =>
        IsTruthyLauncherEnvValue(env, "PROTON_VKD3D_LOWLATENCY") ||
        HasSteamCompatConfigToken(env, "vkd3dlowlatency");

    internal static bool IsDxvkLowLatencyEnabled(IReadOnlyDictionary<string, string> env) =>
        IsTruthyLauncherEnvValue(env, "PROTON_DXVK_LOWLATENCY") ||
        HasSteamCompatConfigToken(env, "dxvklowlatency");

    internal static bool IsDxvkSarekEnabled(IReadOnlyDictionary<string, string> env) =>
        IsTruthyLauncherEnvValue(env, "PROTON_DXVK_SAREK") ||
        HasSteamCompatConfigToken(env, "dxvksarek");

    private static bool IsWineD3dEnabled(IReadOnlyDictionary<string, string> env) =>
        IsTruthyLauncherEnvValue(env, "PROTON_USE_WINED3D") ||
        IsTruthyLauncherEnvValue(env, "PROTON_USE_WINED3D11") ||
        HasSteamCompatConfigToken(env, "wined3d") ||
        HasSteamCompatConfigToken(env, "wined3d11");

    private static bool IsVkd3dHeapEnabled(IReadOnlyDictionary<string, string> env) =>
        IsTruthyLauncherEnvValue(env, "PROTON_VKD3D_HEAP") ||
        HasSteamCompatConfigToken(env, "vkd3dheap");

    private static string ResolveProtonVkd3dSubDir(string? wineLib64, IReadOnlyDictionary<string, string> env) =>
        IsVkd3dLowLatencyEnabled(env) && wineLib64 != null && Directory.Exists(Path.Combine(wineLib64, "vkd3d-low-latency", "x86_64-windows"))
            ? Path.Combine("vkd3d-low-latency", "x86_64-windows")
            : Path.Combine("vkd3d-proton", "x86_64-windows");

    private static string ResolveProtonDxvkSubDir(string? wineLib64, IReadOnlyDictionary<string, string> env)
    {
        if (wineLib64 != null)
        {
            // Match Proton's own precedence: dxvk-sarek > dxvk-low-latency > dxvk
            if (IsDxvkSarekEnabled(env) && Directory.Exists(Path.Combine(wineLib64, "dxvk-sarek", "x86_64-windows")))
                return Path.Combine("dxvk-sarek", "x86_64-windows");
            if (IsDxvkLowLatencyEnabled(env) && Directory.Exists(Path.Combine(wineLib64, "dxvk-low-latency", "x86_64-windows")))
                return Path.Combine("dxvk-low-latency", "x86_64-windows");
        }
        return Path.Combine("dxvk", "x86_64-windows");
    }

    private static string SanitizeWarmerVkd3dConfig(string rawVal)
    {
        if (string.IsNullOrWhiteSpace(rawVal)) return string.Empty;
        var kept = new List<string>();
        foreach (var part in rawVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.Equals(part, "vk_debug", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "debug_utils", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(part);
            }
        }
        return string.Join(',', kept);
    }

    private static string SanitizeWarmerRadvDebug(string rawVal)
    {
        if (string.IsNullOrWhiteSpace(rawVal)) return string.Empty;
        var kept = new List<string>();
        foreach (var part in rawVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!NonCodegenRadvDebugFlags.Contains(part))
                kept.Add(part);
        }
        return string.Join(',', kept);
    }

    private static IReadOnlyList<(string Key, string Value)> GetWarmerShaderEnvPairs(IReadOnlyDictionary<string, string> env)
    {
        var list = new List<(string Key, string Value)>();
        foreach (string key in ShaderCodegenEnvKeys)
        {
            if (!env.TryGetValue(key, out string? rawVal) || string.IsNullOrWhiteSpace(rawVal))
                continue;

            string val = key switch
            {
                "VKD3D_CONFIG" => SanitizeWarmerVkd3dConfig(rawVal),
                "RADV_DEBUG" => SanitizeWarmerRadvDebug(rawVal),
                _ => rawVal.Trim()
            };
            if (string.IsNullOrEmpty(val))
                continue;

            list.Add((key, val));
        }
        return list;
    }

    internal static string CanonicalizeStampEnvKeyValue(string key, string rawVal)
    {
        if (string.IsNullOrWhiteSpace(rawVal))
            return string.Empty;

        switch (key)
        {
            case "VKD3D_CONFIG":
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var part in rawVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!NonCodegenVkd3dConfigFlags.Contains(part))
                        set.Add(part.ToLowerInvariant());
                }
                return string.Join(',', set);
            }

            case "VKD3D_DISABLE_EXTENSIONS":
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var part in rawVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    set.Add(part);
                }
                return string.Join(',', set);
            }

            case "VKD3D_SHADER_CONFIG":
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var part in rawVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    set.Add(part.ToLowerInvariant());
                }
                return string.Join(',', set);
            }

            case "RADV_PERFTEST":
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var part in rawVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!NonCodegenRadvPerfTestFlags.Contains(part))
                        set.Add(part.ToLowerInvariant());
                }
                return string.Join(',', set);
            }

            case "RADV_DEBUG":
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var part in rawVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!NonCodegenRadvDebugFlags.Contains(part))
                        set.Add(part.ToLowerInvariant());
                }
                return string.Join(',', set);
            }

            case "DXVK_CONFIG":
            {
                var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var clause in rawVal.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int eq = clause.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = clause[..eq].Trim();
                    string v = clause[(eq + 1)..].Trim();
                    if (k.Length == 0 || v.Length == 0 || NonCodegenDxvkConfigKeys.Contains(k))
                        continue;
                    map[k.ToLowerInvariant()] = v;
                }
                if (map.Count == 0)
                    return string.Empty;

                var sb = new System.Text.StringBuilder();
                bool first = true;
                foreach (var kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(kv.Key).Append('=').Append(kv.Value);
                }
                return sb.ToString();
            }

            case "VKD3D_FEATURE_LEVEL":
            case "VKD3D_SHADER_MODEL":
                return rawVal.Trim().ToLowerInvariant();

            case "PROTON_DXVK_LOWLATENCY":
            case "PROTON_DXVK_SAREK":
            case "PROTON_VKD3D_LOWLATENCY":
                // Active DLL variant is already encoded directly in the stamp's d3d12core.dll / d3d11.dll byte lengths
                // so pre-existing Low-Latency stamps on disk remain 100% backward-compatible.
                return string.Empty;

            case "PROTON_USE_WINED3D":
            case "PROTON_VKD3D_HEAP":
                return string.Equals(rawVal.Trim(), "0", StringComparison.Ordinal) ||
                       string.Equals(rawVal.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : "1";

            default:
                return rawVal.Trim();
        }
    }

    internal static string CanonicalizeRawEnvStampPayload(string? rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
            return string.Empty;

        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in rawPayload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            string key = entry[..eq].Trim();
            string val = entry[(eq + 1)..].Trim();
            string norm = CanonicalizeStampEnvKeyValue(key, val);
            if (!string.IsNullOrEmpty(norm))
                canonical[key] = norm;
        }

        if (canonical.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var kv in canonical)
        {
            if (!first) sb.Append(';');
            first = false;
            string safeVal = kv.Value
                .Replace('|', '_')
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            sb.Append(kv.Key).Append('=').Append(safeVal);
        }
        return sb.ToString();
    }

    internal static string BuildShaderCodegenEnvStampSuffix(IReadOnlyDictionary<string, string> env)
    {
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (IsWineD3dEnabled(env))
            canonical["PROTON_USE_WINED3D"] = "1";
        if (IsVkd3dHeapEnabled(env))
            canonical["PROTON_VKD3D_HEAP"] = "1";
        if (env.TryGetValue("__GL_0x9e7d4a", out string? rawNvPromo) &&
            string.Equals(rawNvPromo?.Trim(), "0", StringComparison.Ordinal))
        {
            canonical["__GL_0x9e7d4a"] = "0";
        }

        foreach (string key in ShaderCodegenEnvKeys)
        {
            if (!env.TryGetValue(key, out string? rawVal) || string.IsNullOrWhiteSpace(rawVal))
                continue;

            string norm = CanonicalizeStampEnvKeyValue(key, rawVal);
            if (!string.IsNullOrEmpty(norm))
                canonical[key] = norm;
        }

        if (canonical.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder("|env=");
        bool first = true;
        foreach (var kv in canonical)
        {
            if (!first) sb.Append(';');
            first = false;
            string safeVal = kv.Value
                .Replace('|', '_')
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            sb.Append(kv.Key).Append('=').Append(safeVal);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolves the effective per-game launch environment variables from the game's launcher configuration
    /// (Steam <c>localconfig.vdf</c> <c>LaunchOptions</c>, Heroic <c>config.json</c> + <c>GamesConfig/&lt;id&gt;.json</c>,
    /// Lutris <c>system.env</c> + <c>prefix_command</c>, or Manual <c>LaunchCommand</c>) and any Goverlay <c>bgmod.conf</c>
    /// explicitly invoked by that game's launch wrapper.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetEffectiveGameLauncherEnvironment(
        GameInfo game,
        string? homeDir = null,
        string? xdgDataHome = null,
        string? xdgConfigHome = null)
    {
        string home = homeDir ?? HomeDir;
        string xdgData = Shaderless.Core.AppPaths.GetXdgDataHome(home, xdgDataHome);
        string xdgConfig = Shaderless.Core.AppPaths.GetXdgConfigHome(home, xdgConfigHome);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        if (homeDir == null)
        {
            foreach (string key in TrackedProcessEnvKeys)
            {
                string? procVal = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(procVal))
                    env[key] = procVal;
            }
        }

        string rawId = game.RawId;
        if (game.Source == GameSource.Steam && !string.IsNullOrEmpty(rawId))
        {
            string? launchOptions = TryGetSteamAppLaunchOptions(game, rawId, home, xdgDataHome, isHostDefaultHome: homeDir == null);
            if (!string.IsNullOrWhiteSpace(launchOptions))
            {
                ApplyShellLaunchStringToEnv(launchOptions, requirePercentCommand: true, stopAtExe: false, game, home, xdgData, env);
            }
        }
        else if (game.Source == GameSource.Heroic && !string.IsNullOrEmpty(rawId))
        {
            ApplyHeroicLaunchConfigToEnv(game, rawId, home, xdgData, xdgConfig, env);
        }
        else if (game.Source == GameSource.Lutris)
        {
            var lutrisCfg = Shaderless.Core.Games.LutrisLinuxSource.TryReadGameRunnerConfig(
                game.Id,
                game.IsFlatpak,
                home,
                xdgDataHome,
                xdgConfigHome);

            if (lutrisCfg != null)
            {
                if (!string.IsNullOrWhiteSpace(lutrisCfg.PrefixCommand))
                {
                    ApplyShellLaunchStringToEnv(lutrisCfg.PrefixCommand, requirePercentCommand: false, stopAtExe: false, game, home, xdgData, env);
                }

                foreach (var kv in lutrisCfg.Env)
                {
                    if (string.IsNullOrEmpty(kv.Value))
                        env.Remove(kv.Key);
                    else
                        env[kv.Key] = kv.Value;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(game.LaunchCommand))
        {
            bool hasPercentCmd = game.LaunchCommand.Contains("%command%", StringComparison.OrdinalIgnoreCase);
            ApplyShellLaunchStringToEnv(
                game.LaunchCommand,
                requirePercentCommand: hasPercentCmd,
                stopAtExe: !hasPercentCmd,
                game,
                home,
                xdgData,
                env);
        }

        return env;
    }

    private static string? TryGetSteamAppLaunchOptions(
        GameInfo game,
        string rawId,
        string home,
        string? xdgDataHome,
        bool isHostDefaultHome)
    {
        var steamRoots = new List<string>();
        var seenRoots = new HashSet<string>(StringComparer.Ordinal);
        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            string canon = Shaderless.Core.AppPaths.GetCanonicalPath(root);
            if (seenRoots.Add(canon))
                steamRoots.Add(root);
        }

        if (isHostDefaultHome && xdgDataHome == null)
        {
            AddRoot(ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath));
        }

        var allRoots = GetAllSteamRoots(home, xdgDataHome);
        if (IsFlatpakGame(game.Id, game.InstallPath, game.PrefixPath))
        {
            foreach (var r in allRoots.OrderByDescending(p => p.Contains("/.var/app/", StringComparison.Ordinal)))
                AddRoot(r);
        }
        else
        {
            foreach (var r in allRoots)
                AddRoot(r);
        }

        foreach (var sr in steamRoots)
        {
            string userdataDir = Path.Combine(sr, "userdata");
            if (!Directory.Exists(userdataDir)) continue;

            List<(string Path, long Ticks, long Length)> localConfigs;
            try
            {
                localConfigs = Directory.EnumerateDirectories(userdataDir)
                    .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
                    .Where(File.Exists)
                    .Select(p =>
                    {
                        var fi = new FileInfo(p);
                        return (Path: p, Ticks: fi.LastWriteTimeUtc.Ticks, Length: fi.Length);
                    })
                    .OrderByDescending(x => x.Ticks)
                    .ThenBy(x => x.Path, StringComparer.Ordinal)
                    .ToList();
            }
            catch
            {
                continue;
            }

            foreach (var lc in localConfigs)
            {
                Dictionary<string, string> appMap;
                lock (_steamLocalConfigLock)
                {
                    if (!_steamLocalConfigCache.TryGetValue(lc.Path, out var cached) ||
                        cached.LastWriteTicks != lc.Ticks ||
                        cached.Length != lc.Length)
                    {
                        try
                        {
                            string vdfText = File.ReadAllText(lc.Path);
                            appMap = ParseSteamLocalConfigAppLaunchOptions(vdfText);
                            _steamLocalConfigCache[lc.Path] = (lc.Ticks, lc.Length, appMap);
                        }
                        catch
                        {
                            continue;
                        }
                    }
                    else
                    {
                        appMap = cached.AppLaunchOptions;
                    }
                }

                if (appMap.TryGetValue(rawId, out string? launchOpts))
                    return launchOpts;
            }
        }

        return null;
    }

    internal static Dictionary<string, string> ParseSteamLocalConfigAppLaunchOptions(string vdf)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(vdf))
            return result;

        int i = 0;
        int n = vdf.Length;
        int depth = 0;
        int matchedDepth = 0;
        string? pendingKey = null;
        string? currentAppId = null;

        while (i < n)
        {
            char c = vdf[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < n && vdf[i + 1] == '/')
            {
                i += 2;
                while (i < n && vdf[i] != '\n')
                    i++;
                continue;
            }

            if (c == '[')
            {
                // Skip VDF conditional tags such as [$LINUX]
                i++;
                while (i < n && vdf[i] != ']')
                    i++;
                if (i < n) i++;
                continue;
            }

            if (c == '{')
            {
                i++;
                if (depth == matchedDepth && pendingKey != null)
                {
                    bool matchesLevel = matchedDepth switch
                    {
                        0 => pendingKey.Equals("UserLocalConfigStore", StringComparison.OrdinalIgnoreCase),
                        1 => pendingKey.Equals("Software", StringComparison.OrdinalIgnoreCase),
                        2 => pendingKey.Equals("Valve", StringComparison.OrdinalIgnoreCase),
                        3 => pendingKey.Equals("Steam", StringComparison.OrdinalIgnoreCase),
                        4 => pendingKey.Equals("apps", StringComparison.OrdinalIgnoreCase),
                        5 => pendingKey.Length > 0,
                        _ => false
                    };

                    if (matchesLevel)
                    {
                        if (matchedDepth == 5)
                        {
                            currentAppId = pendingKey;
                            if (!result.ContainsKey(currentAppId))
                                result[currentAppId] = string.Empty;
                        }
                        matchedDepth++;
                    }
                }

                pendingKey = null;
                depth++;
                continue;
            }

            if (c == '}')
            {
                i++;
                pendingKey = null;
                if (depth > 0)
                    depth--;
                if (matchedDepth > depth)
                {
                    if (matchedDepth == 6)
                        currentAppId = null;
                    matchedDepth = depth;
                }
                continue;
            }

            bool needTokenString = depth == matchedDepth && matchedDepth <= 6;
            string? token = c == '"'
                ? ReadVdfQuotedToken(vdf, ref i, needTokenString)
                : ReadVdfUnquotedToken(vdf, ref i, needTokenString);

            if (!needTokenString)
                continue;

            if (pendingKey == null)
            {
                pendingKey = token ?? string.Empty;
            }
            else
            {
                if (matchedDepth == 6 &&
                    currentAppId != null &&
                    pendingKey.Equals("LaunchOptions", StringComparison.OrdinalIgnoreCase))
                {
                    result[currentAppId] = token ?? string.Empty;
                }
                pendingKey = null;
            }
        }

        return result;
    }

    private static string? ReadVdfQuotedToken(string vdf, ref int i, bool materialize)
    {
        int n = vdf.Length;
        i++; // skip opening '"'
        int start = i;
        bool hasEscapes = false;

        if (!materialize)
        {
            while (i < n)
            {
                char ch = vdf[i];
                if (ch == '\\' && i + 1 < n)
                {
                    i += 2;
                    continue;
                }
                if (ch == '"')
                {
                    i++;
                    break;
                }
                i++;
            }
            return null;
        }

        while (i < n)
        {
            char ch = vdf[i];
            if (ch == '\\' && i + 1 < n)
            {
                hasEscapes = true;
                i += 2;
                continue;
            }
            if (ch == '"')
            {
                int end = i;
                i++;
                if (!hasEscapes)
                    return vdf[start..end];

                var sb = new System.Text.StringBuilder(end - start);
                for (int k = start; k < end; k++)
                {
                    if (vdf[k] == '\\' && k + 1 < end)
                    {
                        k++;
                        sb.Append(vdf[k] switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '\\' => '\\',
                            '"' => '"',
                            _ => vdf[k]
                        });
                    }
                    else
                    {
                        sb.Append(vdf[k]);
                    }
                }
                return sb.ToString();
            }
            i++;
        }

        return vdf[start..];
    }

    private static string? ReadVdfUnquotedToken(string vdf, ref int i, bool materialize)
    {
        int n = vdf.Length;
        int start = i;
        while (i < n)
        {
            char ch = vdf[i];
            if (char.IsWhiteSpace(ch) || ch is '{' or '}' or '"' or '[')
                break;
            if (ch == '/' && i + 1 < n && vdf[i + 1] == '/')
                break;
            i++;
        }
        return materialize ? vdf[start..i] : null;
    }

    private static void ApplyHeroicLaunchConfigToEnv(
        GameInfo game,
        string rawId,
        string home,
        string xdgData,
        string xdgConfig,
        Dictionary<string, string> env)
    {
        var heroicDirs = (game.IsFlatpak
            ? new[]
            {
                Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"),
                Path.Combine(xdgConfig, "heroic"),
                Path.Combine(home, ".config", "heroic")
            }
            : new[]
            {
                Path.Combine(xdgConfig, "heroic"),
                Path.Combine(home, ".config", "heroic"),
                Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic")
            }).Distinct(StringComparer.Ordinal).ToList();

        string? selectedDir = heroicDirs.FirstOrDefault(d => File.Exists(Path.Combine(d, "GamesConfig", $"{rawId}.json")))
                           ?? heroicDirs.FirstOrDefault(d => File.Exists(Path.Combine(d, "config.json")));
        if (selectedDir == null)
            return;

        HeroicLaunchSection? defaultSec = TryGetHeroicLaunchSection(Path.Combine(selectedDir, "config.json"), "defaultSettings");
        HeroicLaunchSection? gameSec = TryGetHeroicLaunchSection(Path.Combine(selectedDir, "GamesConfig", $"{rawId}.json"), rawId);

        if (defaultSec != null)
        {
            foreach (var (k, v) in defaultSec.EnvOptions)
            {
                if (string.IsNullOrEmpty(v))
                    env.Remove(k);
                else
                    env[k] = v;
            }
        }

        string? beforeScript = !string.IsNullOrWhiteSpace(gameSec?.BeforeLaunchScriptPath)
            ? gameSec!.BeforeLaunchScriptPath
            : defaultSec?.BeforeLaunchScriptPath;
        if (!string.IsNullOrWhiteSpace(beforeScript))
        {
            ApplyShellLaunchStringToEnv(beforeScript, requirePercentCommand: false, stopAtExe: false, game, home, xdgData, env);
        }

        IReadOnlyList<string>? activeWrappers = gameSec is { HasWrappersProperty: true }
            ? gameSec.WrapperCommands
            : defaultSec?.WrapperCommands;
        if (activeWrappers != null)
        {
            foreach (string wrapperCmd in activeWrappers)
            {
                if (!string.IsNullOrWhiteSpace(wrapperCmd))
                    ApplyShellLaunchStringToEnv(wrapperCmd, requirePercentCommand: false, stopAtExe: false, game, home, xdgData, env);
            }
        }

        if (gameSec != null)
        {
            foreach (var (k, v) in gameSec.EnvOptions)
            {
                if (string.IsNullOrEmpty(v))
                    env.Remove(k);
                else
                    env[k] = v;
            }
        }
    }

    private static HeroicLaunchSection? TryGetHeroicLaunchSection(string filePath, string sectionKey)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var fi = new FileInfo(filePath);
            long ticks = fi.LastWriteTimeUtc.Ticks;
            long len = fi.Length;
            string cacheKey = $"{filePath}|{sectionKey}";

            lock (_heroicLaunchSectionLock)
            {
                if (_heroicLaunchSectionCache.TryGetValue(cacheKey, out var cached) &&
                    cached.LastWriteTicks == ticks &&
                    cached.Length == len)
                {
                    return cached.Section;
                }
            }

            var envOpts = new List<(string Key, string Value)>();
            var wrappers = new List<string>();
            bool hasWrappersProp = false;
            string? beforeScript = null;

            using (var doc = JsonDocument.Parse(File.ReadAllText(filePath)))
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var settings = root.TryGetProperty(sectionKey, out var inner) && inner.ValueKind == JsonValueKind.Object
                        ? inner
                        : (string.Equals(sectionKey, "defaultSettings", StringComparison.Ordinal) ? default : root);

                    if (settings.ValueKind == JsonValueKind.Object)
                    {
                        foreach (string propName in new[] { "enviromentOptions", "environmentOptions" })
                        {
                            if (settings.TryGetProperty(propName, out var envArr) && envArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in envArr.EnumerateArray())
                                {
                                    if (item.ValueKind != JsonValueKind.Object) continue;
                                    string? key = item.TryGetProperty("key", out var kp) && kp.ValueKind == JsonValueKind.String
                                        ? kp.GetString()?.Trim()
                                        : null;
                                    if (string.IsNullOrEmpty(key)) continue;

                                    string val = string.Empty;
                                    if (item.TryGetProperty("value", out var vp))
                                    {
                                        val = vp.ValueKind switch
                                        {
                                            JsonValueKind.String => vp.GetString() ?? string.Empty,
                                            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => vp.GetRawText(),
                                            _ => string.Empty
                                        };
                                    }
                                    envOpts.Add((key, val));
                                }
                            }
                        }

                        if (settings.TryGetProperty("wrapperOptions", out var wrapArr) && wrapArr.ValueKind == JsonValueKind.Array)
                        {
                            hasWrappersProp = true;
                            foreach (var item in wrapArr.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Object)
                                {
                                    string? exe = item.TryGetProperty("exe", out var ep) && ep.ValueKind == JsonValueKind.String
                                        ? ep.GetString()?.Trim()
                                        : null;
                                    string? args = item.TryGetProperty("args", out var ap) && ap.ValueKind == JsonValueKind.String
                                        ? ap.GetString()?.Trim()
                                        : null;
                                    if (!string.IsNullOrEmpty(exe))
                                    {
                                        string quotedExe = exe.Contains(' ') && !exe.StartsWith('"') ? $"\"{exe}\"" : exe;
                                        wrappers.Add(string.IsNullOrEmpty(args) ? quotedExe : $"{quotedExe} {args}");
                                    }
                                }
                                else if (item.ValueKind == JsonValueKind.String)
                                {
                                    string? s = item.GetString()?.Trim();
                                    if (!string.IsNullOrEmpty(s))
                                        wrappers.Add(s);
                                }
                            }
                        }

                        if (settings.TryGetProperty("beforeLaunchScriptPath", out var bsp) && bsp.ValueKind == JsonValueKind.String)
                        {
                            beforeScript = bsp.GetString()?.Trim();
                        }
                    }
                }
            }

            var parsed = new HeroicLaunchSection(envOpts, hasWrappersProp, wrappers, beforeScript);
            lock (_heroicLaunchSectionLock)
            {
                _heroicLaunchSectionCache[cacheKey] = (ticks, len, parsed);
            }
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyShellLaunchStringToEnv(
        string launchString,
        bool requirePercentCommand,
        bool stopAtExe,
        GameInfo game,
        string home,
        string xdgData,
        Dictionary<string, string> env)
    {
        if (string.IsNullOrWhiteSpace(launchString))
            return;

        string prefixSlice = launchString;
        if (requirePercentCommand)
        {
            int cmdIdx = launchString.IndexOf("%command%", StringComparison.OrdinalIgnoreCase);
            if (cmdIdx < 0)
                return;
            prefixSlice = launchString[..cmdIdx];
        }

        var tokens = TokenizeShellWords(prefixSlice);
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token.Length == 0)
                continue;

            if (stopAtExe && token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.Equals(token, "unset", StringComparison.Ordinal) ||
                string.Equals(token, "-u", StringComparison.Ordinal))
            {
                if (i + 1 < tokens.Count && IsValidEnvVarName(tokens[i + 1]))
                {
                    env.Remove(tokens[++i]);
                }
                continue;
            }

            if (token.StartsWith("--unset=", StringComparison.Ordinal))
            {
                string unsetKey = token["--unset=".Length..];
                if (IsValidEnvVarName(unsetKey))
                    env.Remove(unsetKey);
                continue;
            }

            int eq = token.IndexOf('=');
            if (eq > 0 && IsValidEnvVarName(token.AsSpan(0, eq)))
            {
                string key = token[..eq];
                string val = token[(eq + 1)..];
                if (val.Length == 0)
                    env.Remove(key);
                else
                    env[key] = val;
                continue;
            }

            string expanded = ExpandHomePrefix(token, home);
            string baseName = Path.GetFileName(expanded.TrimEnd('/', '\\'));
            if (string.Equals(baseName, "bgmod", StringComparison.Ordinal))
            {
                string? bgConf = ResolveBgmodConfPath(expanded, game.Name, home, xdgData);
                if (bgConf != null)
                {
                    foreach (var kv in ReadBgmodEnv(bgConf))
                    {
                        if (string.IsNullOrEmpty(kv.Value))
                            env.Remove(kv.Key);
                        else
                            env[kv.Key] = kv.Value;
                    }
                }
            }
        }
    }

    private static string ExpandHomePrefix(string pathToken, string home)
    {
        if (pathToken.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(home, pathToken[2..]);
        if (pathToken.StartsWith("$HOME/", StringComparison.Ordinal))
            return Path.Combine(home, pathToken[6..]);
        if (pathToken.StartsWith("${HOME}/", StringComparison.Ordinal))
            return Path.Combine(home, pathToken[8..]);
        return pathToken;
    }

    private static string? ResolveBgmodConfPath(string expandedBgmodToken, string? gameName, string home, string xdgData)
    {
        var candidates = new List<string>();
        if (expandedBgmodToken.Contains('/') || expandedBgmodToken.Contains('\\'))
        {
            string? tokenDir = Path.GetDirectoryName(expandedBgmodToken);
            if (!string.IsNullOrEmpty(tokenDir))
                candidates.Add(Path.Combine(tokenDir, "bgmod.conf"));
        }

        var govRoots = new[]
        {
            Path.Combine(xdgData, "goverlay", "gameconfig"),
            Path.Combine(home, ".local", "share", "goverlay", "gameconfig")
        }.Distinct(StringComparer.Ordinal).ToList();

        if (!string.IsNullOrWhiteSpace(gameName))
        {
            string sanitized = gameName.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
            foreach (var root in govRoots)
            {
                candidates.Add(Path.Combine(root, gameName, "bgmod.conf"));
                if (!string.Equals(sanitized, gameName, StringComparison.Ordinal))
                    candidates.Add(Path.Combine(root, sanitized, "bgmod.conf"));
            }
        }

        foreach (var root in govRoots)
        {
            candidates.Add(Path.Combine(root, "global", "bgmod.conf"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IReadOnlyDictionary<string, string> ReadBgmodEnv(string confPath)
    {
        try
        {
            var fi = new FileInfo(confPath);
            long ticks = fi.LastWriteTimeUtc.Ticks;
            long len = fi.Length;

            lock (_bgmodConfLock)
            {
                if (_bgmodConfCache.TryGetValue(confPath, out var cached) &&
                    cached.LastWriteTicks == ticks &&
                    cached.Length == len)
                {
                    return cached.Env;
                }
            }

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            string currentSection = string.Empty;

            foreach (string rawLine in File.ReadAllLines(confPath))
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
                    continue;

                if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
                {
                    currentSection = trimmed[1..^1].Trim();
                    continue;
                }

                if (currentSection.Length > 0 && !currentSection.Equals("Env", StringComparison.OrdinalIgnoreCase))
                    continue;

                int eq = trimmed.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = trimmed[..eq].Trim();
                if (!IsValidEnvVarName(key))
                    continue;

                string val = trimmed[(eq + 1)..].Trim();
                if (val.Length >= 2 &&
                    ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                {
                    val = val[1..^1];
                }

                dict[key] = val;
            }

            lock (_bgmodConfLock)
            {
                _bgmodConfCache[confPath] = (ticks, len, dict);
            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool IsValidEnvVarName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty) return false;
        char first = name[0];
        if (!((first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z') || first == '_'))
            return false;

        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                return false;
        }
        return true;
    }

    private static List<string> TokenizeShellWords(string input)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool hasToken = false;
        bool inSingle = false;
        bool inDouble = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (inSingle)
            {
                if (c == '\'')
                    inSingle = false;
                else
                    current.Append(c);
                continue;
            }

            if (inDouble)
            {
                if (c == '"')
                {
                    inDouble = false;
                }
                else if (c == '\\' && i + 1 < input.Length && input[i + 1] is '"' or '\\' or '$' or '`')
                {
                    current.Append(input[++i]);
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (char.IsWhiteSpace(c) || c is ';' or '&' or '|')
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            if (c == '\'')
            {
                inSingle = true;
                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                inDouble = true;
                hasToken = true;
                continue;
            }

            if (c == '\\' && i + 1 < input.Length)
            {
                current.Append(input[++i]);
                hasToken = true;
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
            tokens.Add(current.ToString());

        return tokens;
    }

    public static bool IsLowLatencyConfigured(
        GameInfo game,
        string envVarName,
        string? homeDir = null,
        string? xdgDataHome = null,
        string? xdgConfigHome = null)
    {
        var env = GetEffectiveGameLauncherEnvironment(game, homeDir, xdgDataHome, xdgConfigHome);
        return envVarName switch
        {
            "PROTON_VKD3D_LOWLATENCY" => IsVkd3dLowLatencyEnabled(env),
            "PROTON_DXVK_LOWLATENCY" => IsDxvkLowLatencyEnabled(env),
            "PROTON_DXVK_SAREK" => IsDxvkSarekEnabled(env),
            _ => IsTruthyLauncherEnvValue(env, envVarName)
        };
    }

    /// <summary>
    /// Compares a saved Proton stamp against the current Proton stamp after canonicalizing shader-codegen launch options,
    /// returning <c>null</c> when equivalent, <c>"Proton updated"</c> when the Proton build or DLLs changed, or
    /// <c>"launch options changed"</c> when only shader-affecting launch options / DLL variant toggles changed.
    /// </summary>
    public static string? CompareProtonStamps(string? savedProtonStamp, string? currentProtonStamp)
    {
        if (string.IsNullOrEmpty(savedProtonStamp) ||
            string.IsNullOrEmpty(currentProtonStamp) ||
            string.Equals(savedProtonStamp, "unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentProtonStamp, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(savedProtonStamp, currentProtonStamp, StringComparison.Ordinal))
            return null;

        var saved = ParseProtonStampParts(savedProtonStamp);
        var current = ParseProtonStampParts(currentProtonStamp);

        if (!string.Equals(saved.ProtonPath, current.ProtonPath, StringComparison.Ordinal))
            return "Proton updated";

        if (saved.NvPromoZero != current.NvPromoZero ||
            !string.Equals(saved.CanonicalEnv, current.CanonicalEnv, StringComparison.Ordinal))
        {
            return "launch options changed";
        }

        if (!string.Equals(saved.DllSigs, current.DllSigs, StringComparison.Ordinal))
        {
            return IsProtonDllVariantSwitch(saved.ProtonPath, saved.DllSigs, current.DllSigs)
                ? "launch options changed"
                : "Proton updated";
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="oldDllSigs"/> and <paramref name="currentDllSigs"/> differ solely because
    /// the user toggled between DLL variants (<c>vkd3d-proton</c> vs <c>vkd3d-low-latency</c>, or <c>dxvk</c> vs
    /// <c>dxvk-low-latency</c> / <c>dxvk-sarek</c>) that both exist inside the same <paramref name="protonPath"/>
    /// installation — as opposed to an in-place Proton upgrade where the old DLL size no longer exists in any variant folder.
    /// </summary>
    internal static bool IsProtonDllVariantSwitch(string protonPath, string oldDllSigs, string currentDllSigs)
    {
        if (string.IsNullOrEmpty(protonPath) ||
            string.IsNullOrEmpty(oldDllSigs) ||
            string.IsNullOrEmpty(currentDllSigs) ||
            string.Equals(oldDllSigs, currentDllSigs, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string? protonDir = Path.GetDirectoryName(protonPath);
            if (string.IsNullOrEmpty(protonDir) || !Directory.Exists(protonDir))
                return false;

            string? wineLib64 = new[]
            {
                Path.Combine(protonDir, "files", "lib64", "wine"),
                Path.Combine(protonDir, "dist", "lib64", "wine"),
                Path.Combine(protonDir, "files", "lib", "wine"),
                Path.Combine(protonDir, "dist", "lib", "wine")
            }.FirstOrDefault(Directory.Exists);

            if (wineLib64 == null)
                return false;

            var vkd3dLengths = new HashSet<long>();
            foreach (string rel in new[]
            {
                Path.Combine("vkd3d-proton", "x86_64-windows", "d3d12core.dll"),
                Path.Combine("vkd3d-low-latency", "x86_64-windows", "d3d12core.dll"),
                Path.Combine("vkd3d-proton", "d3d12core.dll"),
                Path.Combine("vkd3d-low-latency", "d3d12core.dll")
            })
            {
                string full = Path.Combine(wineLib64, rel);
                if (File.Exists(full))
                    vkd3dLengths.Add(new FileInfo(full).Length);
            }

            var dxvkLengths = new HashSet<long>();
            foreach (string rel in new[]
            {
                Path.Combine("dxvk", "x86_64-windows", "d3d11.dll"),
                Path.Combine("dxvk-low-latency", "x86_64-windows", "d3d11.dll"),
                Path.Combine("dxvk-sarek", "x86_64-windows", "d3d11.dll"),
                Path.Combine("dxvk", "d3d11.dll"),
                Path.Combine("dxvk-low-latency", "d3d11.dll"),
                Path.Combine("dxvk-sarek", "d3d11.dll")
            })
            {
                string full = Path.Combine(wineLib64, rel);
                if (File.Exists(full))
                    dxvkLengths.Add(new FileInfo(full).Length);
            }

            var oldParts = oldDllSigs.Split('|');
            var curParts = currentDllSigs.Split('|');
            if (oldParts.Length != curParts.Length || oldParts.Length is < 1 or > 2)
                return false;

            if (!long.TryParse(oldParts[0], out long oldVkd3d) ||
                !long.TryParse(curParts[0], out long curVkd3d) ||
                oldVkd3d <= 0 || curVkd3d <= 0)
            {
                return false;
            }

            bool vkd3dValid = oldVkd3d == curVkd3d ||
                              (vkd3dLengths.Contains(oldVkd3d) && vkd3dLengths.Contains(curVkd3d));
            if (!vkd3dValid)
                return false;

            if (oldParts.Length == 2)
            {
                if (!long.TryParse(oldParts[1], out long oldDxvk) ||
                    !long.TryParse(curParts[1], out long curDxvk) ||
                    oldDxvk <= 0 || curDxvk <= 0)
                {
                    return false;
                }

                bool dxvkValid = oldDxvk == curDxvk ||
                                 (dxvkLengths.Contains(oldDxvk) && dxvkLengths.Contains(curDxvk));
                if (!dxvkValid)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool AreDriverAndProtonStampsEquivalent(string? savedRuntimeStamp, string? currentRuntimeStamp)
    {
        if (string.IsNullOrEmpty(savedRuntimeStamp) || string.IsNullOrEmpty(currentRuntimeStamp))
            return true;

        if (string.Equals(savedRuntimeStamp, currentRuntimeStamp, StringComparison.Ordinal))
            return true;

        int savedPipe = savedRuntimeStamp.IndexOf('|');
        int currentPipe = currentRuntimeStamp.IndexOf('|');
        if (savedPipe <= 0 || currentPipe <= 0)
            return false;

        string savedDriver = savedRuntimeStamp[..savedPipe];
        string currentDriver = currentRuntimeStamp[..currentPipe];
        if (!string.Equals(savedDriver, "Unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(currentDriver, "Unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(savedDriver, currentDriver, StringComparison.Ordinal))
        {
            return false;
        }

        string savedProton = savedRuntimeStamp[(savedPipe + 1)..];
        string currentProton = currentRuntimeStamp[(currentPipe + 1)..];
        return CompareProtonStamps(savedProton, currentProton) == null;
    }

    private static (string ProtonPath, string DllSigs, bool NvPromoZero, string CanonicalEnv) ParseProtonStampParts(string stamp)
    {
        var parts = stamp.Split('|');
        string protonPath = parts.Length > 0 ? parts[0] : string.Empty;
        var dllSigs = new List<string>();
        bool nvPromoZero = false;
        string canonicalEnv = string.Empty;

        for (int i = 1; i < parts.Length; i++)
        {
            string seg = parts[i];
            if (string.Equals(seg, "nvpromo=0", StringComparison.Ordinal))
            {
                nvPromoZero = true;
            }
            else if (seg.StartsWith("env=", StringComparison.Ordinal))
            {
                canonicalEnv = CanonicalizeRawEnvStampPayload(seg["env=".Length..]);
            }
            else
            {
                dllSigs.Add(seg);
            }
        }

        return (protonPath, string.Join('|', dllSigs), nvPromoZero, canonicalEnv);
    }

    internal static string? CompareRuntimeCacheStamps(string? oldStamp, string? currentStamp)
    {
        if (string.IsNullOrWhiteSpace(oldStamp) || string.IsNullOrWhiteSpace(currentStamp))
            return "Proton updated";

        if (string.Equals(oldStamp, currentStamp, StringComparison.Ordinal))
            return null;

        var oldParsed = ParseRuntimeCacheStampParts(oldStamp);
        var curParsed = ParseRuntimeCacheStampParts(currentStamp);

        if (!string.IsNullOrEmpty(oldParsed.NvidiaVer) &&
            !string.IsNullOrEmpty(curParsed.NvidiaVer) &&
            !string.Equals(oldParsed.NvidiaVer, "default", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(curParsed.NvidiaVer, "default", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(oldParsed.NvidiaVer, curParsed.NvidiaVer, StringComparison.Ordinal))
        {
            return "driver updated";
        }

        if (!string.Equals(oldParsed.ProtonPath, curParsed.ProtonPath, StringComparison.Ordinal))
            return "Proton updated";

        if (oldParsed.NvPromoZero != curParsed.NvPromoZero ||
            !string.Equals(oldParsed.CanonicalEnv, curParsed.CanonicalEnv, StringComparison.Ordinal))
        {
            return "launch options changed";
        }

        if (!string.Equals(oldParsed.D3d12Sig, curParsed.D3d12Sig, StringComparison.Ordinal))
        {
            return IsProtonDllVariantSwitch(oldParsed.ProtonPath, oldParsed.D3d12Sig, curParsed.D3d12Sig)
                ? "launch options changed"
                : "Proton updated";
        }

        return null;
    }

    private static (string ProtonPath, string D3d12Sig, string? NvidiaVer, bool NvPromoZero, string CanonicalEnv) ParseRuntimeCacheStampParts(string stamp)
    {
        var parts = stamp.Split('|');
        string protonPath = parts.Length > 0 ? parts[0] : string.Empty;
        string d3d12Sig = parts.Length > 1 ? parts[1] : "default";
        string? nvidiaVer = null;
        int extraStart = 2;

        if (parts.Length > 2 &&
            !parts[2].StartsWith("nvpromo=", StringComparison.Ordinal) &&
            !parts[2].StartsWith("env=", StringComparison.Ordinal))
        {
            nvidiaVer = parts[2];
            extraStart = 3;
        }

        bool nvPromoZero = false;
        string rawEnv = string.Empty;
        for (int i = extraStart; i < parts.Length; i++)
        {
            string seg = parts[i];
            if (string.Equals(seg, "nvpromo=0", StringComparison.Ordinal))
                nvPromoZero = true;
            else if (seg.StartsWith("env=", StringComparison.Ordinal))
                rawEnv = seg["env=".Length..];
        }

        string canonicalEnv = CanonicalizeRawEnvStampPayload(rawEnv);
        if (!string.IsNullOrEmpty(canonicalEnv) && canonicalEnv.Contains("__GL_0x9e7d4a=0", StringComparison.Ordinal))
        {
            nvPromoZero = true;
            var filtered = canonicalEnv
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !string.Equals(p, "__GL_0x9e7d4a=0", StringComparison.Ordinal));
            canonicalEnv = string.Join(';', filtered);
        }

        return (protonPath, d3d12Sig, nvidiaVer, nvPromoZero, canonicalEnv);
    }

    public static string GetProtonStamp(
        GameInfo game,
        string? homeDir = null,
        string? xdgDataHome = null,
        string? xdgConfigHome = null)
    {
        try
        {
            string? protonPath = FindProtonBinaryForGame(game, preferredVersion: null, homeDir, xdgDataHome, xdgConfigHome);
            if (string.IsNullOrEmpty(protonPath)) return "unknown";

            string? protonDir = Path.GetDirectoryName(protonPath);
            string? wineLib64 = protonDir == null ? null : new[]
            {
                Path.Combine(protonDir, "files", "lib64", "wine"),
                Path.Combine(protonDir, "dist", "lib64", "wine"),
                Path.Combine(protonDir, "files", "lib", "wine"),
                Path.Combine(protonDir, "dist", "lib", "wine")
            }.FirstOrDefault(Directory.Exists);

            var launcherEnv = GetEffectiveGameLauncherEnvironment(game, homeDir, xdgDataHome, xdgConfigHome);
            string vkd3dSubDir = ResolveProtonVkd3dSubDir(wineLib64, launcherEnv);
            string dxvkSubDir = ResolveProtonDxvkSubDir(wineLib64, launcherEnv);

            long vkd3dLen = 0;
            long dxvkLen = 0;
            if (wineLib64 != null)
            {
                string d3d12Core = Path.Combine(wineLib64, vkd3dSubDir, "d3d12core.dll");
                if (File.Exists(d3d12Core)) vkd3dLen = new FileInfo(d3d12Core).Length;

                string d3d11 = Path.Combine(wineLib64, dxvkSubDir, "d3d11.dll");
                if (File.Exists(d3d11)) dxvkLen = new FileInfo(d3d11).Length;
            }

            string codegenSuffix = BuildShaderCodegenEnvStampSuffix(launcherEnv);
            return $"{protonPath}|{vkd3dLen}|{dxvkLen}{codegenSuffix}";
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

            if (File.Exists(Shaderless.Core.Games.RecorderManager.WarmedKeysPath(game.Id)) ||
                File.Exists(Path.Combine(Shaderless.Core.Games.RecorderManager.GameDir(game.Id), Shaderless.Engine.App.Recordings.WarmedFile)))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    public static string? GetRuntimeCacheStampMismatchReason(
        GameInfo game,
        string? homeDir = null,
        string? xdgDataHome = null,
        string? xdgConfigHome = null)
    {
        try
        {
            string targetCachePath;
            if (game.Source == GameSource.Steam)
            {
                if (homeDir == null && xdgDataHome == null)
                {
                    targetCachePath = GetSteamShaderCachePath(game.Id, game.InstallPath, game.PrefixPath);
                }
                else
                {
                    string sr = GetAllSteamRoots(homeDir, xdgDataHome).FirstOrDefault() ?? ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath);
                    targetCachePath = Path.Combine(sr, "steamapps", "shadercache", game.RawId);
                }
            }
            else
            {
                targetCachePath = GetNonSteamShaderCachePath(game.Id, game.PrefixPath);
            }
            string stampFile = Path.Combine(targetCachePath, "shaderless_runtime.stamp");
            string legacyStampFile = Path.Combine(targetCachePath, "scskiller_runtime.stamp");
            if (!File.Exists(stampFile) && File.Exists(legacyStampFile))
            {
                try { File.Move(legacyStampFile, stampFile, overwrite: false); } catch { }
            }
            if (!File.Exists(stampFile)) return null;

            string? protonPath = FindProtonBinaryForGame(game, preferredVersion: null, homeDir, xdgDataHome, xdgConfigHome);
            if (string.IsNullOrEmpty(protonPath)) return null;

            string? protonDir = Path.GetDirectoryName(protonPath);
            string? wineLib64 = protonDir == null ? null : new[]
            {
                Path.Combine(protonDir, "files", "lib64", "wine"),
                Path.Combine(protonDir, "dist", "lib64", "wine"),
                Path.Combine(protonDir, "files", "lib", "wine"),
                Path.Combine(protonDir, "dist", "lib", "wine")
            }.FirstOrDefault(Directory.Exists);

            var launcherEnv = GetEffectiveGameLauncherEnvironment(game, homeDir, xdgDataHome, xdgConfigHome);
            string vkd3dSubDir = ResolveProtonVkd3dSubDir(wineLib64, launcherEnv);

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

            bool disableNvidiaPromo = ShouldDisableNvidiaDescriptorPromotion(game);
            if (launcherEnv.TryGetValue("__GL_0x9e7d4a", out string? rawNvPromo) && !string.IsNullOrWhiteSpace(rawNvPromo))
            {
                disableNvidiaPromo = string.Equals(rawNvPromo.Trim(), "0", StringComparison.Ordinal);
            }
            string promoSuffix = disableNvidiaPromo ? "|nvpromo=0" : string.Empty;
            string codegenSuffix = BuildShaderCodegenEnvStampSuffix(launcherEnv);
            string currentStamp = $"{protonPath}|{d3d12CoreSig}|{nvidiaVer}{promoSuffix}{codegenSuffix}";
            string oldStamp = File.ReadAllText(stampFile).Trim();

            return CompareRuntimeCacheStamps(oldStamp, currentStamp);
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
                exe = Shaderless.Core.Games.GameEngineDetector.FindExecutable(game.InstallPath) ?? "";

            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                if (Shaderless.Core.Games.GameEngineDetector.TryGetUbisoftPlusExecutablePair(exe, out string plainExe, out string plusExe))
                {
                    var plainFi = new FileInfo(plainExe);
                    var plusFi = new FileInfo(plusExe);
                    exeLen = plainFi.Length + plusFi.Length;
                    exeTicks = Math.Max(plainFi.LastWriteTimeUtc.Ticks, plusFi.LastWriteTimeUtc.Ticks);
                }
                else
                {
                    var fi = new FileInfo(exe);
                    exeLen = fi.Length;
                    exeTicks = fi.LastWriteTimeUtc.Ticks;
                }
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
                        ext.Equals(".ba2", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".forge", StringComparison.OrdinalIgnoreCase))
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
        long maxNvidiaPartBytes = DefaultMaxNvidiaPartBytes,
        string? gameInstallPath = null,
        bool preserveExistingCache = false,
        bool disableNvidiaDescriptorPromotion = false,
        string shaderCodegenEnvSuffix = "")
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
            string promoSuffix = disableNvidiaDescriptorPromotion ? "|nvpromo=0" : string.Empty;
            string envSuffix = string.IsNullOrEmpty(shaderCodegenEnvSuffix) ? string.Empty : shaderCodegenEnvSuffix;
            string currentStamp = $"{protonPath}|{d3d12CoreSig}|{nvidiaVer}{promoSuffix}{envSuffix}";
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
                stampChanged = CompareRuntimeCacheStamps(oldStamp, currentStamp) != null;
            }

            File.WriteAllText(stampFile, currentStamp);

            // A runtime stamp change (driver / Proton / shader launch options) always starts from a clean cache. A full compile (!isIncrementalRun)
            // does too, unless the caller asks to keep what is already compiled for this same runtime (preserveExistingCache:
            // e.g. a compile planned from a new recording after a compile from the game files, whose cache still helps).
            bool resetCache = stampChanged || (!isIncrementalRun && !preserveExistingCache);

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
                    foreach (var workCacheDir in GetGameVkd3dWorkingDirs(gameInstallPath, gameExeDir))
                    {
                        try
                        {
                            string exeCache = Path.Combine(workCacheDir, "vkd3d-proton.cache");
                            if (File.Exists(exeCache)) File.Delete(exeCache);
                            string exeCacheWrite = Path.Combine(workCacheDir, "vkd3d-proton.cache.write");
                            if (File.Exists(exeCacheWrite)) File.Delete(exeCacheWrite);
                        }
                        catch { }
                    }
                    string prevManifest = Path.Combine(targetCachePath, InstalledManifest);
                    if (File.Exists(prevManifest))
                    {
                        foreach (var prevPath in File.ReadAllLines(prevManifest).Where(l => l.EndsWith(".cache", StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                if (File.Exists(prevPath)) File.Delete(prevPath);
                                DeleteVkd3dWriteCompanionsCaseInsensitive(prevPath);
                            }
                            catch { }
                        }
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
        long maxNvidiaPartBytes = DefaultMaxNvidiaPartBytes,
        string? gameInstallPath = null)
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
                    CleanupUnusedNvidiaDictionarySeed(dir);
                }
            }
        }
        catch { }

        // 3. Promote VKD3D-Proton .cache.write files into finalized .cache files and sync to gameExeDir (+ UE -basedir Binaries/Win64)
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

                var workCacheDirs = GetGameVkd3dWorkingDirs(gameInstallPath, gameExeDir);
                foreach (var workDir in workCacheDirs)
                {
                    // Also promote any runtime vkd3d-proton.cache.write left in the game's working directory
                    string dirWrite = Path.Combine(workDir, "vkd3d-proton.cache.write");
                    string dirFinal = Path.Combine(workDir, "vkd3d-proton.cache");
                    if (File.Exists(dirWrite) && new FileInfo(dirWrite).Length > 48)
                    {
                        try
                        {
                            MergeOrReplaceVkd3dCache(dirWrite, dirFinal);
                            File.Delete(dirWrite);
                        }
                        catch { }
                    }
                }

                if (workCacheDirs.Count > 0)
                {
                    var largestCache = Directory.GetFiles(vkd3dCache, "*.cache")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Length)
                        .FirstOrDefault();

                    if (largestCache != null && largestCache.Length > 48)
                    {
                        foreach (var workDir in workCacheDirs)
                        {
                            try
                            {
                                string gameCachePath = Path.Combine(workDir, "vkd3d-proton.cache");
                                MergeOrReplaceVkd3dCache(largestCache.FullName, gameCachePath);
                                // Keep the shadercache copy in sync with the merged working-dir copy so future incremental runs don't re-append
                                if (File.Exists(gameCachePath) && new FileInfo(gameCachePath).Length > largestCache.Length)
                                {
                                    File.Copy(gameCachePath, largestCache.FullName, overwrite: true);
                                    largestCache.Refresh();
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>Returns all directories where VKD3D-Proton may read/write a relative vkd3d-proton.cache for the game:
    /// the resolved executable directory, plus any Unreal &lt;Project&gt;/Binaries/Win64 working directory when the exe sits
    /// elsewhere (e.g. Phantom Blade Zero launching platform/steam/PBZ.exe with -basedir=../ProjectPBZ/Binaries/Win64).</summary>
    internal static IReadOnlyList<string> GetGameVkd3dWorkingDirs(string? installPath, string? gameExeDir)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddIfExists(string? candidate)
        {
            if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate)) return;
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (seen.Add(full)) dirs.Add(full);
        }

        AddIfExists(gameExeDir);

        if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath) && !string.IsNullOrEmpty(gameExeDir))
        {
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(installPath))
                {
                    if (Path.GetFileName(sub).Equals("Engine", StringComparison.OrdinalIgnoreCase))
                        continue;
                    AddIfExists(Path.Combine(sub, "Binaries", "Win64"));
                }
            }
            catch { }
        }

        return dirs;
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
            bool hasAntiCheat = Shaderless.Core.Games.RecorderManager.DetectAntiCheat(game) != Shaderless.Engine.AntiCheat.None;
            string? gameExeDir = !hasAntiCheat
                ? Shaderless.Core.Games.RecorderManager.ExeDir(game)
                : null;

            FinalizeDriverCaches(targetCachePath, vkd3dCache, glCache, gameExeDir, DefaultMaxNvidiaPartBytes, hasAntiCheat ? null : game.InstallPath);
            InstallCachesWhereGameReads(game, vkd3dCache, glCache);
        }
        catch { }
    }

    public static string SanitizeNvidiaGameTag(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return "game";
        string raw = gameId.StartsWith("flatpak_", StringComparison.OrdinalIgnoreCase)
            ? gameId["flatpak_".Length..]
            : gameId;

        var sb = new System.Text.StringBuilder(Math.Min(raw.Length, 24));
        foreach (char c in raw)
        {
            if (c >= '0' && c <= '9')
                sb.Append(c);
            else if (c >= 'a' && c <= 'z')
                sb.Append(c);
            else if (c >= 'A' && c <= 'Z')
                sb.Append((char)(c - 'A' + 'a'));

            if (sb.Length >= 24)
                break;
        }

        return sb.Length > 0 ? sb.ToString() : "game";
    }

    public static bool TryParseShaderlessGamePart(string pathOrName, out string? gameTag, out int partIndex)
    {
        gameTag = null;
        partIndex = -1;
        if (string.IsNullOrEmpty(pathOrName)) return false;

        string name = Path.GetFileName(pathOrName);
        if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".toc", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        const string plainPrefix = "shaderless_part";
        if (name.StartsWith(plainPrefix, StringComparison.Ordinal) && name.Length > plainPrefix.Length)
        {
            ReadOnlySpan<char> digits = name.AsSpan(plainPrefix.Length);
            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] < '0' || digits[i] > '9')
                    return false;
            }
            if (int.TryParse(digits, out int idx) && idx > 0)
            {
                partIndex = idx;
                return true;
            }
            return false;
        }

        const string gamePrefix = "shaderless_g";
        const string partMarker = "_part";
        if (name.StartsWith(gamePrefix, StringComparison.Ordinal))
        {
            int markerPos = name.IndexOf(partMarker, gamePrefix.Length, StringComparison.Ordinal);
            if (markerPos > gamePrefix.Length && markerPos + partMarker.Length < name.Length)
            {
                ReadOnlySpan<char> tagSpan = name.AsSpan(gamePrefix.Length, markerPos - gamePrefix.Length);
                for (int i = 0; i < tagSpan.Length; i++)
                {
                    char ch = tagSpan[i];
                    if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z')))
                        return false;
                }

                ReadOnlySpan<char> digits = name.AsSpan(markerPos + partMarker.Length);
                for (int i = 0; i < digits.Length; i++)
                {
                    if (digits[i] < '0' || digits[i] > '9')
                        return false;
                }

                if (int.TryParse(digits, out int idx) && idx > 0)
                {
                    gameTag = tagSpan.ToString();
                    partIndex = idx;
                    return true;
                }
            }
        }

        return false;
    }

    public static int TryParseShaderlessPartIndex(string pathOrName)
    {
        return TryParseShaderlessGamePart(pathOrName, out string? tag, out int idx) && tag == null
            ? idx
            : -1;
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
            && TryParseShaderlessGamePart(fileName, out _, out int partIdx)
            && partIdx > 0;
    }

    /// <summary>
    /// Reads the 32-bit Zstandard dictionary ID used by an NVIDIA NVDC <c>.bin/.toc</c> cache pair:
    /// first checks for an <c>_NVDICT_</c> TOC record (<c>ZSTD_MAGIC_DICTIONARY = 0xEC30A437</c> at <c>off + 32</c>),
    /// and if absent (e.g. multi-part spill files), inspects the first Zstandard-compressed shader record
    /// (<c>ZSTD_MAGICNUMBER = 0xFD2FB528</c> at <c>off + 36</c>, RFC 8878 frame header).
    /// Returns <c>0</c> if the cache is uncompressed, non-NVDC, or does not reference a Zstandard dictionary.
    /// </summary>
    public static uint TryGetNvidiaCacheDictId(string binPath, string tocPath)
    {
        if (string.IsNullOrEmpty(binPath) || string.IsNullOrEmpty(tocPath)) return 0;
        if (!File.Exists(binPath) || !File.Exists(tocPath)) return 0;

        try
        {
            long binLen = new FileInfo(binPath).Length;
            long tocLen = new FileInfo(tocPath).Length;
            if (binLen <= 64 || tocLen < 56 || (tocLen - 32) % 24 != 0)
                return 0;

            byte[] tocBytes = File.ReadAllBytes(tocPath);
            if (!IsNvdcHeader(tocBytes))
                return 0;

            ReadOnlySpan<byte> nvDictTag = "_NVDICT_"u8;
            using var binStream = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> probe = stackalloc byte[48];

            // 1. Check for explicit _NVDICT_ dictionary record
            for (int pos = 32; pos + 24 <= tocBytes.Length; pos += 24)
            {
                if (!tocBytes.AsSpan(pos + 8, 8).SequenceEqual(nvDictTag))
                    continue;

                uint off = BitConverter.ToUInt32(tocBytes, pos + 16);
                uint plen = BitConverter.ToUInt32(tocBytes, pos + 20);
                if (off < 32 || plen < 8 || off + 40L > binLen)
                    continue;

                binStream.Position = off;
                if (binStream.Read(probe[..40]) != 40)
                    continue;

                if (probe[0] == 0x9d && probe[1] == 0xa1 && probe[2] == 0x46 && probe[3] == 0x98 &&
                    BitConverter.ToUInt32(probe.Slice(32, 4)) == 0xEC30A437u)
                {
                    uint dictId = BitConverter.ToUInt32(probe.Slice(36, 4));
                    if (dictId != 0)
                        return dictId;
                }
            }

            // 2. Fallback: inspect up to the first 16 Zstd-compressed shader frames (for spill parts without _NVDICT_)
            int checkedFrames = 0;
            for (int pos = 32; pos + 24 <= tocBytes.Length && checkedFrames < 16; pos += 24, checkedFrames++)
            {
                uint off = BitConverter.ToUInt32(tocBytes, pos + 16);
                uint plen = BitConverter.ToUInt32(tocBytes, pos + 20);
                if (off < 32 || plen < 14 || off + 46L > binLen)
                    continue;

                binStream.Position = off;
                if (binStream.Read(probe[..46]) != 46)
                    continue;

                if (probe[0] != 0x9d || probe[1] != 0xa1 || probe[2] != 0x46 || probe[3] != 0x98)
                    continue;

                if (BitConverter.ToUInt32(probe.Slice(36, 4)) != 0xFD2FB528u)
                    continue;

                byte fhd = probe[40];
                int didFlag = fhd & 0x3;
                int singleSeg = (fhd >> 5) & 0x1;
                int didOff = 41 + (singleSeg == 1 ? 0 : 1);

                uint dictId = didFlag switch
                {
                    1 => probe[didOff],
                    2 => BitConverter.ToUInt16(probe.Slice(didOff, 2)),
                    3 => BitConverter.ToUInt32(probe.Slice(didOff, 4)),
                    _ => 0u
                };
                if (dictId != 0)
                    return dictId;
            }
        }
        catch { }

        return 0;
    }

    internal static bool HasNvidiaDictEntry(string tocPath)
    {
        if (string.IsNullOrEmpty(tocPath) || !File.Exists(tocPath)) return false;
        try
        {
            byte[] tocBytes = File.ReadAllBytes(tocPath);
            if (!IsNvdcHeader(tocBytes)) return false;
            ReadOnlySpan<byte> nvDictTag = "_NVDICT_"u8;
            for (int pos = 32; pos + 24 <= tocBytes.Length; pos += 24)
            {
                if (tocBytes.AsSpan(pos + 8, 8).SequenceEqual(nvDictTag))
                    return true;
            }
        }
        catch { }
        return false;
    }

    public sealed record NvidiaDictSeed(
        string RelativeLeafDir,
        uint DictId,
        byte[] Header32,
        byte[] TocEntry24,
        byte[] BinRecord);

    private static IReadOnlyList<string> GetNvidiaDictionaryDonorRoots(GameInfo game, string glCache, string gameCompatData)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddRoot(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            if (Directory.Exists(full) && seen.Add(full))
                roots.Add(full);
        }

        AddRoot(glCache);
        if (!string.IsNullOrEmpty(game.GlCachePath))
            AddRoot(Path.Combine(game.GlCachePath, "GLCache"));
        if (!string.IsNullOrEmpty(game.PrefixPath))
        {
            AddRoot(Path.Combine(game.PrefixPath, "GLCache"));
            AddRoot(Path.Combine(game.PrefixPath, "shadercache", "nvidiav1", "GLCache"));
        }
        if (!string.IsNullOrEmpty(gameCompatData))
        {
            AddRoot(Path.Combine(gameCompatData, "GLCache"));
            AddRoot(Path.Combine(gameCompatData, "shadercache", "nvidiav1", "GLCache"));
        }
        if (game.IsFlatpak || IsFlatpakGame(game.Id, game.InstallPath, game.PrefixPath))
            AddRoot(GetFlatpakLauncherNvidiaGlCachePath(game.Source));

        return roots;
    }

    private static bool TryExtractNvidiaDictRecord(
        string binPath,
        string tocPath,
        out uint dictId,
        out byte[] header32,
        out byte[] tocEntry24,
        out byte[] binRecord)
    {
        dictId = 0;
        header32 = Array.Empty<byte>();
        tocEntry24 = Array.Empty<byte>();
        binRecord = Array.Empty<byte>();

        try
        {
            if (!File.Exists(binPath) || !File.Exists(tocPath)) return false;
            long binLen = new FileInfo(binPath).Length;
            byte[] tocBytes = File.ReadAllBytes(tocPath);
            if (!IsNvdcHeader(tocBytes)) return false;

            ReadOnlySpan<byte> nvDictTag = "_NVDICT_"u8;
            for (int pos = 32; pos + 24 <= tocBytes.Length; pos += 24)
            {
                if (!tocBytes.AsSpan(pos + 8, 8).SequenceEqual(nvDictTag))
                    continue;

                uint off = BitConverter.ToUInt32(tocBytes, pos + 16);
                uint plen = BitConverter.ToUInt32(tocBytes, pos + 20);
                long recLen = 32L + plen;
                if (off < 32 || plen < 8 || recLen > 16 * 1024 * 1024 || off + recLen > binLen)
                    continue;

                byte[] rec = new byte[recLen];
                using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Position = off;
                int read = 0;
                while (read < rec.Length)
                {
                    int n = fs.Read(rec, read, rec.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != rec.Length) continue;

                if (rec[0] != 0x9d || rec[1] != 0xa1 || rec[2] != 0x46 || rec[3] != 0x98)
                    continue;
                if (BitConverter.ToUInt32(rec, 28) != plen)
                    continue;
                if (BitConverter.ToUInt32(rec, 32) != 0xEC30A437u)
                    continue;

                uint id = BitConverter.ToUInt32(rec, 36);
                if (id == 0) continue;

                header32 = tocBytes[..32];
                tocEntry24 = new byte[24];
                Buffer.BlockCopy(tocBytes, pos, tocEntry24, 0, 16);
                BitConverter.TryWriteBytes(tocEntry24.AsSpan(16, 4), 32u);
                BitConverter.TryWriteBytes(tocEntry24.AsSpan(20, 4), plen);
                binRecord = rec;
                dictId = id;
                return true;
            }
        }
        catch { }

        return false;
    }

    public static NvidiaDictSeed? CaptureNvidiaDictionarySeed(IEnumerable<string> glCacheRoots)
    {
        try
        {
            var leafCandidates = new Dictionary<string, (DateTime NewestUtc, List<(string Bin, string Toc)> Pairs)>(StringComparer.Ordinal);

            foreach (var root in glCacheRoots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                foreach (var toc in Directory.EnumerateFiles(root, "*.toc", SearchOption.AllDirectories))
                {
                    string bin = Path.ChangeExtension(toc, ".bin");
                    if (!File.Exists(bin)) continue;
                    var binFi = new FileInfo(bin);
                    if (binFi.Length <= 64) continue;
                    if (TryReadValidNvdcHeader(toc) == null) continue;

                    string? dir = Path.GetDirectoryName(toc);
                    if (string.IsNullOrEmpty(dir)) continue;
                    string relLeaf = Path.GetRelativePath(root, dir);
                    if (string.IsNullOrEmpty(relLeaf) || relLeaf == ".") continue;

                    DateTime mtime = binFi.LastWriteTimeUtc;
                    if (!leafCandidates.TryGetValue(relLeaf, out var entry))
                    {
                        entry = (mtime, new List<(string, string)>());
                    }
                    else if (mtime > entry.NewestUtc)
                    {
                        entry.NewestUtc = mtime;
                    }
                    entry.Pairs.Add((bin, toc));
                    leafCandidates[relLeaf] = entry;
                }
            }

            if (leafCandidates.Count == 0)
                return null;

            var activeLeaf = leafCandidates
                .OrderByDescending(kv => kv.Value.NewestUtc)
                .First();

            var bytesByDict = new Dictionary<uint, long>();
            var seedsByDict = new Dictionary<uint, NvidiaDictSeed>();
            var seenInodes = new HashSet<(uint, uint, ulong)>();

            foreach (var (bin, toc) in activeLeaf.Value.Pairs)
            {
                uint dId = TryGetNvidiaCacheDictId(bin, toc);
                if (dId == 0) continue;

                long addBytes = 0;
                if (TryGetLinuxFileIdentity(bin, out var ino, out long statLen))
                {
                    if (seenInodes.Add(ino)) addBytes = statLen;
                }
                else
                {
                    addBytes = new FileInfo(bin).Length;
                }

                bytesByDict[dId] = bytesByDict.GetValueOrDefault(dId) + addBytes;

                if (!seedsByDict.ContainsKey(dId) &&
                    TryExtractNvidiaDictRecord(bin, toc, out uint extractedId, out byte[] hdr32, out byte[] toc24, out byte[] binRec) &&
                    extractedId == dId)
                {
                    seedsByDict[dId] = new NvidiaDictSeed(activeLeaf.Key, dId, hdr32, toc24, binRec);
                }
            }

            if (seedsByDict.Count == 0)
                return null;

            return seedsByDict.Values
                .OrderByDescending(s => bytesByDict.GetValueOrDefault(s.DictId))
                .First();
        }
        catch
        {
            return null;
        }
    }

    public static bool ApplyNvidiaDictionarySeed(string glCache, NvidiaDictSeed? seed)
    {
        if (seed == null || string.IsNullOrWhiteSpace(glCache))
            return false;

        try
        {
            if (Directory.Exists(glCache))
            {
                foreach (var existingToc in Directory.EnumerateFiles(glCache, "*.toc", SearchOption.AllDirectories))
                {
                    if (HasNvidiaDictEntry(existingToc))
                        return false;
                }
            }

            string leafDir = Path.Combine(glCache, seed.RelativeLeafDir);
            Directory.CreateDirectory(leafDir);
            string mergedBin = Path.Combine(leafDir, "steamapp_merged_shader_cache.bin");
            string mergedToc = Path.Combine(leafDir, "steamapp_merged_shader_cache.toc");

            using (var binFs = new FileStream(mergedBin, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                binFs.Write(seed.Header32, 0, 32);
                binFs.Write(seed.BinRecord, 0, seed.BinRecord.Length);
            }

            using (var tocFs = new FileStream(mergedToc, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                tocFs.Write(seed.Header32, 0, 32);
                tocFs.Write(seed.TocEntry24, 0, 24);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SeedNvidiaDictionaryForWarmer(string glCache, IEnumerable<string>? donorGlCacheRoots = null)
    {
        var roots = new List<string>();
        if (donorGlCacheRoots != null) roots.AddRange(donorGlCacheRoots);
        if (!string.IsNullOrWhiteSpace(glCache)) roots.Add(glCache);
        var seed = CaptureNvidiaDictionarySeed(roots);
        return ApplyNvidiaDictionarySeed(glCache, seed);
    }

    /// <summary>
    /// Determines whether <c>shaderless_warm.exe</c> must pass <c>__GL_0x9e7d4a=0</c> (<c>enableDescriptorPromotion = 0</c>)
    /// so NVIDIA's <c>libnvidia-glcore.so</c> produces identical 64-bit <c>k1</c> TOC hashes as the real game.
    /// Layer 1 inspects real-game (non-Shaderless) NVDC caches on disk via <c>libzstd.so.1</c> for D3D12 (<c>vkd3d-proton</c>)
    /// shaders; Layer 2 falls back to static Epic / EOS Vulkan implicit-layer detection when no real-game donor cache exists yet.
    /// </summary>
    public static bool ShouldDisableNvidiaDescriptorPromotion(
        GameInfo game,
        string? shaderlessGlCache = null,
        IEnumerable<string>? donorGlCacheRoots = null,
        string? gameCompatData = null)
    {
        try
        {
            string compatData = gameCompatData ?? string.Empty;
            if (string.IsNullOrEmpty(compatData))
            {
                if (!string.IsNullOrEmpty(game.PrefixPath))
                {
                    compatData = game.PrefixPath.EndsWith("/pfx", StringComparison.Ordinal) ||
                                 game.PrefixPath.EndsWith("\\pfx", StringComparison.Ordinal)
                        ? Path.GetDirectoryName(game.PrefixPath)!
                        : game.PrefixPath;
                }
                else if (game.Source == GameSource.Steam)
                {
                    string compatClient = ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath);
                    compatData = Path.Combine(compatClient, "steamapps", "compatdata", game.RawId);
                }
            }

            string targetGlCache = shaderlessGlCache ?? string.Empty;
            if (string.IsNullOrEmpty(targetGlCache))
            {
                string targetCachePath = game.Source == GameSource.Steam
                    ? GetSteamShaderCachePath(game.Id, game.InstallPath, game.PrefixPath)
                    : GetNonSteamShaderCachePath(game.Id, game.PrefixPath);
                if (!string.IsNullOrEmpty(targetCachePath))
                    targetGlCache = Path.Combine(targetCachePath, "nvidiav1", "GLCache");
            }

            var roots = donorGlCacheRoots != null
                ? donorGlCacheRoots.ToList()
                : GetNvidiaDictionaryDonorRoots(game, targetGlCache, compatData).ToList();

            bool? fromCache = TryDetectNvidiaDescriptorPromotionDisabledFromCaches(game, targetGlCache, roots, compatData);
            if (fromCache.HasValue)
                return fromCache.Value;

            return HasStaticNvidiaDescriptorPromotionDisableSignal(game, compatData);
        }
        catch
        {
            return false;
        }
    }

    private static bool? TryDetectNvidiaDescriptorPromotionDisabledFromCaches(
        GameInfo game,
        string? shaderlessGlCache,
        IReadOnlyList<string> donorGlCacheRoots,
        string? gameCompatData)
    {
        if (!OperatingSystem.IsLinux() || donorGlCacheRoots.Count == 0)
            return null;

        try
        {
            var shaderlessOwnedPaths = new HashSet<string>(StringComparer.Ordinal);
            var shaderlessOwnedInodes = new HashSet<(uint, uint, ulong)>();

            void MarkOwnedFile(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    string full = Path.GetFullPath(path);
                    shaderlessOwnedPaths.Add(full);
                    if (File.Exists(full) && TryGetLinuxFileIdentity(full, out var ino, out _))
                        shaderlessOwnedInodes.Add(ino);
                }
                catch { }
            }

            bool skipShaderlessMergedInTarget = game.Source != GameSource.Steam || HasShaderlessCompiledArtifacts(game);
            if (skipShaderlessMergedInTarget && !string.IsNullOrEmpty(shaderlessGlCache) && Directory.Exists(shaderlessGlCache))
            {
                foreach (var f in Directory.EnumerateFiles(shaderlessGlCache, "*", SearchOption.AllDirectories))
                {
                    string fn = Path.GetFileName(f);
                    if (!fn.StartsWith("steamapp_shader_cache", StringComparison.Ordinal) &&
                        !fn.StartsWith("steam_shader_cache.", StringComparison.Ordinal))
                    {
                        MarkOwnedFile(f);
                    }
                }
            }

            if (game.Source != GameSource.Steam)
            {
                var scDirs = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(game.PrefixPath))
                    scDirs.Add(Path.Combine(game.PrefixPath, "shadercache"));
                if (!string.IsNullOrEmpty(gameCompatData))
                    scDirs.Add(Path.Combine(gameCompatData, "shadercache"));

                foreach (var scDir in scDirs)
                {
                    if (!Directory.Exists(scDir)) continue;
                    foreach (var sub in Directory.EnumerateDirectories(scDir))
                    {
                        string manifest = Path.Combine(sub, InstalledManifest);
                        if (File.Exists(manifest))
                        {
                            foreach (var line in File.ReadAllLines(manifest))
                            {
                                if (!string.IsNullOrWhiteSpace(line))
                                    MarkOwnedFile(line.Trim());
                            }
                        }

                        string subGl = Path.Combine(sub, "nvidiav1", "GLCache");
                        if (Directory.Exists(subGl))
                        {
                            foreach (var f in Directory.EnumerateFiles(subGl, "*.bin", SearchOption.AllDirectories))
                            {
                                MarkOwnedFile(f);
                                MarkOwnedFile(Path.ChangeExtension(f, ".toc"));
                            }
                        }
                    }
                }
            }

            var leafCandidates = new Dictionary<string, (DateTime NewestUtc, List<(string Bin, string Toc, DateTime MtimeUtc, long Length, bool IsDonor)> Pairs)>(StringComparer.Ordinal);
            var seenDonorInodes = new HashSet<(uint, uint, ulong)>();

            foreach (var root in donorGlCacheRoots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                foreach (var toc in Directory.EnumerateFiles(root, "*.toc", SearchOption.AllDirectories))
                {
                    string bin = Path.ChangeExtension(toc, ".bin");
                    if (!File.Exists(bin)) continue;
                    var binFi = new FileInfo(bin);
                    if (binFi.Length <= 64) continue;
                    if (TryReadValidNvdcHeader(toc) == null) continue;

                    string? dir = Path.GetDirectoryName(toc);
                    if (string.IsNullOrEmpty(dir)) continue;
                    string relLeaf = Path.GetRelativePath(root, dir);
                    if (string.IsNullOrEmpty(relLeaf) || relLeaf == ".") continue;

                    string fn = Path.GetFileName(bin);
                    string fullBin = Path.GetFullPath(bin);
                    bool isDonor = !TryParseShaderlessGamePart(fn, out _, out _) &&
                                   !shaderlessOwnedPaths.Contains(fullBin);
                    if (isDonor && TryGetLinuxFileIdentity(fullBin, out var binIno, out _))
                    {
                        if (shaderlessOwnedInodes.Contains(binIno) || !seenDonorInodes.Add(binIno))
                            isDonor = false;
                    }

                    DateTime mtime = binFi.LastWriteTimeUtc;
                    if (!leafCandidates.TryGetValue(relLeaf, out var entry))
                    {
                        entry = (mtime, new List<(string, string, DateTime, long, bool)>());
                    }
                    else if (mtime > entry.NewestUtc)
                    {
                        entry.NewestUtc = mtime;
                    }
                    entry.Pairs.Add((fullBin, Path.GetFullPath(toc), mtime, binFi.Length, isDonor));
                    leafCandidates[relLeaf] = entry;
                }
            }

            if (leafCandidates.Count == 0)
                return null;

            foreach (var activeLeaf in leafCandidates.OrderByDescending(kv => kv.Value.NewestUtc))
            {
                var donorPairs = activeLeaf.Value.Pairs
                    .Where(p => p.IsDonor)
                    .OrderByDescending(p => p.MtimeUtc)
                    .ThenByDescending(p => p.Length)
                    .ToList();

                if (donorPairs.Count == 0)
                    continue;

                var rawDictsById = new Dictionary<uint, byte[]>();
                byte[]? firstRawDict = null;
                foreach (var pair in activeLeaf.Value.Pairs)
                {
                    if (TryExtractNvidiaDictRecord(pair.Bin, pair.Toc, out uint dId, out _, out _, out byte[] binRec) &&
                        dId != 0 && binRec.Length > 32)
                    {
                        if (!rawDictsById.ContainsKey(dId))
                        {
                            byte[] rawDict = binRec[32..];
                            rawDictsById[dId] = rawDict;
                            firstRawDict ??= rawDict;
                        }
                    }
                }

                if (firstRawDict == null)
                    continue;

                var ddictHandles = new Dictionary<uint, IntPtr>();
                IntPtr fallbackDdict = IntPtr.Zero;
                IntPtr dctx = IntPtr.Zero;

                try
                {
                    dctx = ZSTD_createDCtx();
                    if (dctx == IntPtr.Zero)
                        return null;

                    byte[] decompressBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(2 * 1024 * 1024);
                    try
                    {
                        int zeroCount = 0;
                        int nonZeroCount = 0;

                        foreach (var donor in donorPairs)
                        {
                            uint fileDictId = TryGetNvidiaCacheDictId(donor.Bin, donor.Toc);
                            IntPtr ddict = IntPtr.Zero;
                            if (fileDictId != 0 && rawDictsById.TryGetValue(fileDictId, out byte[]? matchedDict))
                            {
                                if (!ddictHandles.TryGetValue(fileDictId, out ddict))
                                {
                                    ddict = ZSTD_createDDict(matchedDict, (nuint)matchedDict.Length);
                                    if (ddict != IntPtr.Zero)
                                        ddictHandles[fileDictId] = ddict;
                                }
                            }
                            if (ddict == IntPtr.Zero)
                            {
                                if (fallbackDdict == IntPtr.Zero)
                                    fallbackDdict = ZSTD_createDDict(firstRawDict, (nuint)firstRawDict.Length);
                                ddict = fallbackDdict;
                            }
                            if (ddict == IntPtr.Zero)
                                continue;

                            TrySampleD3d12DescriptorPromotionCounts(
                                donor.Bin,
                                donor.Toc,
                                ddict,
                                dctx,
                                decompressBuf,
                                ref zeroCount,
                                ref nonZeroCount,
                                maxSamplesPerToc: 48);

                            if (zeroCount + nonZeroCount >= 96)
                                break;
                        }

                        if (zeroCount + nonZeroCount >= 4)
                            return zeroCount > nonZeroCount;
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(decompressBuf);
                    }
                }
                finally
                {
                    if (dctx != IntPtr.Zero)
                        ZSTD_freeDCtx(dctx);
                    foreach (var h in ddictHandles.Values)
                    {
                        if (h != IntPtr.Zero)
                            ZSTD_freeDDict(h);
                    }
                    if (fallbackDdict != IntPtr.Zero && !ddictHandles.ContainsValue(fallbackDdict))
                        ZSTD_freeDDict(fallbackDdict);
                }
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static void TrySampleD3d12DescriptorPromotionCounts(
        string binPath,
        string tocPath,
        IntPtr ddict,
        IntPtr dctx,
        byte[] decompressBuf,
        ref int zeroCount,
        ref int nonZeroCount,
        int maxSamplesPerToc)
    {
        byte[] tocBytes;
        try
        {
            tocBytes = File.ReadAllBytes(tocPath);
        }
        catch
        {
            return;
        }

        if (!IsNvdcHeader(tocBytes))
            return;

        int totalEntries = (tocBytes.Length - 32) / 24;
        if (totalEntries <= 0)
            return;

        int step = Math.Max(1, totalEntries / Math.Max(1, maxSamplesPerToc));
        ReadOnlySpan<byte> nvDictTag = "_NVDICT_"u8;
        ReadOnlySpan<byte> d3d12DescBufTag = "4b51426e5b24e765: true"u8;
        ReadOnlySpan<byte> promoKeyTag = "03105bcbd882737e:"u8;

        try
        {
            using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long binLen = fs.Length;

            for (int idx = 0; idx < totalEntries; idx += step)
            {
                int pos = 32 + idx * 24;
                if (pos + 24 > tocBytes.Length)
                    break;

                if (tocBytes.AsSpan(pos + 8, 8).SequenceEqual(nvDictTag))
                    continue;

                uint off = BitConverter.ToUInt32(tocBytes, pos + 16);
                uint plen = BitConverter.ToUInt32(tocBytes, pos + 20);
                long recLen = 32L + plen;
                if (off < 32 || plen < 16 || plen > 2 * 1024 * 1024 || off + recLen > binLen)
                    continue;

                byte[] rec = System.Buffers.ArrayPool<byte>.Shared.Rent((int)recLen);
                try
                {
                    fs.Position = off;
                    int read = 0;
                    int targetRead = (int)recLen;
                    while (read < targetRead)
                    {
                        int n = fs.Read(rec, read, targetRead - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != targetRead)
                        continue;

                    if (rec[0] != 0x9d || rec[1] != 0xa1 || rec[2] != 0x46 || rec[3] != 0x98)
                        continue;
                    if (BitConverter.ToUInt32(rec, 36) != 0xFD2FB528u)
                        continue;

                    var pin = System.Runtime.InteropServices.GCHandle.Alloc(rec, System.Runtime.InteropServices.GCHandleType.Pinned);
                    nuint decompLen;
                    try
                    {
                        IntPtr srcPtr = IntPtr.Add(pin.AddrOfPinnedObject(), 36);
                        decompLen = ZSTD_decompress_usingDDict(
                            dctx,
                            decompressBuf,
                            (nuint)decompressBuf.Length,
                            srcPtr,
                            (nuint)(plen - 4),
                            ddict);
                    }
                    finally
                    {
                        pin.Free();
                    }

                    if (ZSTD_isError(decompLen) != 0 || decompLen == 0 || decompLen > (nuint)decompressBuf.Length)
                        continue;

                    ReadOnlySpan<byte> text = decompressBuf.AsSpan(0, (int)decompLen);
                    if (text.IndexOf(d3d12DescBufTag) < 0)
                        continue;

                    int keyPos = text.IndexOf(promoKeyTag);
                    if (keyPos < 0)
                        continue;

                    int p = keyPos + promoKeyTag.Length;
                    while (p < text.Length && (text[p] == (byte)' ' || text[p] == (byte)'\t'))
                        p++;

                    long val = 0;
                    int digits = 0;
                    while (p < text.Length && text[p] >= (byte)'0' && text[p] <= (byte)'9')
                    {
                        val = val * 10 + (text[p] - (byte)'0');
                        digits++;
                        p++;
                    }

                    if (digits > 0)
                    {
                        if (val == 0) zeroCount++;
                        else nonZeroCount++;
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rec);
                }
            }
        }
        catch { }
    }

    public static bool HasStaticNvidiaDescriptorPromotionDisableSignal(GameInfo game, string? gameCompatData = null)
    {
        var prefixCandidates = new HashSet<string>(StringComparer.Ordinal);
        void AddPrefix(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
                if (Directory.Exists(full))
                {
                    prefixCandidates.Add(full);
                    string pfxSub = Path.Combine(full, "pfx");
                    if (Directory.Exists(pfxSub))
                        prefixCandidates.Add(pfxSub);
                }
            }
            catch { }
        }

        AddPrefix(game.PrefixPath);
        AddPrefix(gameCompatData);

        foreach (var pfx in prefixCandidates)
        {
            string sysReg = Path.Combine(pfx, "system.reg");
            if (HasEnabledEosVulkanImplicitLayerInRegistry(sysReg))
                return true;

            if (game.Source != GameSource.Steam)
            {
                string driveC = Path.Combine(pfx, "drive_c");
                if (File.Exists(Path.Combine(driveC, "Program Files", "Epic Games", "Launcher", "Portal", "Extras", "Overlay", "EOSOVH-Win64-Shipping.dll")) ||
                    Directory.Exists(Path.Combine(driveC, "Program Files (x86)", "Epic Games", "Epic Online Services")))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool HasEnabledEosVulkanImplicitLayerInRegistry(string systemRegPath)
    {
        if (string.IsNullOrWhiteSpace(systemRegPath) || !File.Exists(systemRegPath))
            return false;

        try
        {
            bool inImplicitLayers = false;
            foreach (var rawLine in File.ReadLines(systemRegPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line[0] == '[')
                {
                    inImplicitLayers = line.StartsWith("[Software\\\\Khronos\\\\Vulkan\\\\ImplicitLayers]", StringComparison.OrdinalIgnoreCase) ||
                                       line.StartsWith("[Software\\Khronos\\Vulkan\\ImplicitLayers]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inImplicitLayers &&
                    line.Contains("EOSOverlayVkLayer", StringComparison.OrdinalIgnoreCase) &&
                    line.EndsWith("=dword:00000000", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static void CleanupUnusedNvidiaDictionarySeed(string leafDir)
    {
        try
        {
            if (string.IsNullOrEmpty(leafDir) || !Directory.Exists(leafDir)) return;
            var bins = Directory.GetFiles(leafDir, "*.bin");
            if (bins.Length != 1) return;

            string mergedBin = Path.Combine(leafDir, "steamapp_merged_shader_cache.bin");
            string mergedToc = Path.Combine(leafDir, "steamapp_merged_shader_cache.toc");
            if (!File.Exists(mergedBin) || !File.Exists(mergedToc)) return;

            if (new FileInfo(mergedToc).Length == 56 && HasNvidiaDictEntry(mergedToc))
            {
                File.Delete(mergedBin);
                File.Delete(mergedToc);
                if (!Directory.EnumerateFileSystemEntries(leafDir).Any())
                {
                    Directory.Delete(leafDir, false);
                    string? parent = Path.GetDirectoryName(leafDir);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                        Directory.Delete(parent, false);
                }
            }
        }
        catch { }
    }

    private static string? FindNewestNvidiaLeafDirectory(string? glCacheRoot)
    {
        if (string.IsNullOrWhiteSpace(glCacheRoot) || !Directory.Exists(glCacheRoot))
            return null;

        try
        {
            string? bestDir = null;
            DateTime bestMtime = DateTime.MinValue;

            foreach (var binFile in Directory.EnumerateFiles(glCacheRoot, "*.bin", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(binFile);
                if (fi.Length <= 64) continue;
                string tocFile = Path.ChangeExtension(binFile, ".toc");
                if (!File.Exists(tocFile)) continue;

                string? dir = Path.GetDirectoryName(binFile);
                if (string.IsNullOrEmpty(dir)) continue;

                if (bestDir == null || fi.LastWriteTimeUtc > bestMtime)
                {
                    bestDir = dir;
                    bestMtime = fi.LastWriteTimeUtc;
                }
            }

            return bestDir;
        }
        catch
        {
            return null;
        }
    }

    public static string BuildNvidiaReadOnlyAppNames(
        string? glCacheRoot,
        string? existingEnvValue = null,
        string? preferredGameTag = null)
    {
        var extraTokens = new List<string>();
        if (!string.IsNullOrWhiteSpace(existingEnvValue))
        {
            foreach (var tok in existingEnvValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (tok.Equals("steamapp_merged_shader_cache", StringComparison.Ordinal) ||
                    tok.Equals("steam_shader_cache", StringComparison.Ordinal) ||
                    TryParseShaderlessGamePart(tok, out _, out _))
                {
                    continue;
                }
                extraTokens.Add(tok);
            }
        }

        string? leafDir = FindNewestNvidiaLeafDirectory(glCacheRoot);
        var gamePartsByTag = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var gameBytesByTag = new Dictionary<string, long>(StringComparer.Ordinal);
        var plainParts = new SortedSet<int>();
        long plainBytes = 0;

        if (!string.IsNullOrEmpty(leafDir) && Directory.Exists(leafDir))
        {
            try
            {
                string mergedPath = Path.Combine(leafDir, "steamapp_merged_shader_cache.bin");
                if (File.Exists(mergedPath) && File.Exists(Path.ChangeExtension(mergedPath, ".toc")))
                {
                    long mLen = new FileInfo(mergedPath).Length;
                    if (mLen > 64) plainBytes += mLen;
                }

                foreach (var binFile in Directory.EnumerateFiles(leafDir, "shaderless_*.bin"))
                {
                    if (!TryParseShaderlessGamePart(binFile, out string? tag, out int pIdx) || pIdx <= 0)
                        continue;

                    var fi = new FileInfo(binFile);
                    if (fi.Length <= 64 || !File.Exists(Path.ChangeExtension(binFile, ".toc")))
                        continue;

                    if (tag == null)
                    {
                        plainParts.Add(pIdx);
                        plainBytes += fi.Length;
                    }
                    else
                    {
                        if (!gamePartsByTag.TryGetValue(tag, out var set))
                        {
                            set = new SortedSet<int>();
                            gamePartsByTag[tag] = set;
                        }
                        set.Add(pIdx);
                        gameBytesByTag[tag] = gameBytesByTag.GetValueOrDefault(tag) + fi.Length;
                    }
                }
            }
            catch { }
        }

        // Order candidate read-only caches with Shaderless caches FIRST so Shaderless's _NVDICT_ always wins
        var candidates = new List<string>();
        string normPreferred = !string.IsNullOrWhiteSpace(preferredGameTag)
            ? SanitizeNvidiaGameTag(preferredGameTag)
            : string.Empty;

        if (!string.IsNullOrEmpty(normPreferred) && gamePartsByTag.TryGetValue(normPreferred, out var prefSet))
        {
            foreach (int idx in prefSet)
                candidates.Add($"shaderless_g{normPreferred}_part{idx}");
        }

        var remainingGroups = new List<(string? Tag, long Bytes)>();
        remainingGroups.Add((null, plainBytes));
        foreach (var kv in gamePartsByTag)
        {
            if (!string.Equals(kv.Key, normPreferred, StringComparison.Ordinal))
                remainingGroups.Add((kv.Key, gameBytesByTag.GetValueOrDefault(kv.Key)));
        }

        foreach (var (grpTag, _) in remainingGroups
                     .OrderByDescending(g => g.Bytes)
                     .ThenBy(g => g.Tag ?? string.Empty, StringComparer.Ordinal))
        {
            if (grpTag == null)
            {
                candidates.Add("steamapp_merged_shader_cache");
                foreach (int idx in plainParts)
                    candidates.Add($"shaderless_part{idx}");
            }
            else if (gamePartsByTag.TryGetValue(grpTag, out var grpSet))
            {
                foreach (int idx in grpSet)
                    candidates.Add($"shaderless_g{grpTag}_part{idx}");
            }
        }

        candidates.AddRange(extraTokens);
        candidates.Add("steam_shader_cache");

        // Filter out any on-disk read-only cache whose Zstd dictionary ID conflicts with the highest-priority active dictionary
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        uint activeDictId = 0;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
                continue;

            if (!string.IsNullOrEmpty(leafDir))
            {
                string binPath = Path.Combine(leafDir, candidate + ".bin");
                string tocPath = Path.Combine(leafDir, candidate + ".toc");
                if (File.Exists(binPath) && File.Exists(tocPath) && new FileInfo(binPath).Length > 64)
                {
                    uint dictId = TryGetNvidiaCacheDictId(binPath, tocPath);
                    if (dictId != 0)
                    {
                        if (activeDictId == 0)
                        {
                            activeDictId = dictId;
                        }
                        else if (dictId != activeDictId)
                        {
                            // Incompatible _NVDICT_ dictionary ID: exclude so libnvidia-glcore.so never rejects active cache!
                            continue;
                        }
                    }
                }
            }

            names.Add(candidate);
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

                bool hasParts = Directory.EnumerateFiles(dir, "shaderless_*.bin")
                    .Any(f => TryParseShaderlessGamePart(f, out _, out int pIdx) && pIdx > 0 && new FileInfo(f).Length > 64 && File.Exists(Path.ChangeExtension(f, ".toc")));

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

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern IntPtr ZSTD_createDDict(byte[] dictBuffer, nuint dictSize);

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern nuint ZSTD_freeDDict(IntPtr ddict);

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern IntPtr ZSTD_createDCtx();

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern nuint ZSTD_freeDCtx(IntPtr dctx);

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern nuint ZSTD_decompress_usingDDict(
        IntPtr dctx,
        byte[] dst,
        nuint dstCapacity,
        IntPtr src,
        nuint srcSize,
        IntPtr ddict);

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern uint ZSTD_isError(nuint code);

    [System.Runtime.InteropServices.DllImport("libc.so.6", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int dirfd,
        string pathname,
        int flags,
        uint mask,
        [System.Runtime.InteropServices.Out] byte[] statxbuf);

    [ThreadStatic]
    private static byte[]? t_statxBuf;

    internal static bool TryGetLinuxFileIdentity(string filePath, out (uint DevMajor, uint DevMinor, ulong Ino) id, out long sizeBytes)
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
    const string NvidiaGamesMapFile = "shaderless_games.tsv";
    private static readonly string[] NonSteamDriverCacheSubDirs =
        { "AMDv1", "mesa_shader_cache_sf", "mesa_shader_cache_db", "mesa_shader_cache", "DXVK_state_cache" };

    private static void UpsertNvidiaGameTagMap(string glCacheTargetDir, string gameTag, string? gameName)
    {
        if (string.IsNullOrWhiteSpace(glCacheTargetDir) || string.IsNullOrWhiteSpace(gameTag) || string.IsNullOrWhiteSpace(gameName))
            return;

        try
        {
            Directory.CreateDirectory(glCacheTargetDir);
            string mapPath = Path.Combine(glCacheTargetDir, NvidiaGamesMapFile);
            string cleanName = gameName.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (cleanName.Length == 0) return;

            var lines = new List<string>();
            if (File.Exists(mapPath))
            {
                foreach (var line in File.ReadAllLines(mapPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int tab = line.IndexOf('\t');
                    string existingTag = tab >= 0 ? line[..tab].Trim() : line.Trim();
                    if (!string.Equals(existingTag, gameTag, StringComparison.Ordinal))
                        lines.Add(line);
                }
            }
            lines.Add($"{gameTag}\t{cleanName}");
            File.WriteAllLines(mapPath, lines);
        }
        catch { }
    }

    private static void RemoveNvidiaGameTagFromMap(string glCacheTargetDir, string gameTag)
    {
        if (string.IsNullOrWhiteSpace(glCacheTargetDir) || string.IsNullOrWhiteSpace(gameTag))
            return;

        try
        {
            string mapPath = Path.Combine(glCacheTargetDir, NvidiaGamesMapFile);
            if (!File.Exists(mapPath)) return;

            var remaining = new List<string>();
            foreach (var line in File.ReadAllLines(mapPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int tab = line.IndexOf('\t');
                string existingTag = tab >= 0 ? line[..tab].Trim() : line.Trim();
                if (!string.Equals(existingTag, gameTag, StringComparison.Ordinal))
                    remaining.Add(line);
            }

            if (remaining.Count == 0)
                File.Delete(mapPath);
            else
                File.WriteAllLines(mapPath, remaining);
        }
        catch { }
    }

    private static void LinkNonSteamNvidiaCacheParts(
        string srcGlCacheRoot,
        string destGlCacheRoot,
        GameInfo game,
        List<string> installed)
    {
        if (!Directory.Exists(srcGlCacheRoot)) return;

        string tag = SanitizeNvidiaGameTag(game.Id);
        static int OrderSourceBin(string path)
        {
            string fn = Path.GetFileName(path);
            if (fn.Equals("steamapp_merged_shader_cache.bin", StringComparison.Ordinal))
                return 0;
            if (TryParseShaderlessGamePart(fn, out _, out int pIdx) && pIdx > 0)
                return pIdx;
            if (fn.Equals("steam_shader_cache.bin", StringComparison.Ordinal))
                return 1_000_000;
            return 2_000_000;
        }

        var binsByRelDir = Directory.GetFiles(srcGlCacheRoot, "*.bin", SearchOption.AllDirectories)
            .Where(f =>
            {
                string fn = Path.GetFileName(f);
                return IsNvidiaReadOnlyCacheFileName(fn) &&
                       new FileInfo(f).Length > 0 &&
                       File.Exists(Path.ChangeExtension(f, ".toc"));
            })
            .GroupBy(f => Path.GetDirectoryName(Path.GetRelativePath(srcGlCacheRoot, f)) ?? string.Empty, StringComparer.Ordinal);

        bool linkedAny = false;
        foreach (var grp in binsByRelDir)
        {
            var orderedBins = grp.OrderBy(OrderSourceBin).ThenBy(Path.GetFileName, StringComparer.Ordinal).ToList();
            int partNum = 1;
            foreach (var srcBin in orderedBins)
            {
                string srcToc = Path.ChangeExtension(srcBin, ".toc");
                string destLeaf = string.IsNullOrEmpty(grp.Key)
                    ? destGlCacheRoot
                    : Path.Combine(destGlCacheRoot, grp.Key);

                string destBase = Path.Combine(destLeaf, $"shaderless_g{tag}_part{partNum}");
                string destBin = destBase + ".bin";
                string destToc = destBase + ".toc";

                try
                {
                    LinkOrCopy(srcBin, destBin);
                    installed.Add(destBin);
                    LinkOrCopy(srcToc, destToc);
                    installed.Add(destToc);
                    linkedAny = true;
                    partNum++;
                }
                catch { }
            }
        }

        if (linkedAny)
            UpsertNvidiaGameTagMap(destGlCacheRoot, tag, game.Name);
    }

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
                    if (game.Source == GameSource.Steam)
                    {
                        foreach (var file in Directory.GetFiles(glCache, "*", SearchOption.AllDirectories)
                                     .Where(f => IsNvidiaReadOnlyCacheFileName(Path.GetFileName(f)) && new FileInfo(f).Length > 0))
                        {
                            string dest = Path.Combine(fpGlCache, Path.GetRelativePath(glCache, file));
                            try { LinkOrCopy(file, dest); installed.Add(dest); } catch { }
                        }
                    }
                    else
                    {
                        LinkNonSteamNvidiaCacheParts(glCache, fpGlCache, game, installed);
                    }
                }
            }

            string? resolvedGameExe = Shaderless.Core.Games.GameEngineDetector.ResolveRealGameExecutable(game.InstallPath, game.ExecutablePath) ?? game.ExecutablePath;
            var runtimeExeNames = Shaderless.Core.Games.GameEngineDetector.GetObservedRuntimeExecutableFileNames(resolvedGameExe);
            var allowedStemsIgnoreCase = new HashSet<string>(
                runtimeExeNames.Select(Path.GetFileNameWithoutExtension).Where(s => !string.IsNullOrWhiteSpace(s))!,
                StringComparer.OrdinalIgnoreCase);

            if (game.Source == GameSource.Steam)
            {
                if (Directory.Exists(vkd3dCache) && allowedStemsIgnoreCase.Count > 0)
                {
                    var dirStems = CollectAndPromoteGameVkd3dWriteFiles(vkd3dCache, allowedStemsIgnoreCase);
                    foreach (var cache in Directory.GetFiles(vkd3dCache, "*.cache").Where(f => new FileInfo(f).Length > 0).ToArray())
                    {
                        string cacheFileName = Path.GetFileName(cache);
                        foreach (var targetName in GetAllTargetVkd3dCacheFileNames(cacheFileName, runtimeExeNames, allowedStemsIgnoreCase, dirStems))
                        {
                            if (string.Equals(targetName, cacheFileName, StringComparison.Ordinal))
                                continue;
                            string siblingDest = Path.Combine(vkd3dCache, targetName);
                            try { MergeOrReplaceVkd3dCache(cache, siblingDest); } catch { }
                        }
                    }
                }

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
                    var dirStems = CollectAndPromoteGameVkd3dWriteFiles(gameVkd3d, allowedStemsIgnoreCase);
                    foreach (var cache in Directory.GetFiles(vkd3dCache, "*.cache").Where(f => new FileInfo(f).Length > 0))
                    {
                        string cacheFileName = Path.GetFileName(cache);
                        foreach (var targetName in GetAllTargetVkd3dCacheFileNames(cacheFileName, runtimeExeNames, allowedStemsIgnoreCase, dirStems))
                        {
                            string targetDest = Path.Combine(gameVkd3d, targetName);
                            try { MergeOrReplaceVkd3dCache(cache, targetDest); installed.Add(targetDest); } catch { }
                        }
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
            //    Install as per-game shaderless_g<tag>_part1..N so shared Epic/GOG/EA prefixes never overwrite another game's GLCache.
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
                    LinkNonSteamNvidiaCacheParts(glCache, gameGl, game, installed);
                }
            }

            // Record every installed destination so Clear Cache can remove exact files cleanly
            PruneRemovedNvidiaLinks();
            File.WriteAllLines(manifestPath, installed.Distinct().ToList());
        }
        catch { }
    }

    internal static IReadOnlyList<string> CollectAndPromoteGameVkd3dWriteFiles(
        string vkd3dDir,
        IReadOnlySet<string> allowedStemsIgnoreCase)
    {
        var observedStems = new List<string>();
        if (string.IsNullOrEmpty(vkd3dDir) || !Directory.Exists(vkd3dDir) || allowedStemsIgnoreCase.Count == 0)
            return observedStems;

        var seenExact = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var file in Directory.GetFiles(vkd3dDir, "*.cache*"))
            {
                string fn = Path.GetFileName(file);
                if (!TryParseVkd3dCacheFileName(fn, out _, out string stem, out _, out bool isWrite))
                    continue;
                if (!allowedStemsIgnoreCase.Contains(stem))
                    continue;

                if (seenExact.Add(stem))
                    observedStems.Add(stem);

                if (isWrite)
                {
                    string finalPath = file[..^".write".Length];
                    try
                    {
                        if (new FileInfo(file).Length > 48)
                            MergeOrReplaceVkd3dCache(file, finalPath);
                        File.Delete(file);
                    }
                    catch { }
                }
            }
        }
        catch { }

        return observedStems;
    }

    internal static IReadOnlyList<string> GetAllTargetVkd3dCacheFileNames(
        string cacheFileName,
        IReadOnlyList<string> runtimeExeNames,
        IReadOnlySet<string> allowedStemsIgnoreCase,
        IReadOnlyList<string>? extraObservedStems = null)
    {
        if (!TryParseVkd3dCacheFileName(cacheFileName, out string prefix, out string sourceStem, out string suffix, out bool isWrite) ||
            isWrite)
        {
            return new[] { cacheFileName };
        }

        var results = new List<string> { cacheFileName };
        if (!allowedStemsIgnoreCase.Contains(sourceStem))
            return results;

        var seenFileNames = new HashSet<string>(StringComparer.Ordinal) { cacheFileName };
        void AddForStem(string? candidateStem)
        {
            if (string.IsNullOrWhiteSpace(candidateStem)) return;
            string formatted = prefix + candidateStem + suffix;
            if (seenFileNames.Add(formatted))
                results.Add(formatted);
        }

        foreach (var exeName in runtimeExeNames)
            AddForStem(Path.GetFileNameWithoutExtension(exeName));

        if (extraObservedStems != null)
        {
            foreach (var obs in extraObservedStems)
                AddForStem(obs);
        }

        return results;
    }

    internal static bool TryParseVkd3dCacheFileName(
        string? fileName,
        out string prefix,
        out string stem,
        out string suffix,
        out bool isWrite)
    {
        prefix = string.Empty;
        stem = string.Empty;
        suffix = string.Empty;
        isWrite = false;

        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        ReadOnlySpan<char> span = fileName.AsSpan();
        if (span.EndsWith(".write", StringComparison.OrdinalIgnoreCase))
        {
            isWrite = true;
            span = span[..^".write".Length];
        }

        if (span.Equals("vkd3d-proton.cache", StringComparison.OrdinalIgnoreCase))
            return false;

        if (span.EndsWith(".exe.cache", StringComparison.OrdinalIgnoreCase))
        {
            suffix = span[^".exe.cache".Length..].ToString();
            span = span[..^".exe.cache".Length];
        }
        else if (span.EndsWith(".cache", StringComparison.OrdinalIgnoreCase))
        {
            suffix = span[^".cache".Length..].ToString();
            span = span[..^".cache".Length];
        }
        else
        {
            return false;
        }

        if (span.StartsWith("vkd3d-proton.", StringComparison.OrdinalIgnoreCase))
        {
            prefix = span[.."vkd3d-proton.".Length].ToString();
            span = span["vkd3d-proton.".Length..];
        }

        if (span.IsEmpty)
            return false;

        stem = span.ToString();
        return true;
    }

    private static void DeleteVkd3dWriteCompanionsCaseInsensitive(string cachePath)
    {
        try
        {
            string exactWrite = cachePath + ".write";
            if (File.Exists(exactWrite))
                File.Delete(exactWrite);

            string? dir = Path.GetDirectoryName(cachePath);
            string targetWriteName = Path.GetFileName(cachePath) + ".write";
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.write"))
                {
                    if (string.Equals(Path.GetFileName(f), targetWriteName, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
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

    /// <summary>Captures a single snapshot of all running .exe basenames in /proc/*/cmdline (excluding Shaderless's own warmer)
    /// so batch callers (library scan, GameExitMonitor) scan /proc once instead of once per game.</summary>
    public static HashSet<string> CaptureRunningExeNames(string procRoot = "/proc")
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(procRoot)) return running;

            string workRoot = Path.Combine(Shaderless.Core.AppPaths.DataDir, "work");

            foreach (var dir in Directory.EnumerateDirectories(procRoot))
            {
                string pidStr = Path.GetFileName(dir);
                if (pidStr.Length == 0 || !pidStr.All(char.IsDigit)) continue;

                string cmdlineFile = Path.Combine(dir, "cmdline");
                string raw;
                try
                {
                    if (!File.Exists(cmdlineFile)) continue;
                    raw = File.ReadAllText(cmdlineFile);
                }
                catch { continue; }

                if (string.IsNullOrEmpty(raw)) continue;

                // Ignore our own staged warmer inside ~/.local/share/shaderless/work/<id>/
                if (raw.Contains(workRoot, StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("/shaderless/work/", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("\\shaderless\\work\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var arg in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fileName = arg.Replace('\\', '/');
                    int slash = fileName.LastIndexOf('/');
                    if (slash >= 0) fileName = fileName[(slash + 1)..];
                    if (fileName.Length > 4)
                        running.Add(fileName);
                }
            }
        }
        catch { }
        return running;
    }

    /// <summary>Checks whether the game's executable is currently running in /proc/*/cmdline (excluding Shaderless's own warmer).</summary>
    public static bool IsGameRunning(GameInfo game, string procRoot = "/proc") =>
        IsGameRunning(game, runningExeNames: null, procRoot);

    /// <summary>Checks whether the game's executable matches a pre-captured snapshot of running .exe basenames
    /// (or captures /proc/*/cmdline on demand when <paramref name="runningExeNames"/> is null).</summary>
    public static bool IsGameRunning(GameInfo game, IReadOnlySet<string>? runningExeNames, string procRoot = "/proc")
    {
        if (runningExeNames is { Count: 0 })
            return false;

        try
        {
            var exeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(game.ExecutablePath) && game.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exeNames.Add(Path.GetFileName(game.ExecutablePath));

            string? resolvedDir = Shaderless.Core.Games.RecorderManager.ExeDir(game);
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

            if (exeNames.Count == 0) return false;

            var activeExes = runningExeNames ?? CaptureRunningExeNames(procRoot);
            if (activeExes.Count == 0) return false;

            foreach (var exe in exeNames)
            {
                if (activeExes.Contains(exe))
                    return true;
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
            string tag = SanitizeNvidiaGameTag(gameId);
            string compatData = prefix.EndsWith("/pfx", StringComparison.Ordinal) || prefix.EndsWith("\\pfx", StringComparison.Ordinal)
                ? Path.GetDirectoryName(prefix)!
                : prefix;

            var glTargets = new HashSet<string>(StringComparer.Ordinal)
            {
                Path.Combine(prefix, "GLCache"),
                Path.Combine(compatData, "GLCache"),
                Path.Combine(prefix, "shadercache", "nvidiav1", "GLCache"),
                Path.Combine(compatData, "shadercache", "nvidiav1", "GLCache")
            };
            foreach (var gt in glTargets)
            {
                RemoveNvidiaGameTagFromMap(gt, tag);
                if (!Directory.Exists(gt)) continue;
                foreach (var bucket in Directory.GetFiles(gt, "steamapp_shader_cache*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(bucket);
                    if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".toc", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(bucket); } catch { }
                    }
                }
            }

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
                        if (path.EndsWith(".cache", StringComparison.OrdinalIgnoreCase))
                        {
                            DeleteVkd3dWriteCompanionsCaseInsensitive(path);
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
            foreach (var gt in glTargets)
            {
                if (!Directory.Exists(gt)) continue;
                foreach (var file in Directory.GetFiles(gt, $"shaderless_g{tag}_part*", SearchOption.AllDirectories))
                    try { File.Delete(file); } catch { }
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

    /// <summary>
    /// Seamlessly migrates recordings, work artifacts, compiled caches, and saved cache state when a game previously discovered via
    /// a fallback launcher container child ID (e.g. <c>5_ubisoft_8006</c>) gains its own explicit launcher card ID (e.g. <c>7</c>).
    /// </summary>
    public static void MigrateSupersededNonSteamGameId(string oldGameId, GameInfo newGame, string? dataDir = null)
    {
        if (string.IsNullOrWhiteSpace(oldGameId) || string.IsNullOrWhiteSpace(newGame.Id) ||
            string.Equals(oldGameId, newGame.Id, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            string rootDataDir = dataDir ?? Shaderless.Core.Games.RecorderManager.DataDir;
            static string SanitizeDirId(string id) =>
                string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));

            string oldSanitized = SanitizeDirId(oldGameId);
            string newSanitized = SanitizeDirId(newGame.Id);

            string oldGamesDir = Path.Combine(rootDataDir, "games", oldSanitized);
            string newGamesDir = Path.Combine(rootDataDir, "games", newSanitized);
            string oldWorkDir = Path.Combine(rootDataDir, "work", oldSanitized);
            string newWorkDir = Path.Combine(rootDataDir, "work", newSanitized);
            string? oldScDir = !string.IsNullOrEmpty(newGame.PrefixPath)
                ? GetNonSteamShaderCachePath(oldGameId, newGame.PrefixPath)
                : null;
            string stateFilePath = Path.Combine(rootDataDir, "games-cache-state.json");

            bool hasOldGames = Directory.Exists(oldGamesDir);
            bool hasOldWork = Directory.Exists(oldWorkDir);
            bool hasOldSc = oldScDir != null && Directory.Exists(oldScDir);
            bool hasOldState = File.Exists(stateFilePath) &&
                               File.ReadAllText(stateFilePath).Contains($"\"{oldGameId}\"", StringComparison.Ordinal);

            if (!hasOldGames && !hasOldWork && !hasOldSc && !hasOldState)
                return;

            // 1. Migrate ~/.local/share/shaderless/games/<old> -> games/<new> (preserving recording.db, warmed keys, notes, aes.key)
            if (hasOldGames)
            {
                try
                {
                    if (!Directory.Exists(newGamesDir))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(newGamesDir)!);
                        Directory.Move(oldGamesDir, newGamesDir);
                    }
                    else
                    {
                        string oldRec = Path.Combine(oldGamesDir, "recording.db");
                        string newRec = Path.Combine(newGamesDir, "recording.db");
                        if (File.Exists(oldRec))
                        {
                            if (!File.Exists(newRec))
                                File.Move(oldRec, newRec);
                            else
                                Shaderless.Engine.App.Recordings.Merge(newRec, oldRec, null);
                        }

                        foreach (var file in Directory.GetFiles(oldGamesDir))
                        {
                            string fn = Path.GetFileName(file);
                            if (fn.Equals("recording.db.lock", StringComparison.OrdinalIgnoreCase))
                                continue;
                            string dest = Path.Combine(newGamesDir, fn);
                            if (!File.Exists(dest))
                                File.Move(file, dest);
                        }

                        Directory.Delete(oldGamesDir, recursive: true);
                    }
                }
                catch { }
            }

            // 2. Migrate ~/.local/share/shaderless/work/<old> -> work/<new>
            if (hasOldWork)
            {
                try
                {
                    if (!Directory.Exists(newWorkDir))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(newWorkDir)!);
                        Directory.Move(oldWorkDir, newWorkDir);
                    }
                    else
                    {
                        Directory.Delete(oldWorkDir, recursive: true);
                    }
                }
                catch { }
            }

            // 3. Migrate <prefix>/shadercache/<old> -> <prefix>/shadercache/<new> & re-link shaderless_g<newTag>_part*
            if (hasOldSc && !string.IsNullOrEmpty(newGame.PrefixPath) && oldScDir != null)
            {
                try
                {
                    string prefix = newGame.PrefixPath;
                    string compatData = prefix.EndsWith("/pfx", StringComparison.Ordinal) || prefix.EndsWith("\\pfx", StringComparison.Ordinal)
                        ? Path.GetDirectoryName(prefix)!
                        : prefix;
                    string oldTag = SanitizeNvidiaGameTag(oldGameId);

                    var glTargets = new HashSet<string>(StringComparer.Ordinal)
                    {
                        Path.Combine(prefix, "GLCache"),
                        Path.Combine(compatData, "GLCache"),
                        Path.Combine(prefix, "shadercache", "nvidiav1", "GLCache"),
                        Path.Combine(compatData, "shadercache", "nvidiav1", "GLCache")
                    };
                    if (!string.IsNullOrEmpty(newGame.GlCachePath))
                        glTargets.Add(Path.Combine(newGame.GlCachePath, "GLCache"));
                    if (newGame.IsFlatpak || IsFlatpakGame(newGame.Id, newGame.InstallPath, newGame.PrefixPath))
                    {
                        string? fpGl = GetFlatpakLauncherNvidiaGlCachePath(newGame.Source);
                        if (!string.IsNullOrEmpty(fpGl))
                            glTargets.Add(fpGl);
                    }

                    foreach (var gt in glTargets)
                    {
                        RemoveNvidiaGameTagFromMap(gt, oldTag);
                        if (!Directory.Exists(gt)) continue;
                        foreach (var oldShard in Directory.GetFiles(gt, $"shaderless_g{oldTag}_part*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(oldShard); } catch { }
                        }
                    }

                    string newScDir = GetNonSteamShaderCachePath(newGame.Id, prefix);
                    if (!Directory.Exists(newScDir))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(newScDir)!);
                        Directory.Move(oldScDir, newScDir);
                    }
                    else
                    {
                        CopyDirectory(oldScDir, newScDir);
                        Directory.Delete(oldScDir, recursive: true);
                    }

                    string oldManifest = Path.Combine(newScDir, InstalledManifest);
                    if (File.Exists(oldManifest))
                    {
                        try { File.Delete(oldManifest); } catch { }
                    }

                    InstallCachesWhereGameReads(
                        newGame,
                        Path.Combine(newScDir, "VKD3D_shader_cache"),
                        Path.Combine(newScDir, "nvidiav1", "GLCache"));
                }
                catch { }
            }

            // 4. Migrate cache_states.json entry (<oldGameId> -> <newGame.Id>)
            if (hasOldState && File.Exists(stateFilePath))
            {
                try
                {
                    var states = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(stateFilePath));
                    if (states != null && states.Remove(oldGameId, out var oldRec))
                    {
                        if (!states.ContainsKey(newGame.Id))
                            states[newGame.Id] = oldRec;
                        File.WriteAllText(
                            stateFilePath,
                            JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch { }
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

        // 5. Purge any NVDC read-only part from an older driver version (mismatched 32B header) or mismatched Zstd dictionary,
        //    and stage any oversized (> maxPartBytes) read-only files to be re-split cleanly.
        var sourcesToPack = new List<(string BinPath, string TocPath)>();
        var existingKeys = new HashSet<(ulong K0, ulong K1)>();
        uint targetDictId = 0;

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
            else
            {
                uint mDict = TryGetNvidiaCacheDictId(mergedBin, mergedToc);
                if (mDict != 0)
                    targetDictId = mDict;

                if (mLen > maxPartBytes && mTocLen >= 32 + 48)
                {
                    StageForRepack(mergedBin, mergedToc);
                }
                else
                {
                    AddTocKeysToSet(File.ReadAllBytes(mergedToc), existingKeys);
                }
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
            else
            {
                uint pDict = TryGetNvidiaCacheDictId(p.Bin, p.Toc);
                if (pDict != 0)
                {
                    if (targetDictId == 0)
                    {
                        targetDictId = pDict;
                    }
                    else if (pDict != targetDictId)
                    {
                        try { if (File.Exists(p.Bin)) File.Delete(p.Bin); } catch { }
                        try { if (File.Exists(p.Toc)) File.Delete(p.Toc); } catch { }
                        continue;
                    }
                }

                if (pLen > maxPartBytes && pTocLen >= 32 + 48)
                {
                    StageForRepack(p.Bin, p.Toc);
                }
                else
                {
                    AddTocKeysToSet(File.ReadAllBytes(p.Toc), existingKeys);
                }
            }
        }

        // Check steam_shader_cache.bin/.toc:
        // - If its Zstd dictionary ID conflicts with Shaderless's active targetDictId, leave it untouched and do not mix its keys!
        // - If oversized (> maxPartBytes) OR >= 50% duplicate of existingKeys (like legacy 2 GB duplicate), stage for repack & delete duplicate!
        // - Otherwise (distinct compatible Steam cache <= maxPartBytes), preserve it and register its keys in existingKeys.
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
                    uint rDict = TryGetNvidiaCacheDictId(steamRoBin, steamRoToc);
                    if (targetDictId == 0 && rDict != 0)
                        targetDictId = rDict;

                    if (targetDictId == 0 || rDict == 0 || rDict == targetDictId)
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
        }

        foreach (var wb in nvdcWritable)
        {
            string wtoc = Path.ChangeExtension(wb.FullName, ".toc");
            uint wbDict = TryGetNvidiaCacheDictId(wb.FullName, wtoc);
            if (targetDictId == 0 && wbDict != 0)
                targetDictId = wbDict;

            if (targetDictId != 0 && wbDict != 0 && wbDict != targetDictId)
            {
                try { if (File.Exists(wb.FullName)) File.Delete(wb.FullName); } catch { }
                try { if (File.Exists(wtoc)) File.Delete(wtoc); } catch { }
                continue;
            }

            sourcesToPack.Add((wb.FullName, wtoc));
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
        var s = settings ?? Shaderless.Core.SettingsManager.Load();
        int maxGb = s.EffectiveMaxCacheSizeGb;
        long maxBytes = s.MaxCacheSizeBytes;
        string mesaMax = s.MesaMaxCacheSize;
        long partLimit = maxNvidiaPartBytes >= 256 ? maxNvidiaPartBytes : DefaultMaxNvidiaPartBytes;

        return
            "import os, sys, struct, glob, shutil\n\n" +
            $"# shaderless-glcache-merge-v7 max_gb={maxGb}\n" +
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
            "def _parse_game_part(name):\n" +
            "    if name.endswith((\".bin\", \".toc\")): name = name[:-4]\n" +
            "    if name.startswith(\"shaderless_part\") and name[15:].isdigit():\n" +
            "        v = int(name[15:])\n" +
            "        return (\"\", v) if v > 0 else (None, -1)\n" +
            "    if name.startswith(\"shaderless_g\") and \"_part\" in name[12:]:\n" +
            "        tag, idxs = name[12:].split(\"_part\", 1)\n" +
            "        if tag and tag.isalnum() and tag.lower() == tag and idxs.isdigit():\n" +
            "            v = int(idxs)\n" +
            "            if v > 0: return (tag, v)\n" +
            "    return (None, -1)\n" +
            "def _part_idx(name):\n" +
            "    t, v = _parse_game_part(name)\n" +
            "    return v if t == \"\" else -1\n" +
            "def _get_nv_dict_id(bp, tp):\n" +
            "    try:\n" +
            "        if not os.path.exists(bp) or not os.path.exists(tp): return 0\n" +
            "        blen = os.path.getsize(bp)\n" +
            "        tlen = os.path.getsize(tp)\n" +
            "        if blen <= 64 or tlen < 56 or (tlen - 32) % 24 != 0: return 0\n" +
            "        with open(tp, \"rb\") as f: tb = f.read()\n" +
            "        if not _is_nvdc(tb): return 0\n" +
            "        with open(bp, \"rb\") as bf:\n" +
            "            for i in range(32, len(tb), 24):\n" +
            "                if tb[i+8:i+16] == b\"_NVDICT_\":\n" +
            "                    off, plen = struct.unpack_from(\"<II\", tb, i+16)\n" +
            "                    if off >= 32 and plen >= 8 and off + 40 <= blen:\n" +
            "                        bf.seek(off)\n" +
            "                        pr = bf.read(40)\n" +
            "                        if len(pr) == 40 and pr[:4] == b\"\\x9d\\xa1\\x46\\x98\" and struct.unpack_from(\"<I\", pr, 32)[0] == 0xEC30A437:\n" +
            "                            did = struct.unpack_from(\"<I\", pr, 36)[0]\n" +
            "                            if did: return did\n" +
            "            checked = 0\n" +
            "            for i in range(32, len(tb), 24):\n" +
            "                if checked >= 16: break\n" +
            "                checked += 1\n" +
            "                off, plen = struct.unpack_from(\"<II\", tb, i+16)\n" +
            "                if off < 32 or plen < 14 or off + 46 > blen: continue\n" +
            "                bf.seek(off)\n" +
            "                pr = bf.read(46)\n" +
            "                if len(pr) < 46 or pr[:4] != b\"\\x9d\\xa1\\x46\\x98\": continue\n" +
            "                if struct.unpack_from(\"<I\", pr, 36)[0] != 0xFD2FB528: continue\n" +
            "                fhd = pr[40]\n" +
            "                df = fhd & 3\n" +
            "                doff = 41 + (0 if ((fhd >> 5) & 1) else 1)\n" +
            "                if df == 1: did = pr[doff]\n" +
            "                elif df == 2: did = struct.unpack_from(\"<H\", pr, doff)[0]\n" +
            "                elif df == 3: did = struct.unpack_from(\"<I\", pr, doff)[0]\n" +
            "                else: did = 0\n" +
            "                if did: return did\n" +
            "    except Exception:\n" +
            "        pass\n" +
            "    return 0\n" +
            "def _resolve_game_tag(gl_roots, game_name):\n" +
            "    gn = (game_name or \"\").strip().lower()\n" +
            "    if not gn: return \"\"\n" +
            "    gn_alnum = \"\".join(c for c in gn if c.isascii() and c.isalnum())\n" +
            "    seen_tags = set()\n" +
            "    fallback_tag = \"\"\n" +
            "    for r in gl_roots:\n" +
            "        if not r: continue\n" +
            "        mp = os.path.join(r, \"shaderless_games.tsv\")\n" +
            "        if os.path.isfile(mp):\n" +
            "            try:\n" +
            "                with open(mp, \"r\", encoding=\"utf-8\", errors=\"ignore\") as f:\n" +
            "                    for ln in f:\n" +
            "                        pts = ln.rstrip(\"\\r\\n\").split(\"\\t\", 1)\n" +
            "                        if len(pts) == 2:\n" +
            "                            t = pts[0].strip()\n" +
            "                            nm = pts[1].strip().lower()\n" +
            "                            if nm == gn:\n" +
            "                                return t\n" +
            "                            if not fallback_tag and gn_alnum and \"\".join(c for c in nm if c.isascii() and c.isalnum()) == gn_alnum:\n" +
            "                                fallback_tag = t\n" +
            "                            if t: seen_tags.add(t)\n" +
            "            except Exception:\n" +
            "                pass\n" +
            "    if fallback_tag:\n" +
            "        return fallback_tag\n" +
            "    if len(seen_tags) == 1 and gn in (\"ubisoft connect\", \"ubisoft game launcher\", \"uplay\", \"epic games store\", \"ea app\", \"origin\", \"battle.net\"):\n" +
            "        return next(iter(seen_tags))\n" +
            "    return \"\"\n" +
            "def _newest_leaf_dir(gl_root):\n" +
            "    if not gl_root or not os.path.isdir(gl_root): return \"\"\n" +
            "    best_dir = \"\"\n" +
            "    best_mt = -1.0\n" +
            "    try:\n" +
            "        for bp in glob.glob(os.path.join(gl_root, \"**\", \"*.bin\"), recursive=True):\n" +
            "            try:\n" +
            "                if os.path.getsize(bp) <= 64 or not os.path.exists(bp[:-4] + \".toc\"): continue\n" +
            "                mt = os.path.getmtime(bp)\n" +
            "                if mt > best_mt:\n" +
            "                    best_mt = mt\n" +
            "                    best_dir = os.path.dirname(bp)\n" +
            "            except Exception:\n" +
            "                pass\n" +
            "    except Exception:\n" +
            "        pass\n" +
            "    return best_dir\n" +
            "def _merge_nv_runtime_buckets(gl_root, game_tag=\"\"):\n" +
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
            "            game_tocs = sorted(\n" +
            "                [p for p in glob.glob(os.path.join(bdir, f\"shaderless_g{game_tag}_part*.toc\")) if _parse_game_part(os.path.basename(p))[0] == game_tag],\n" +
            "                key=lambda p: _parse_game_part(os.path.basename(p))[1]) if game_tag else []\n" +
            "            use_game_slots = bool(game_tocs)\n" +
            "            target_dict = 0\n" +
            "            keys = set()\n" +
            "            if use_game_slots:\n" +
            "                slot_tocs = list(game_tocs)\n" +
            "            else:\n" +
            "                slot_tocs = [mtoc] + sorted(\n" +
            "                    [p for p in glob.glob(os.path.join(bdir, \"shaderless_part*.toc\")) if _part_idx(os.path.basename(p)) > 0],\n" +
            "                    key=lambda p: _part_idx(os.path.basename(p)))\n" +
            "            for rt in slot_tocs:\n" +
            "                rb = rt[:-4] + \".bin\"\n" +
            "                if os.path.exists(rt) and os.path.exists(rb) and os.path.getsize(rb) > 64:\n" +
            "                    with open(rt, \"rb\") as f: dt = f.read()\n" +
            "                    if _is_nvdc(dt) and dt[:32] == hdr:\n" +
            "                        rdid = _get_nv_dict_id(rb, rt)\n" +
            "                        if rdid:\n" +
            "                            if target_dict == 0:\n" +
            "                                target_dict = rdid\n" +
            "                            elif rdid != target_dict:\n" +
            "                                for p in (rb, rt):\n" +
            "                                    try:\n" +
            "                                        if os.path.exists(p): os.remove(p)\n" +
            "                                    except Exception: pass\n" +
            "                                continue\n" +
            "                        keys.update(dt[i:i+16] for i in range(32, len(dt), 24))\n" +
            "            if not use_game_slots and os.path.exists(rtoc) and os.path.exists(rbin) and os.path.getsize(rbin) > 64:\n" +
            "                with open(rtoc, \"rb\") as f: rdt = f.read()\n" +
            "                if _is_nvdc(rdt) and rdt[:32] == hdr:\n" +
            "                    rdid = _get_nv_dict_id(rbin, rtoc)\n" +
            "                    if target_dict == 0 and rdid: target_dict = rdid\n" +
            "                    if target_dict == 0 or rdid == 0 or rdid == target_dict:\n" +
            "                        keys.update(rdt[i:i+16] for i in range(32, len(rdt), 24))\n" +
            "            compat_wb = []\n" +
            "            for bp, tp, blen in valid_wb:\n" +
            "                wdid = _get_nv_dict_id(bp, tp)\n" +
            "                if target_dict == 0 and wdid: target_dict = wdid\n" +
            "                if target_dict and wdid and wdid != target_dict:\n" +
            "                    for p in (bp, tp):\n" +
            "                        try:\n" +
            "                            if os.path.exists(p): os.remove(p)\n" +
            "                        except Exception: pass\n" +
            "                    continue\n" +
            "                compat_wb.append((bp, tp, blen))\n" +
            "            if not compat_wb: continue\n" +
            "            def _slot_name(s):\n" +
            "                if use_game_slots: return f\"shaderless_g{game_tag}_part{s + 1}\"\n" +
            "                return \"steamapp_merged_shader_cache\" if s == 0 else f\"shaderless_part{s}\"\n" +
            "            s0_bin = os.path.join(bdir, _slot_name(0) + \".bin\")\n" +
            "            s0_toc = os.path.join(bdir, _slot_name(0) + \".toc\")\n" +
            "            if not keys and not os.path.exists(s0_bin) and compat_wb[0][2] <= _MAX_PART_BYTES:\n" +
            "                bp0, tp0, _ = compat_wb.pop(0)\n" +
            "                shutil.move(bp0, s0_bin)\n" +
            "                shutil.move(tp0, s0_toc)\n" +
            "                with open(s0_toc, \"rb\") as f: dt = f.read()\n" +
            "                if _is_nvdc(dt): keys.update(dt[i:i+16] for i in range(32, len(dt), 24))\n" +
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
            "                for bp, tp, blen in compat_wb:\n" +
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
            "def _build_ro_names(active_gl_root, gl_roots, existing_ro=\"\", game_tag=\"\"):\n" +
            "    extra = []\n" +
            "    for tok in (existing_ro or \"\").split(\";\"):\n" +
            "        tok = tok.strip()\n" +
            "        if not tok: continue\n" +
            "        if tok in (\"steamapp_merged_shader_cache\", \"steam_shader_cache\"): continue\n" +
            "        if _parse_game_part(tok)[0] is not None: continue\n" +
            "        extra.append(tok)\n" +
            "    leaf_dir = _newest_leaf_dir(active_gl_root)\n" +
            "    if not leaf_dir:\n" +
            "        for r in gl_roots:\n" +
            "            leaf_dir = _newest_leaf_dir(r)\n" +
            "            if leaf_dir: break\n" +
            "    game_parts = {}\n" +
            "    game_bytes = {}\n" +
            "    plain_parts = set()\n" +
            "    plain_bytes = 0\n" +
            "    if leaf_dir and os.path.isdir(leaf_dir):\n" +
            "        try:\n" +
            "            mp = os.path.join(leaf_dir, \"steamapp_merged_shader_cache.bin\")\n" +
            "            if os.path.exists(mp) and os.path.exists(mp[:-4] + \".toc\"):\n" +
            "                mlen = os.path.getsize(mp)\n" +
            "                if mlen > 64: plain_bytes += mlen\n" +
            "            for bp in glob.glob(os.path.join(leaf_dir, \"shaderless_*.bin\")):\n" +
            "                gtag, pi = _parse_game_part(os.path.basename(bp))\n" +
            "                if gtag is None or pi <= 0: continue\n" +
            "                blen = os.path.getsize(bp)\n" +
            "                if blen <= 64 or not os.path.exists(bp[:-4] + \".toc\"): continue\n" +
            "                if gtag == \"\":\n" +
            "                    plain_parts.add(pi)\n" +
            "                    plain_bytes += blen\n" +
            "                else:\n" +
            "                    game_parts.setdefault(gtag, set()).add(pi)\n" +
            "                    game_bytes[gtag] = game_bytes.get(gtag, 0) + blen\n" +
            "        except Exception:\n" +
            "            pass\n" +
            "    cands = []\n" +
            "    if game_tag and game_tag in game_parts:\n" +
            "        for pi in sorted(game_parts[game_tag]):\n" +
            "            cands.append(f\"shaderless_g{game_tag}_part{pi}\")\n" +
            "    rem_groups = [(\"\", plain_bytes)] + [(k, game_bytes.get(k, 0)) for k in game_parts if k != game_tag]\n" +
            "    for gtag, _ in sorted(rem_groups, key=lambda x: (-x[1], x[0])):\n" +
            "        if gtag == \"\":\n" +
            "            cands.append(\"steamapp_merged_shader_cache\")\n" +
            "            for pi in sorted(plain_parts):\n" +
            "                cands.append(f\"shaderless_part{pi}\")\n" +
            "        else:\n" +
            "            for pi in sorted(game_parts[gtag]):\n" +
            "                cands.append(f\"shaderless_g{gtag}_part{pi}\")\n" +
            "    cands.extend(extra)\n" +
            "    cands.append(\"steam_shader_cache\")\n" +
            "    names = []\n" +
            "    seen = set()\n" +
            "    active_dict = 0\n" +
            "    for c in cands:\n" +
            "        if not c or c in seen: continue\n" +
            "        seen.add(c)\n" +
            "        if leaf_dir:\n" +
            "            bp = os.path.join(leaf_dir, c + \".bin\")\n" +
            "            tp = os.path.join(leaf_dir, c + \".toc\")\n" +
            "            if os.path.exists(bp) and os.path.exists(tp) and os.path.getsize(bp) > 64:\n" +
            "                did = _get_nv_dict_id(bp, tp)\n" +
            "                if did:\n" +
            "                    if active_dict == 0:\n" +
            "                        active_dict = did\n" +
            "                    elif did != active_dict:\n" +
            "                        continue\n" +
            "        names.append(c)\n" +
            "    return \";\".join(names)\n" +
            "_env_gl_base = os.environ.get(\"__GL_SHADER_DISK_CACHE_PATH\", \"\")\n" +
            "_env_gl_root = os.path.join(_env_gl_base, \"GLCache\") if _env_gl_base else \"\"\n" +
            "_gl_root = \"\"\n" +
            "_gl_roots = []\n" +
            "if _env_gl_root:\n" +
            "    _gl_roots.append(_env_gl_root)\n" +
            "if _shader_path:\n" +
            "    for _sub in (\"nvidiav1\", \"VKD3D_shader_cache\", \"DXVK_state_cache\", \"AMDv1\", \"mesa\"):\n" +
            "        try:\n" +
            "            os.makedirs(os.path.join(_shader_path, _sub), exist_ok=True)\n" +
            "        except Exception:\n" +
            "            pass\n" +
            "    _gl_root = os.path.join(_shader_path, \"nvidiav1\", \"GLCache\")\n" +
            "    if _gl_root not in _gl_roots:\n" +
            "        _gl_roots.append(_gl_root)\n" +
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
            "_game_tag = _resolve_game_tag(_gl_roots, os.environ.get(\"GAME_NAME\", \"\"))\n" +
            "for _r in _gl_roots:\n" +
            "    _merge_nv_runtime_buckets(_r, _game_tag)\n" +
            "_active_gl_root = _env_gl_root if _env_gl_base else _gl_root\n" +
            "if _gl_roots:\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME\"] = _build_ro_names(_active_gl_root, _gl_roots, os.environ.get(\"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME\", \"\"), _game_tag)\n" +
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
        var effectiveSettings = settings ?? Shaderless.Core.SettingsManager.Load();
        string versionTag = $"shaderless-glcache-merge-v7 max_gb={effectiveSettings.EffectiveMaxCacheSizeGb}";
        string settingsContent = BuildProtonUserSettingsScript(effectiveSettings);

        var targetDirs = new List<string>();
        if (!string.IsNullOrEmpty(compatData) && Directory.Exists(compatData))
        {
            targetDirs.Add(compatData);
        }

        // Only enumerate global Proton installations when not running under isolated test overrides without a real compatData
        if (string.IsNullOrEmpty(Shaderless.Core.SettingsManager.TestSettingsPathOverride) &&
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
                Path.Combine(Shaderless.Core.AppPaths.GetXdgDataHome(HomeDir), "umu", "compatibilitytools"),
                Path.Combine(HomeDir, ".local", "share", "umu", "compatibilitytools"),
                Path.Combine(Shaderless.Core.AppPaths.GetXdgConfigHome(HomeDir), "heroic", "tools", "proton"),
                Path.Combine(HomeDir, ".config", "heroic", "tools", "proton"),
                Path.Combine(HomeDir, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "tools", "proton"),
                Path.Combine(Shaderless.Core.AppPaths.GetXdgConfigHome(HomeDir), "heroic", "tools", "wine"),
                Path.Combine(HomeDir, ".config", "heroic", "tools", "wine"),
                Path.Combine(HomeDir, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "tools", "wine"),
                Path.Combine(Shaderless.Core.AppPaths.GetXdgDataHome(HomeDir), "lutris", "runners", "proton"),
                Path.Combine(HomeDir, ".local", "share", "lutris", "runners", "proton"),
                Path.Combine(HomeDir, ".var", "app", "net.lutris.Lutris", "data", "lutris", "runners", "proton"),
                Path.Combine(Shaderless.Core.AppPaths.GetXdgDataHome(HomeDir), "lutris", "runners", "wine"),
                Path.Combine(HomeDir, ".local", "share", "lutris", "runners", "wine"),
                Path.Combine(HomeDir, ".var", "app", "net.lutris.Lutris", "data", "lutris", "runners", "wine"),
                "/usr/share/steam/compatibilitytools.d"
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
