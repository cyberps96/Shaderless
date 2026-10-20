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

            // 1. Check compatdata/<AppID>/config_info which records the exact Proton installation path used for the game's prefix
            var configInfoCandidates = new List<string>
            {
                Path.Combine(steamRoot, "steamapps", "compatdata", rawId, "config_info")
            };
            if (!string.IsNullOrEmpty(game.PrefixPath))
            {
                string? compatDir = Path.GetDirectoryName(game.PrefixPath.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(compatDir))
                {
                    configInfoCandidates.Insert(0, Path.Combine(compatDir, "config_info"));
                }
            }

            foreach (var configInfo in configInfoCandidates)
            {
                if (File.Exists(configInfo))
                {
                    try
                    {
                        foreach (var line in File.ReadLines(configInfo))
                        {
                            int filesIdx = line.IndexOf("/files/", StringComparison.Ordinal);
                            if (filesIdx > 0)
                            {
                                string protonRoot = line.Substring(0, filesIdx);
                                string protonBin = Path.Combine(protonRoot, "proton");
                                if (File.Exists(protonBin))
                                    return protonBin;
                            }
                        }
                    }
                    catch { }
                }
            }

            // 2. Check Steam's config/config.vdf CompatToolMapping for this AppID (or "0" global default)
            string configVdf = Path.Combine(steamRoot, "config", "config.vdf");
            if (File.Exists(configVdf))
            {
                try
                {
                    string vdf = File.ReadAllText(configVdf);
                    string? compatBlock = ExtractVdfBlock(vdf, "CompatToolMapping");
                    if (!string.IsNullOrEmpty(compatBlock))
                    {
                        foreach (var key in new[] { rawId, "0" })
                        {
                            var match = Regex.Match(compatBlock, $@"""{Regex.Escape(key)}""\s*\{{[^}}]*""name""\s+""([^""]+)""", RegexOptions.Singleline);
                            if (match.Success)
                            {
                                string toolName = match.Groups[1].Value;
                                string? resolved = ResolveCompatToolName(toolName, steamRoot);
                                if (resolved != null)
                                    return resolved;
                            }
                        }
                    }
                }
                catch { }
            }

            // 3. Check Proton installations inside this game's owning Steam root before falling back globally
            var rootCandidates = new List<string>();
            string rootTools = Path.Combine(steamRoot, "compatibilitytools.d");
            if (Directory.Exists(rootTools))
            {
                foreach (var dir in Directory.GetDirectories(rootTools).OrderByDescending(d => d))
                    rootCandidates.Add(Path.Combine(dir, "proton"));
            }
            string rootCommon = Path.Combine(steamRoot, "steamapps", "common");
            if (Directory.Exists(rootCommon))
            {
                rootCandidates.Add(Path.Combine(rootCommon, "Proton - Experimental", "proton"));
                rootCandidates.Add(Path.Combine(rootCommon, "Proton 9.0 (Beta)", "proton"));
                rootCandidates.Add(Path.Combine(rootCommon, "Proton 9.0", "proton"));
                rootCandidates.Add(Path.Combine(rootCommon, "Proton 8.0", "proton"));
                foreach (var dir in Directory.GetDirectories(rootCommon, "Proton*").OrderByDescending(d => d))
                    rootCandidates.Add(Path.Combine(dir, "proton"));
            }
            string? ownRootProton = rootCandidates.FirstOrDefault(File.Exists);
            if (ownRootProton != null)
                return ownRootProton;
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
                if (!File.Exists(gameCfg)) continue;
                try
                {
                    string? protonBin = SCSKiller.Linux.Core.Games.HeroicLinuxSource.ReadProtonBinary(File.ReadAllText(gameCfg), rawId);
                    if (!string.IsNullOrEmpty(protonBin) && File.Exists(protonBin))
                        return protonBin;
                }
                catch { }
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

    private static string? ResolveCompatToolName(string toolName, string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;

        var candidates = new List<string>
        {
            Path.Combine(steamRoot, "compatibilitytools.d", toolName, "proton"),
            Path.Combine("/usr/share/steam/compatibilitytools.d", toolName, "proton"),
            Path.Combine(steamRoot, "steamapps", "common", toolName, "proton")
        };

        if (toolName.Equals("proton_experimental", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Proton - Experimental", "proton"));
        else if (toolName.StartsWith("proton_9", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Proton 9.0", "proton"));
        else if (toolName.StartsWith("proton_8", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Proton 8.0", "proton"));

        return candidates.FirstOrDefault(File.Exists);
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

    public static async Task<bool> RunWarmerAsync(
        GameInfo game,
        string workDir,
        Action<WarmerEvent> onEvent,
        CancellationToken cancellationToken = default,
        bool isIncrementalRun = false)
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
        if (!prefixInitialized)
        {
            compatData = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "proton_prefix");
            prefixInitialized = File.Exists(Path.Combine(compatData, "pfx", "system.reg"));
        }
        Directory.CreateDirectory(compatData);

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

        // Prepare existing driver caches so incremental runs append to the merged cache while full runs start clean
        PrepareDriverCachesForWarmer(targetCachePath, vkd3dCache, glCache, protonPath, workDir, isIncrementalRun, hasAntiCheat ? null : gameExeDir);

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
        string protonVerb = prefixInitialized ? "runinprefix" : "run";

        var startInfo = new ProcessStartInfo
        {
            FileName = "nice",
            Arguments = $"-n 10 \"{protonPath}\" {protonVerb} \"{targetExe}\" \"{workDir}\" \"{exeName}\" --threads {threads}",
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
        startInfo.EnvironmentVariables["WINEDLLOVERRIDES"] = "d3d12,d3d12core,dxgi,d3d11=n,b";
        startInfo.EnvironmentVariables["PROTON_USE_XALIA"] = "0";
        if (IsLowLatencyConfigured(game, "PROTON_VKD3D_LOWLATENCY"))
            startInfo.EnvironmentVariables["PROTON_VKD3D_LOWLATENCY"] = "1";
        if (IsLowLatencyConfigured(game, "PROTON_DXVK_LOWLATENCY"))
            startInfo.EnvironmentVariables["PROTON_DXVK_LOWLATENCY"] = "1";
        startInfo.EnvironmentVariables["VKD3D_SHADER_CACHE_PATH"] = vkd3dCache;
        startInfo.EnvironmentVariables["DXVK_STATE_CACHE_PATH"] = dxvkCache;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_PATH"] = nvidiaBase;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE"] = "1";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_APP_NAME"] = "steamapp_shader_cache";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = "steam_shader_cache;steamapp_merged_shader_cache";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_SKIP_CLEANUP"] = "1";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_SIZE"] = "10737418240";
        startInfo.EnvironmentVariables["AMD_VK_PIPELINE_CACHE_PATH"] = amdCache;
        startInfo.EnvironmentVariables["AMD_VK_PIPELINE_CACHE_FILENAME"] = "steamapp_shader_cache";
        startInfo.EnvironmentVariables["AMD_VK_USE_PIPELINE_CACHE"] = "1";
        startInfo.EnvironmentVariables["MESA_SHADER_CACHE_DIR"] = mesaCache;
        startInfo.EnvironmentVariables["MESA_SHADER_CACHE_MAX_SIZE"] = "10G";
        startInfo.EnvironmentVariables["MESA_GLSL_CACHE_DIR"] = mesaCache;
        startInfo.EnvironmentVariables["MESA_GLSL_CACHE_MAX_SIZE"] = "10G";
        startInfo.EnvironmentVariables["MESA_DISK_CACHE_SINGLE_FILE"] = "1";

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
            FinalizeDriverCaches(targetCachePath, vkd3dCache, glCache, hasAntiCheat ? null : gameExeDir);
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

            // 2. Otherwise prefer the game prefix's own system32 DLL if Proton has already installed a non-symlink native DLL there
            if (sourceDll == null && Directory.Exists(gameSys32))
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

    public static bool IsRuntimeCacheStampValid(GameInfo game)
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
            if (!File.Exists(stampFile)) return true;

            string? protonPath = FindProtonBinaryForGame(game);
            if (string.IsNullOrEmpty(protonPath)) return true;

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

            return string.Equals(oldStamp, currentStamp, StringComparison.Ordinal) ||
                   string.Equals(oldStamp, baseStamp, StringComparison.Ordinal);
        }
        catch
        {
            return true;
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
        string? gameExeDir = null)
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

            // A full compile (!isIncrementalRun) or runtime stamp change must start from a clean cache
            // so two full compiles never stack past NVIDIA GLCache's 2 GiB signed 32-bit offset limit.
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

                    string mergedBin = Path.Combine(dir, "steamapp_merged_shader_cache.bin");
                    string mergedToc = Path.Combine(dir, "steamapp_merged_shader_cache.toc");
                    string steamRoBin = Path.Combine(dir, "steam_shader_cache.bin");
                    string steamRoToc = Path.Combine(dir, "steam_shader_cache.toc");

                    bool mergedOverflowsSigned32 = File.Exists(mergedBin) && new FileInfo(mergedBin).Length > 2_100_000_000L;

                    if (resetCache || mergedOverflowsSigned32)
                    {
                        try { if (File.Exists(mergedBin)) File.Delete(mergedBin); } catch { }
                        try { if (File.Exists(mergedToc)) File.Delete(mergedToc); } catch { }
                        try { if (File.Exists(steamRoBin)) File.Delete(steamRoBin); } catch { }
                        try { if (File.Exists(steamRoToc)) File.Delete(steamRoToc); } catch { }
                        foreach (var binFile in Directory.GetFiles(dir, "steamapp_shader_cache*.bin"))
                        {
                            string tocFile = Path.ChangeExtension(binFile, ".toc");
                            try { File.Delete(binFile); } catch { }
                            try { if (File.Exists(tocFile)) File.Delete(tocFile); } catch { }
                        }
                        continue;
                    }

                    long existingMergedLen = File.Exists(mergedBin) ? new FileInfo(mergedBin).Length : 0;

                    // Before overwriting bucket 0 and bucket 1 with mergedBin for the incremental warmer run,
                    // promote/merge any runtime bucket written by the game during gameplay into steam_shader_cache.
                    foreach (var binFile in Directory.GetFiles(dir, "steamapp_shader_cache*.bin")
                                 .Select(f => new FileInfo(f))
                                 .OrderByDescending(f => f.Length))
                    {
                        string tocFile = Path.ChangeExtension(binFile.FullName, ".toc");
                        if (binFile.Length <= 64)
                        {
                            try { binFile.Delete(); } catch { }
                            try { if (File.Exists(tocFile)) File.Delete(tocFile); } catch { }
                            continue;
                        }

                        if (binFile.Length != existingMergedLen && File.Exists(tocFile))
                        {
                            try
                            {
                                MergeOrPromoteNvidiaGlCache(binFile.FullName, tocFile, steamRoBin, steamRoToc, existingMergedLen);
                            }
                            catch { }
                        }

                        try { if (File.Exists(binFile.FullName)) File.Delete(binFile.FullName); } catch { }
                        try { if (File.Exists(tocFile)) File.Delete(tocFile); } catch { }
                    }

                    string bucket0Bin = Path.Combine(dir, "steamapp_shader_cache0.bin");
                    string bucket0Toc = Path.Combine(dir, "steamapp_shader_cache0.toc");
                    string bucket1Bin = Path.Combine(dir, "steamapp_shader_cache1.bin");
                    string bucket1Toc = Path.Combine(dir, "steamapp_shader_cache1.toc");

                    // Keep steamapp_merged_shader_cache.bin/.toc in place (so read-only lookup always works) AND
                    // pre-seed BOTH bucket 0 and bucket 1 by copying so even if Wine's background explorer.exe locks
                    // bucket 0, the warmer on bucket 1 starts with the full existing cache and appends only new shaders.
                    if (File.Exists(mergedBin) && File.Exists(mergedToc) && existingMergedLen > 64)
                    {
                        try
                        {
                            File.Copy(mergedBin, bucket0Bin, overwrite: true);
                            File.Copy(mergedToc, bucket0Toc, overwrite: true);
                            File.Copy(mergedBin, bucket1Bin, overwrite: true);
                            File.Copy(mergedToc, bucket1Toc, overwrite: true);
                        }
                        catch { }
                    }
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
        }
        catch { }
    }

    public static void FinalizeDriverCaches(string targetCachePath, string vkd3dCache, string glCache, string? gameExeDir = null)
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

        // 2. Promote NVIDIA per-process writable buckets (steamapp_shader_cache0/1/2.bin/.toc)
        // into lock-free shared read-only caches (steam_shader_cache.bin/.toc and steamapp_merged_shader_cache.bin/.toc).
        // In libnvidia-glcore.so, writable buckets (%s%d) use exclusive lockf() locks, so Wine background processes
        // can lock buckets 0 and 1 and prevent the game process from reading them.
        // Read-only caches (%s) in __GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME are opened without lockf() by every process.
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

                    var writableBins = Directory.GetFiles(dir, "steamapp_shader_cache*.bin")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Length)
                        .ToList();

                    // Remove 32-byte empty stub buckets
                    foreach (var stub in writableBins.Where(f => f.Length <= 64).ToList())
                    {
                        string stubToc = Path.ChangeExtension(stub.FullName, ".toc");
                        try { stub.Delete(); } catch { }
                        try { if (File.Exists(stubToc)) File.Delete(stubToc); } catch { }
                        writableBins.Remove(stub);
                    }

                    string mergedBin = Path.Combine(dir, "steamapp_merged_shader_cache.bin");
                    string mergedToc = Path.Combine(dir, "steamapp_merged_shader_cache.toc");
                    string steamRoBin = Path.Combine(dir, "steam_shader_cache.bin");
                    string steamRoToc = Path.Combine(dir, "steam_shader_cache.toc");

                    long existingMergedLen = File.Exists(mergedBin) ? new FileInfo(mergedBin).Length : 0;

                    if (writableBins.Count > 0)
                    {
                        // Primary (largest) bucket -> steamapp_merged_shader_cache
                        var primaryBin = writableBins[0];
                        string primaryToc = Path.ChangeExtension(primaryBin.FullName, ".toc");
                        if (File.Exists(primaryToc) && primaryBin.Length >= existingMergedLen)
                        {
                            try
                            {
                                File.Move(primaryBin.FullName, mergedBin, overwrite: true);
                                File.Move(primaryToc, mergedToc, overwrite: true);
                            }
                            catch { }
                        }

                        // Secondary runtime bucket (if any, not a pre-seeded copy of mergedBin) -> merge/promote into steam_shader_cache
                        long newMergedLen = File.Exists(mergedBin) ? new FileInfo(mergedBin).Length : existingMergedLen;
                        foreach (var secondaryBin in writableBins.Skip(1).Where(b => b.Length != existingMergedLen && b.Length != primaryBin.Length))
                        {
                            string secondaryToc = Path.ChangeExtension(secondaryBin.FullName, ".toc");
                            if (File.Exists(secondaryBin.FullName) && File.Exists(secondaryToc))
                            {
                                try
                                {
                                    MergeOrPromoteNvidiaGlCache(secondaryBin.FullName, secondaryToc, steamRoBin, steamRoToc, newMergedLen);
                                }
                                catch { }
                            }
                        }

                        // Clean up remaining pre-seeded writable buckets so they don't duplicate disk space
                        foreach (var rem in Directory.GetFiles(dir, "steamapp_shader_cache*.bin"))
                        {
                            string remToc = Path.ChangeExtension(rem, ".toc");
                            try { File.Delete(rem); } catch { }
                            try { if (File.Exists(remToc)) File.Delete(remToc); } catch { }
                        }
                    }

                    // Ensure steam_shader_cache is populated (hard-linked to mergedBin if no separate runtime cache exists)
                    if ((!File.Exists(steamRoBin) || !File.Exists(steamRoToc) || new FileInfo(steamRoBin).Length <= 64)
                        && File.Exists(mergedBin) && File.Exists(mergedToc) && new FileInfo(mergedBin).Length > 64)
                    {
                        try
                        {
                            LinkOrCopy(mergedBin, steamRoBin);
                            LinkOrCopy(mergedToc, steamRoToc);
                        }
                        catch { }
                    }
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

    // NVIDIA's shader cache files that a game started through Proton reads without writing (see EnsureProtonLocalShaderCacheSetting)
    static readonly string[] NvidiaReadOnlyCacheNames =
        { "steam_shader_cache.bin", "steam_shader_cache.toc", "steamapp_merged_shader_cache.bin", "steamapp_merged_shader_cache.toc" };

    [System.Runtime.InteropServices.DllImport("libc.so.6", EntryPoint = "link", SetLastError = true)]
    private static extern int HardLink(string existing, string newLink);

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

    static void InstallCachesWhereGameReads(GameInfo game, string vkd3dCache, string glCache)
    {
        var installed = new List<string>();
        try
        {
            // If this is a Flatpak game, also link the read-only NVIDIA caches into the Flatpak launcher's XDG_CACHE_HOME/nvidia/GLCache
            bool isFlatpak = game.IsFlatpak || IsFlatpakGame(game.Id, game.InstallPath, game.PrefixPath);
            if (isFlatpak && Directory.Exists(glCache))
            {
                string? fpGlCache = GetFlatpakLauncherNvidiaGlCachePath(game.Source);
                if (!string.IsNullOrEmpty(fpGlCache))
                {
                    foreach (var file in Directory.GetFiles(glCache, "*", SearchOption.AllDirectories)
                                 .Where(f => NvidiaReadOnlyCacheNames.Contains(Path.GetFileName(f))))
                    {
                        string dest = Path.Combine(fpGlCache, Path.GetRelativePath(glCache, file));
                        try { LinkOrCopy(file, dest); installed.Add(dest); } catch { }
                    }
                }
            }

            if (game.Source == GameSource.Steam)
            {
                if (installed.Count > 0)
                {
                    File.WriteAllLines(Path.Combine(Path.GetDirectoryName(vkd3dCache)!, InstalledManifest), installed);
                }
                return;
            }

            if (string.IsNullOrEmpty(game.PrefixPath)) return;
            string prefix = game.PrefixPath;

            // vkd3d-proton: the folder the game's Proton settings give it (see EnsureProtonLocalShaderCacheSetting)
            string gameVkd3d = Path.Combine(prefix, "shadercache", "VKD3D_shader_cache");
            if (Directory.Exists(vkd3dCache))
            {
                Directory.CreateDirectory(gameVkd3d);
                foreach (var cache in Directory.GetFiles(vkd3dCache, "*.cache"))
                {
                    string dest = Path.Combine(gameVkd3d, Path.GetFileName(cache));
                    try { MergeOrReplaceVkd3dCache(cache, dest); installed.Add(dest); } catch { }
                }
            }

            // NVIDIA: <the game's __GL_SHADER_DISK_CACHE_PATH>/GLCache; the folders under it are named by the driver
            // (the same ones the game itself uses). Lutris sets that path to the prefix for a launcher-installed game and to
            // the exe's folder for a game added by its exe.
            // Heroic sets no NVIDIA path of its own, so the game gets the one in Shaderless's Proton settings file:
            // <prefix>/shadercache/nvidiav1 (measured: STEAM_COMPAT_SHADER_PATH is <prefix>/shadercache).
            var nvidiaTargets = new HashSet<string>(StringComparer.Ordinal);
            string primaryBase = !string.IsNullOrEmpty(game.GlCachePath) ? game.GlCachePath
                : game.Source == GameSource.Heroic ? Path.Combine(prefix, "shadercache", "nvidiav1") : prefix;
            nvidiaTargets.Add(Path.Combine(primaryBase, "GLCache"));
            nvidiaTargets.Add(Path.Combine(prefix, "shadercache", "nvidiav1", "GLCache"));

            if (Directory.Exists(glCache))
            {
                foreach (var gameGl in nvidiaTargets)
                {
                    foreach (var file in Directory.GetFiles(glCache, "*", SearchOption.AllDirectories)
                                 .Where(f => NvidiaReadOnlyCacheNames.Contains(Path.GetFileName(f))))
                    {
                        string dest = Path.Combine(gameGl, Path.GetRelativePath(glCache, file));
                        try { LinkOrCopy(file, dest); installed.Add(dest); } catch { }
                    }
                }
            }

            // what was put there, so Clear can take exactly that out again
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(vkd3dCache)!, InstalledManifest), installed.Distinct().ToList());
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
                foreach (var path in File.ReadAllLines(manifest).Where(l => l.Length > 0))
                    try { File.Delete(path); } catch { }
                return;
            }

            // a compile from before the list was kept: the prefix's own folders
            string ownGl = Path.Combine(own, "nvidiav1", "GLCache");
            if (Directory.Exists(ownGl))
            {
                foreach (var file in Directory.GetFiles(ownGl, "*", SearchOption.AllDirectories)
                             .Where(f => NvidiaReadOnlyCacheNames.Contains(Path.GetFileName(f))))
                    try { File.Delete(Path.Combine(prefix, "GLCache", Path.GetRelativePath(ownGl, file))); } catch { }
            }
            string ownVkd3d = Path.Combine(own, "VKD3D_shader_cache");
            if (Directory.Exists(ownVkd3d))
            {
                foreach (var cache in Directory.GetFiles(ownVkd3d, "*.cache"))
                    try { File.Delete(Path.Combine(prefix, "shadercache", "VKD3D_shader_cache", Path.GetFileName(cache))); } catch { }
            }
        }
        catch { }
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

        // If dest does not exist, is a stub, or is currently hard-linked/identical in size to steamapp_merged_shader_cache,
        // unlink dest first (so we never mutate steamapp_merged_shader_cache through a hard link) and promote source.
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
            srcTocBytes.Length >= 32 && (srcTocBytes.Length - 32) % 24 == 0 &&
            dstTocBytes.Length >= 32 && (dstTocBytes.Length - 32) % 24 == 0 &&
            srcTocBytes[0] == (byte)'N' && srcTocBytes[1] == (byte)'V' && srcTocBytes[2] == (byte)'D' && srcTocBytes[3] == (byte)'C' &&
            srcTocBytes.AsSpan(0, 32).SequenceEqual(dstTocBytes.AsSpan(0, 32));

        if (!validNvdc)
        {
            // Fallback for non-NVDC or different-driver caches: only replace if source is at least as large as dest
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
        for (int pos = 32; pos + 24 <= dstTocBytes.Length; pos += 24)
        {
            ulong k0 = BitConverter.ToUInt64(dstTocBytes, pos);
            ulong k1 = BitConverter.ToUInt64(dstTocBytes, pos + 8);
            existingKeys.Add((k0, k1));
        }

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
                if (currentDstBinLen + recordLen > 2_100_000_000L)
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

                // Verify record magic 0x9846a19d (9d a1 46 98) and payload length at offset 28
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

    private static void EnsureProtonLocalShaderCacheSetting(string compatData, string? protonPath = null)
    {
        const string settingsContent =
            "import os, struct, glob, shutil\n\n" +
            "# shaderless-glcache-merge-v2\n" +
            "_shader_path = os.environ.get(\"STEAM_COMPAT_SHADER_PATH\", \"\")\n" +
            "user_settings = {\n" +
            "    \"PROTON_LOCAL_SHADER_CACHE\": \"1\",\n" +
            "    \"__GL_SHADER_DISK_CACHE\": \"1\",\n" +
            "    \"__GL_SHADER_DISK_CACHE_SIZE\": \"10737418240\",\n" +
            "    \"__GL_SHADER_DISK_CACHE_SKIP_CLEANUP\": \"1\",\n" +
            "}\n" +
            "def _merge_nv_runtime_buckets(gl_root):\n" +
            "    try:\n" +
            "        for bpath in sorted(glob.glob(os.path.join(gl_root, \"**\", \"steamapp_shader_cache*.bin\"), recursive=True), key=lambda p: -os.path.getsize(p)):\n" +
            "            tpath = bpath[:-4] + \".toc\"\n" +
            "            bdir = os.path.dirname(bpath)\n" +
            "            mbin = os.path.join(bdir, \"steamapp_merged_shader_cache.bin\")\n" +
            "            rbin = os.path.join(bdir, \"steam_shader_cache.bin\")\n" +
            "            rtoc = os.path.join(bdir, \"steam_shader_cache.toc\")\n" +
            "            blen = os.path.getsize(bpath) if os.path.exists(bpath) else 0\n" +
            "            mlen = os.path.getsize(mbin) if os.path.exists(mbin) else 0\n" +
            "            if blen <= 64 or not os.path.exists(tpath):\n" +
            "                continue\n" +
            "            if mlen > 64 and blen == mlen:\n" +
            "                continue\n" +
            "            rlen = os.path.getsize(rbin) if (os.path.exists(rbin) and os.path.exists(rtoc)) else 0\n" +
            "            if rlen <= 64 or (mlen > 64 and rlen == mlen):\n" +
            "                for p in (rbin, rtoc):\n" +
            "                    if os.path.exists(p): os.remove(p)\n" +
            "                shutil.move(bpath, rbin)\n" +
            "                shutil.move(tpath, rtoc)\n" +
            "                continue\n" +
            "            with open(tpath, \"rb\") as f: stoc = f.read()\n" +
            "            with open(rtoc, \"rb\") as f: dtoc = f.read()\n" +
            "            if len(stoc) >= 32 and len(dtoc) >= 32 and stoc[:4] == b\"NVDC\" and stoc[:32] == dtoc[:32]:\n" +
            "                keys = {dtoc[i:i+16] for i in range(32, len(dtoc), 24)}\n" +
            "                with open(bpath, \"rb\") as sb, open(rbin, \"ab\") as db, open(rtoc, \"ab\") as dt:\n" +
            "                    dpos = db.tell()\n" +
            "                    for i in range(32, len(stoc), 24):\n" +
            "                        k = stoc[i:i+16]\n" +
            "                        if k in keys: continue\n" +
            "                        off, plen = struct.unpack_from(\"<II\", stoc, i+16)\n" +
            "                        rlen_rec = 32 + plen\n" +
            "                        if off < 32 or off + rlen_rec > blen or dpos + rlen_rec > 2100000000: continue\n" +
            "                        sb.seek(off)\n" +
            "                        rec = sb.read(rlen_rec)\n" +
            "                        if len(rec) == rlen_rec and rec[:4] == b\"\\x9d\\xa1\\x46\\x98\":\n" +
            "                            db.write(rec)\n" +
            "                            dt.write(k + struct.pack(\"<II\", dpos, plen))\n" +
            "                            dpos += rlen_rec\n" +
            "                            keys.add(k)\n" +
            "                os.remove(bpath)\n" +
            "                os.remove(tpath)\n" +
            "    except Exception:\n" +
            "        pass\n" +
            "if _shader_path:\n" +
            "    for _sub in (\"nvidiav1\", \"VKD3D_shader_cache\", \"DXVK_state_cache\", \"AMDv1\", \"mesa\"):\n" +
            "        try:\n" +
            "            os.makedirs(os.path.join(_shader_path, _sub), exist_ok=True)\n" +
            "        except Exception:\n" +
            "            pass\n" +
            "    _merge_nv_runtime_buckets(os.path.join(_shader_path, \"nvidiav1\", \"GLCache\"))\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_PATH\"] = os.path.join(_shader_path, \"nvidiav1\")\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_APP_NAME\"] = \"steamapp_shader_cache\"\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME\"] = \"steam_shader_cache;steamapp_merged_shader_cache\"\n" +
            "    user_settings[\"VKD3D_SHADER_CACHE_PATH\"] = os.path.join(_shader_path, \"VKD3D_shader_cache\")\n" +
            "    user_settings[\"DXVK_STATE_CACHE_PATH\"] = os.path.join(_shader_path, \"DXVK_state_cache\")\n" +
            "    user_settings[\"AMD_VK_PIPELINE_CACHE_PATH\"] = os.path.join(_shader_path, \"AMDv1\")\n" +
            "    user_settings[\"AMD_VK_PIPELINE_CACHE_FILENAME\"] = \"steamapp_shader_cache\"\n" +
            "    user_settings[\"AMD_VK_USE_PIPELINE_CACHE\"] = \"1\"\n" +
            "    user_settings[\"MESA_SHADER_CACHE_DIR\"] = _shader_path\n" +
            "    user_settings[\"MESA_SHADER_CACHE_MAX_SIZE\"] = \"10G\"\n" +
            "    user_settings[\"MESA_GLSL_CACHE_DIR\"] = _shader_path\n" +
            "    user_settings[\"MESA_GLSL_CACHE_MAX_SIZE\"] = \"10G\"\n" +
            "    user_settings[\"MESA_DISK_CACHE_SINGLE_FILE\"] = \"1\"\n";

        var targetDirs = new List<string>();
        if (Directory.Exists(compatData))
        {
            targetDirs.Add(compatData);
        }

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

        foreach (var dir in targetDirs.Distinct())
        {
            try
            {
                string settingsFile = Path.Combine(dir, "user_settings.py");
                if (!File.Exists(settingsFile) || !File.ReadAllText(settingsFile).Contains("shaderless-glcache-merge-v2", StringComparison.Ordinal))
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
