using Microsoft.Data.Sqlite;
using Shaderless.Core.Models;
using System.Text.Json;

namespace Shaderless.Core.Games;

public sealed record LutrisRunnerConfig(
    string RunnerSlug,
    string? Version,
    string? CustomWinePath,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyList<string> RunnerRootDirs,
    string? PrefixCommand = null);

/// <summary>
/// Lutris games from its pga.db (both Native/XDG and Flatpak net.lutris.Lutris).
/// Layouts handled:
/// 1. a game installed in its own folder (the `directory` column is the game's folder);
/// 2. an Epic Games Store game (`service = "egs"`) or Ubisoft Connect game (`service = "ubisoft"` / `"uplay"`) installed through
///    Lutris: `directory` is the launcher's Wine prefix, and the game's real folder comes from the store's manifest / registry
///    inside that prefix;
/// 3. games installed directly inside a Lutris launcher prefix container (such as Ubisoft Connect's
///    `Software\Wow6432Node\Ubisoft\Launcher\Installs\<id>` entries in `system.reg`) when Lutris only has the launcher row;
/// 4. a game added by its executable path (`directory` is empty and the game's YAML config names `exe` and `prefix`).
/// A Wine prefix with no game resolved from it (for example an empty launcher prefix) is not a game and is skipped.
/// </summary>
public class LutrisLinuxSource : IGameSource
{
    private readonly string? _homeOverride;
    private readonly string? _xdgDataOverride;
    private readonly string? _xdgConfigOverride;

    public LutrisLinuxSource(string? homeDir = null, string? xdgDataHome = null, string? xdgConfigHome = null)
    {
        _homeOverride = homeDir;
        _xdgDataOverride = xdgDataHome;
        _xdgConfigOverride = xdgConfigHome;
    }

    public GameSource SourceType => GameSource.Lutris;

    public static IReadOnlyList<(string DataDir, string ConfigDir, string Description, bool IsFlatpak)> GetCandidateInstances(
        string? homeDir = null,
        string? xdgDataHome = null,
        string? xdgConfigHome = null)
    {
        string home = homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string xdgData = AppPaths.GetXdgDataHome(home, xdgDataHome);
        string xdgConfig = AppPaths.GetXdgConfigHome(home, xdgConfigHome);
        string defaultData = Path.Combine(home, ".local", "share");
        string defaultConfig = Path.Combine(home, ".config");

        var candidateInstances = new List<(string DataDir, string ConfigDir, string Description, bool IsFlatpak)>();
        if (!string.Equals(AppPaths.GetCanonicalPath(xdgData), AppPaths.GetCanonicalPath(defaultData), StringComparison.Ordinal) ||
            !string.Equals(AppPaths.GetCanonicalPath(xdgConfig), AppPaths.GetCanonicalPath(defaultConfig), StringComparison.Ordinal))
        {
            candidateInstances.Add((
                Path.Combine(xdgData, "lutris"),
                Path.Combine(xdgConfig, "lutris"),
                "Native Lutris ($XDG_DATA_HOME/lutris)",
                false));
        }

        candidateInstances.Add((
            Path.Combine(home, ".local", "share", "lutris"),
            Path.Combine(home, ".config", "lutris"),
            "Native Lutris (~/.local/share/lutris)",
            false));

        candidateInstances.Add((
            Path.Combine(home, ".var", "app", "net.lutris.Lutris", "data", "lutris"),
            Path.Combine(home, ".var", "app", "net.lutris.Lutris", "config", "lutris"),
            "Flatpak Lutris (~/.var/app/net.lutris.Lutris/data/lutris)",
            true));

        return candidateInstances;
    }

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
                var candidateInstances = GetCandidateInstances(_homeOverride, _xdgDataOverride, _xdgConfigOverride);

                var seenCanonicalDbs = new HashSet<string>(StringComparer.Ordinal);

                foreach (var instance in candidateInstances)
                {
                    string dbPath = Path.Combine(instance.DataDir, "pga.db");
                    bool dbExists = File.Exists(dbPath);
                    checkedPaths.Add(new CheckedPathInfo(dbPath, $"{instance.Description} pga.db", dbExists, instance.IsFlatpak));
                    if (!dbExists) continue;

                    string canonicalDb = AppPaths.GetCanonicalPath(dbPath);
                    if (!seenCanonicalDbs.Add(canonicalDb)) continue;

                    var configDirs = new[]
                    {
                        Path.Combine(instance.DataDir, "games"),
                        Path.Combine(instance.ConfigDir, "games"),
                        Path.Combine(home, ".local", "share", "lutris", "games"),
                        Path.Combine(home, ".config", "lutris", "games")
                    }.Distinct(StringComparer.Ordinal).ToArray();

                    try
                    {
                        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                        connection.Open();

                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "SELECT id, slug, name, runner, directory, installed, service, service_id, configpath FROM games WHERE installed = 1";

                        var discoveredByInstallPath = new Dictionary<string, GameInfo>(StringComparer.Ordinal);
                        var containerPrefixes = new List<(string RawId, string Label, string Directory)>();

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            string rawId = reader.GetInt64(0).ToString();
                            string slug = reader.IsDBNull(1) ? rawId : reader.GetString(1);
                            string name = reader.IsDBNull(2) ? slug : reader.GetString(2);
                            string? runner = reader.IsDBNull(3) ? null : reader.GetString(3);
                            string? directory = reader.IsDBNull(4) ? null : reader.GetString(4);
                            string? service = reader.IsDBNull(6) ? null : reader.GetString(6);
                            string? serviceId = reader.IsDBNull(7) ? null : reader.GetString(7);
                            string? configPath = reader.IsDBNull(8) ? null : reader.GetString(8);

                            string label = $"{name} (id={rawId}, slug={slug})";

                            if (!string.IsNullOrWhiteSpace(runner) &&
                                !runner.Equals("wine", StringComparison.OrdinalIgnoreCase) &&
                                !runner.Equals("proton", StringComparison.OrdinalIgnoreCase))
                            {
                                skipped.Add(new SkippedItemInfo(label, directory ?? dbPath, $"Non-Wine/Proton runner '{runner}'", instance.IsFlatpak));
                                continue;
                            }

                            string installPath;
                            string? exe = null;
                            string? prefix = directory;      // the Wine prefix (what Proton calls the compat data folder)
                            string? glCachePath = directory; // where Lutris points the NVIDIA cache for this game

                            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                            {
                                installPath = directory;
                                if (string.Equals(service, "egs", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(serviceId))
                                {
                                    // Epic Games Store game: `directory` is the launcher's prefix; find the game's own folder in it
                                    var epic = ResolveEpicGame(directory, serviceId);
                                    if (epic == null)
                                    {
                                        skipped.Add(new SkippedItemInfo(label, directory, $"Epic Games Store manifest for '{serviceId}' missing or incomplete in Wine prefix", instance.IsFlatpak));
                                        continue;
                                    }
                                    (installPath, exe) = epic.Value;
                                }
                                else if ((string.Equals(service, "ubisoft", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(service, "uplay", StringComparison.OrdinalIgnoreCase)) &&
                                         !string.IsNullOrWhiteSpace(serviceId))
                                {
                                    // Ubisoft Connect game integrated as a Lutris row: `service_id` may be either Ubisoft's numeric
                                    // installId (`8006`) or GraphQL Store `spaceId` UUID (`1a99b369-...`), so match by ID or title/slug.
                                    var ubi = ResolveUbisoftGame(directory, serviceId, name, slug);
                                    if (ubi == null)
                                    {
                                        skipped.Add(new SkippedItemInfo(label, directory, $"Ubisoft Connect install entry for '{serviceId}' missing or incomplete in Wine prefix", instance.IsFlatpak));
                                        continue;
                                    }
                                    (installPath, exe) = ubi.Value;
                                }
                                else if (Directory.Exists(Path.Combine(directory, "drive_c")))
                                {
                                    // Check if the YAML config names a specific game exe inside or outside the prefix
                                    var cfg = ReadGameConfigFromDirs(configDirs, configPath);
                                    if (cfg.TryGetValue("exe", out var cfgExe) && File.Exists(cfgExe) &&
                                        !IsLauncherContainerExe(cfgExe))
                                    {
                                        exe = cfgExe;
                                        installPath = InferInstallRoot(cfgExe);
                                        if (cfg.TryGetValue("prefix", out var cfgPrefix) && !string.IsNullOrWhiteSpace(cfgPrefix))
                                            prefix = cfgPrefix;
                                    }
                                    else
                                    {
                                        containerPrefixes.Add((rawId, label, directory));
                                        continue;
                                    }
                                }
                            }
                            else
                            {
                                // A game added with its exe only: `directory` is empty and the game's config file names the exe and prefix.
                                // Lutris then points the NVIDIA cache at the exe's folder (measured on a running game).
                                var cfg = ReadGameConfigFromDirs(configDirs, configPath);
                                if (!cfg.TryGetValue("exe", out var cfgExe) || !File.Exists(cfgExe))
                                {
                                    skipped.Add(new SkippedItemInfo(label, configPath ?? dbPath, "Directory empty and no existing 'exe' found in Lutris YAML config", instance.IsFlatpak));
                                    continue;
                                }
                                exe = cfgExe;
                                installPath = InferInstallRoot(cfgExe);
                                prefix = cfg.TryGetValue("prefix", out var cfgPrefix) && !string.IsNullOrWhiteSpace(cfgPrefix) && (Directory.Exists(cfgPrefix) || Path.IsPathRooted(cfgPrefix))
                                    ? cfgPrefix
                                    : null;
                                glCachePath = Path.GetDirectoryName(cfgExe);
                            }

                            exe ??= GameEngineDetector.FindExecutable(installPath);
                            if (string.IsNullOrEmpty(exe))
                            {
                                skipped.Add(new SkippedItemInfo(label, installPath, "No Windows .exe found in install directory", instance.IsFlatpak));
                                continue;
                            }

                            string canonInstallPath = AppPaths.GetCanonicalPath(installPath);
                            var (engine, estimated) = GameEngineDetector.Detect(installPath);
                            string banner = ResolveLutrisBanner(instance.DataDir, slug, home, instance.IsFlatpak);
                            string gameId = instance.IsFlatpak ? $"flatpak_{rawId}" : rawId;

                            var discoveredGame = new GameInfo(
                                Id: gameId,
                                Name: name,
                                Source: GameSource.Lutris,
                                InstallPath: installPath,
                                ExecutablePath: exe,
                                PrefixPath: prefix,
                                BannerPath: string.IsNullOrEmpty(banner) ? null : banner,
                                Engine: engine,
                                State: CacheState.NeedsCompilation,
                                TotalShadersEstimated: estimated,
                                CompiledShadersCount: 0,
                                LastCompiledAt: null,
                                GlCachePath: glCachePath,
                                IsFlatpak: instance.IsFlatpak,
                                LauncherRunner: string.IsNullOrWhiteSpace(slug) ? null : slug
                            );
                            discovered.Add(discoveredGame);
                            discoveredByInstallPath.TryAdd(canonInstallPath, discoveredGame);
                        }

                        // Expand bare Wine prefix launcher containers (e.g. Lutris "Ubisoft Connect" prefix where games are installed inside Ubisoft Connect)
                        foreach (var container in containerPrefixes)
                        {
                            var ubiGames = DiscoverUbisoftGamesInPrefix(container.Directory);
                            int addedFromContainer = 0;
                            foreach (var ubi in ubiGames)
                            {
                                string canonInstall = AppPaths.GetCanonicalPath(ubi.InstallPath);
                                string childRawId = $"{container.RawId}_ubisoft_{ubi.UplayId}";
                                string gameId = instance.IsFlatpak ? $"flatpak_{childRawId}" : childRawId;

                                if (discoveredByInstallPath.TryGetValue(canonInstall, out var explicitCard))
                                {
                                    // Already represented by an explicit Lutris game row (or earlier container).
                                    // Seamlessly migrate any recordings/caches compiled under the fallback container ID (<containerId>_ubisoft_<uplayId>).
                                    if (!string.Equals(gameId, explicitCard.Id, StringComparison.Ordinal))
                                    {
                                        Drivers.ProtonRunner.MigrateSupersededNonSteamGameId(gameId, explicitCard);
                                    }
                                    addedFromContainer++;
                                    continue;
                                }

                                var (engine, estimated) = GameEngineDetector.Detect(ubi.InstallPath);
                                string derivedSlug = SlugifyGameName(ubi.Name);
                                string banner = ResolveLutrisBanner(instance.DataDir, derivedSlug, home, instance.IsFlatpak);

                                var containerChildGame = new GameInfo(
                                    Id: gameId,
                                    Name: ubi.Name,
                                    Source: GameSource.Lutris,
                                    InstallPath: ubi.InstallPath,
                                    ExecutablePath: ubi.Exe,
                                    PrefixPath: container.Directory,
                                    BannerPath: string.IsNullOrEmpty(banner) ? null : banner,
                                    Engine: engine,
                                    State: CacheState.NeedsCompilation,
                                    TotalShadersEstimated: estimated,
                                    CompiledShadersCount: 0,
                                    LastCompiledAt: null,
                                    GlCachePath: container.Directory,
                                    IsFlatpak: instance.IsFlatpak
                                );
                                discovered.Add(containerChildGame);
                                discoveredByInstallPath.TryAdd(canonInstall, containerChildGame);
                                addedFromContainer++;
                            }

                            if (addedFromContainer == 0)
                            {
                                skipped.Add(new SkippedItemInfo(container.Label, container.Directory, "Bare Wine prefix / launcher container (not a standalone game)", instance.IsFlatpak));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(new SkippedItemInfo("pga.db", dbPath, $"SQLite read error: {ex.Message}", instance.IsFlatpak));
                    }
                }
            }
            catch (Exception ex)
            {
                topError = ex.Message;
            }

            return new SourceDiagnostic(GameSource.Lutris, checkedPaths, discovered, skipped, topError);
        }, cancellationToken);
    }

    /// <summary>
    /// Reads Lutris's cascaded runner and environment configuration (`system.yml` → `runners/&lt;runner&gt;.yml` → `games/&lt;configpath&gt;.yml`)
    /// for a discovered Lutris game, allowing Proton switches and environment overrides to be detected immediately before the game is launched.
    /// </summary>
    public static LutrisRunnerConfig? TryReadGameRunnerConfig(
        string gameId,
        bool isFlatpak = false,
        string? homeDir = null,
        string? xdgDataHome = null,
        string? xdgConfigHome = null)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return null;

        bool preferFlatpak = isFlatpak || gameId.StartsWith("flatpak_", StringComparison.OrdinalIgnoreCase);
        string rawId = gameId.StartsWith("flatpak_", StringComparison.OrdinalIgnoreCase)
            ? gameId["flatpak_".Length..]
            : gameId;

        if (!TryExtractLutrisDbNumericId(rawId, out long numericId))
            return null;

        var instances = GetCandidateInstances(homeDir, xdgDataHome, xdgConfigHome)
            .OrderByDescending(i => i.IsFlatpak == preferFlatpak)
            .ToList();

        var seenCanonicalDbs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            string dbPath = Path.Combine(instance.DataDir, "pga.db");
            if (!File.Exists(dbPath)) continue;

            string canonicalDb = AppPaths.GetCanonicalPath(dbPath);
            if (!seenCanonicalDbs.Add(canonicalDb)) continue;

            string? dbRunner = null;
            string? configPath = null;
            bool foundRow = false;

            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT runner, configpath FROM games WHERE id = @id LIMIT 1";
                cmd.Parameters.AddWithValue("@id", numericId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    foundRow = true;
                    dbRunner = reader.IsDBNull(0) ? null : reader.GetString(0);
                    configPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }
            catch
            {
                continue;
            }

            if (!foundRow) continue;

            string runnerSlug = string.IsNullOrWhiteSpace(dbRunner) ? "wine" : dbRunner.Trim().ToLowerInvariant();
            var configRoots = Directory.Exists(instance.ConfigDir)
                ? new[] { instance.ConfigDir, instance.DataDir }
                : new[] { instance.DataDir, instance.ConfigDir };

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            string? version = null;
            string? customWinePath = null;
            string? prefixCommand = null;

            // 1. Lowest level: system.yml
            string? systemYml = ResolveBestLutrisConfigFile(configRoots, "system.yml");
            if (systemYml != null)
            {
                try
                {
                    var sysLines = File.ReadAllLines(systemYml);
                    var sysSec = ParseTopLevelSection(sysLines, "system", keepEmptyValues: true);
                    if (sysSec.TryGetValue("prefix_command", out var spc))
                        prefixCommand = string.IsNullOrWhiteSpace(spc) ? null : spc;

                    foreach (var kv in ParseSystemEnvSection(sysLines))
                        env[kv.Key] = kv.Value;
                }
                catch { }
            }

            // 2. Runner level: runners/<runnerSlug>.yml (plus runners/wine.yml fallback if runnerSlug differs)
            string? runnerYml = ResolveBestLutrisConfigFile(configRoots, Path.Combine("runners", $"{runnerSlug}.yml"));
            if (runnerYml == null && !string.Equals(runnerSlug, "wine", StringComparison.Ordinal))
                runnerYml = ResolveBestLutrisConfigFile(configRoots, Path.Combine("runners", "wine.yml"));

            if (runnerYml != null)
            {
                try
                {
                    var lines = File.ReadAllLines(runnerYml);
                    var runnerSec = ParseTopLevelSection(lines, runnerSlug);
                    if (runnerSec.Count == 0 && !string.Equals(runnerSlug, "wine", StringComparison.Ordinal))
                        runnerSec = ParseTopLevelSection(lines, "wine");

                    if (runnerSec.TryGetValue("version", out var rv) && !string.IsNullOrWhiteSpace(rv))
                        version = rv;
                    if (runnerSec.TryGetValue("custom_wine_path", out var rc) && !string.IsNullOrWhiteSpace(rc))
                        customWinePath = rc;

                    var runnerSysSec = ParseTopLevelSection(lines, "system", keepEmptyValues: true);
                    if (runnerSysSec.TryGetValue("prefix_command", out var rpc))
                        prefixCommand = string.IsNullOrWhiteSpace(rpc) ? null : rpc;

                    foreach (var kv in ParseSystemEnvSection(lines))
                        env[kv.Key] = kv.Value;
                }
                catch { }
            }

            // 3. Highest level: games/<configPath>.yml
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                string? gameYml = ResolveBestLutrisConfigFile(configRoots, Path.Combine("games", $"{configPath}.yml"));
                if (gameYml != null)
                {
                    try
                    {
                        var lines = File.ReadAllLines(gameYml);
                        var gameRunnerSec = ParseTopLevelSection(lines, runnerSlug);
                        if (gameRunnerSec.Count == 0)
                        {
                            string altSection = string.Equals(runnerSlug, "wine", StringComparison.Ordinal) ? "proton" : "wine";
                            gameRunnerSec = ParseTopLevelSection(lines, altSection);
                        }

                        if (gameRunnerSec.TryGetValue("version", out var gv) && !string.IsNullOrWhiteSpace(gv))
                            version = gv;
                        if (gameRunnerSec.TryGetValue("custom_wine_path", out var gc) && !string.IsNullOrWhiteSpace(gc))
                            customWinePath = gc;

                        var gameSysSec = ParseTopLevelSection(lines, "system", keepEmptyValues: true);
                        if (gameSysSec.TryGetValue("prefix_command", out var gpc))
                            prefixCommand = string.IsNullOrWhiteSpace(gpc) ? null : gpc;

                        foreach (var kv in ParseSystemEnvSection(lines))
                            env[kv.Key] = kv.Value;
                    }
                    catch { }
                }
            }

            var runnerRootDirs = new List<string>();
            string? customRunnerDir = TryReadCustomRunnerDir(configRoots);
            if (!string.IsNullOrWhiteSpace(customRunnerDir))
            {
                runnerRootDirs.Add(Path.Combine(customRunnerDir, "proton"));
                runnerRootDirs.Add(Path.Combine(customRunnerDir, "wine"));
            }
            runnerRootDirs.Add(Path.Combine(instance.DataDir, "runners", "proton"));
            runnerRootDirs.Add(Path.Combine(instance.DataDir, "runners", "wine"));
            foreach (var other in instances)
            {
                runnerRootDirs.Add(Path.Combine(other.DataDir, "runners", "proton"));
                runnerRootDirs.Add(Path.Combine(other.DataDir, "runners", "wine"));
            }

            return new LutrisRunnerConfig(
                RunnerSlug: runnerSlug,
                Version: version,
                CustomWinePath: customWinePath,
                Env: env,
                RunnerRootDirs: runnerRootDirs.Distinct(StringComparer.Ordinal).ToList(),
                PrefixCommand: prefixCommand);
        }

        return null;
    }

    private static string? ResolveBestLutrisConfigFile(IEnumerable<string> configRoots, string relativePath)
    {
        string? bestPath = null;
        long bestTicks = -1;
        foreach (var root in configRoots.Distinct(StringComparer.Ordinal))
        {
            string candidate = Path.Combine(root, relativePath);
            if (!File.Exists(candidate)) continue;
            try
            {
                long ticks = new FileInfo(candidate).LastWriteTimeUtc.Ticks;
                if (bestPath == null || ticks > bestTicks)
                {
                    bestPath = candidate;
                    bestTicks = ticks;
                }
            }
            catch
            {
                bestPath ??= candidate;
            }
        }
        return bestPath;
    }

    private static string? TryReadCustomRunnerDir(IEnumerable<string> configRoots)
    {
        string? confFile = ResolveBestLutrisConfigFile(configRoots, "lutris.conf");
        if (confFile == null) return null;
        try
        {
            foreach (var raw in File.ReadLines(confFile))
            {
                string trimmed = raw.Trim();
                if (!trimmed.StartsWith("runner_dir", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                string key = trimmed[..eq].Trim();
                if (!string.Equals(key, "runner_dir", StringComparison.OrdinalIgnoreCase)) continue;
                string val = trimmed[(eq + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        catch { }
        return null;
    }

    /// <summary>The `game:` section's simple values (exe, prefix, ...) of a Lutris game config file; empty when there is none.</summary>
    internal static Dictionary<string, string> ReadGameConfig(string home, string? configPath)
    {
        var dirs = new[]
        {
            Path.Combine(home, ".local", "share", "lutris", "games"),
            Path.Combine(home, ".config", "lutris", "games"),
            Path.Combine(home, ".var", "app", "net.lutris.Lutris", "data", "lutris", "games"),
            Path.Combine(home, ".var", "app", "net.lutris.Lutris", "config", "lutris", "games")
        };
        return ReadGameConfigFromDirs(dirs, configPath);
    }

    internal static Dictionary<string, string> ReadGameConfigFromDirs(IEnumerable<string> dirs, string? configPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configPath)) return result;
        foreach (var dir in dirs)
        {
            string file = Path.Combine(dir, configPath + ".yml");
            if (!File.Exists(file)) continue;
            try { return ParseGameSection(File.ReadAllLines(file)); } catch { }
        }
        return result;
    }

    /// <summary>The key: value lines directly under the top-level `game:` of a Lutris config (two-space indent).</summary>
    internal static Dictionary<string, string> ParseGameSection(IEnumerable<string> lines) =>
        ParseTopLevelSection(lines, "game");

    /// <summary>The key: value lines directly under a top-level section (e.g. `game:`, `wine:`, `proton:`) at two-space indent.</summary>
    internal static Dictionary<string, string> ParseTopLevelSection(
        IEnumerable<string> lines,
        string sectionName,
        bool keepEmptyValues = false)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string header = sectionName + ":";
        bool inSection = false;
        foreach (var raw in lines)
        {
            if (raw.Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            if (!char.IsWhiteSpace(raw[0]))
            {
                inSection = string.Equals(raw.TrimEnd(), header, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || raw.Length < 3 || raw[0] != ' ' || raw[1] != ' ' || raw[2] == ' ') continue;
            int colon = raw.IndexOf(':');
            if (colon < 0) continue;
            string key = raw[2..colon].Trim();
            string value = UnquoteYamlScalar(raw[(colon + 1)..]);
            if (key.Length > 0 && (keepEmptyValues || value.Length > 0))
                result[key] = value;
        }
        return result;
    }

    /// <summary>Parses environment variables under Lutris's top-level `system:` -> `  env:` block (four-space indent),
    /// preserving empty values (`''`) so per-game empty overrides clear global/runner settings.</summary>
    internal static Dictionary<string, string> ParseSystemEnvSection(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inSystem = false;
        bool inEnv = false;

        foreach (var raw in lines)
        {
            if (raw.Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            if (!char.IsWhiteSpace(raw[0]))
            {
                inSystem = string.Equals(raw.TrimEnd(), "system:", StringComparison.OrdinalIgnoreCase);
                inEnv = false;
                continue;
            }
            if (!inSystem) continue;

            if (raw.Length >= 3 && raw[0] == ' ' && raw[1] == ' ' && raw[2] != ' ')
            {
                inEnv = string.Equals(raw[2..].TrimEnd(), "env:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inEnv && raw.Length >= 5 && raw.StartsWith("    ", StringComparison.Ordinal) && raw[4] != ' ')
            {
                int colon = raw.IndexOf(':', 4);
                if (colon < 0) continue;
                string key = UnquoteYamlScalar(raw[4..colon]);
                string value = UnquoteYamlScalar(raw[(colon + 1)..]);
                if (key.Length > 0)
                    result[key] = value;
            }
        }

        return result;
    }

    private static string UnquoteYamlScalar(string rawValue)
    {
        string v = rawValue.Trim();
        if (v.Length >= 2 && ((v[0] == '\'' && v[^1] == '\'') || (v[0] == '"' && v[^1] == '"')))
            return v[1..^1];
        if (string.Equals(v, "null", StringComparison.OrdinalIgnoreCase) || v == "~")
            return string.Empty;
        return v;
    }

    /// <summary>The game's install folder from the path of its exe: delegates to <see cref="GameEngineDetector.InferInstallRoot"/>
    /// so Unreal, CryEngine, REDengine, Dagor, and multi-level binary subdirectories resolve to the true game root.</summary>
    internal static string InferInstallRoot(string exePath) =>
        GameEngineDetector.InferInstallRoot(exePath);

    /// <summary>The install folder and exe (Linux paths) of the Epic game <paramref name="appName"/> in the Wine prefix
    /// <paramref name="prefix"/>, from the Epic launcher's manifests; null when it isn't installed there.</summary>
    internal static (string InstallPath, string? Exe)? ResolveEpicGame(string prefix, string appName)
    {
        string manifests = Path.Combine(prefix, "drive_c", "ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) return null;

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (!root.TryGetProperty("AppName", out var an) || !string.Equals(an.GetString(), appName, StringComparison.OrdinalIgnoreCase)) continue;
                if (root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;

                string? location = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                string? install = WindowsPathToLinux(prefix, location);
                if (install == null || !Directory.Exists(install)) return null;

                string? launch = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;
                string? exe = null;
                if (!string.IsNullOrWhiteSpace(launch))
                {
                    var candidate = Path.Combine(install, launch.Replace('\\', '/'));
                    if (File.Exists(candidate)) exe = candidate;
                }
                return (install, exe);
            }
            catch
            {
                // an unreadable manifest: try the next one
            }
        }
        return null;
    }

    internal static bool TryExtractLutrisDbNumericId(string rawId, out long numericId)
    {
        numericId = 0;
        if (string.IsNullOrWhiteSpace(rawId)) return false;

        int ubiIdx = rawId.IndexOf("_ubisoft_", StringComparison.OrdinalIgnoreCase);
        ReadOnlySpan<char> candidate = ubiIdx > 0 ? rawId.AsSpan(0, ubiIdx) : rawId.AsSpan();
        return long.TryParse(candidate, out numericId);
    }

    private static bool IsLauncherContainerExe(string exePath)
    {
        string fileName = Path.GetFileName(exePath.Replace('\\', '/'));
        return fileName.Equals("EpicGamesLauncher.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("UbisoftConnect.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("upc.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Uplay.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("UplayService.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("EADesktop.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("EALauncher.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Origin.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Battle.net.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLutrisBanner(string lutrisDataDir, string slug, string? homeDir = null, bool isFlatpak = false)
    {
        if (string.IsNullOrWhiteSpace(slug)) return string.Empty;
        foreach (var dirName in new[] { "coverart", "banners" })
        {
            foreach (var ext in new[] { ".jpg", ".png" })
            {
                string candidate = Path.Combine(lutrisDataDir, dirName, slug + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(homeDir))
        {
            string cacheDir = isFlatpak
                ? Path.Combine(homeDir, ".var", "app", "net.lutris.Lutris", "cache", "lutris", "ubisoft", "covers")
                : Path.Combine(homeDir, ".cache", "lutris", "ubisoft", "covers");
            foreach (var ext in new[] { ".jpg", ".png" })
            {
                string candidate = Path.Combine(cacheDir, slug + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return string.Empty;
    }

    internal static string SlugifyGameName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string decomposed = name.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        bool prevDash = false;
        foreach (char c in decomposed)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                prevDash = false;
            }
            else if (c is 'ß')
            {
                sb.Append("ss");
                prevDash = false;
            }
            else if (c is 'æ' or 'Æ')
            {
                sb.Append("ae");
                prevDash = false;
            }
            else if (c is 'œ' or 'Œ')
            {
                sb.Append("oe");
                prevDash = false;
            }
            else if (c is 'ø' or 'Ø')
            {
                sb.Append('o');
                prevDash = false;
            }
            else if (c is ' ' or '-' or '_' or ':' or '/' or '.')
            {
                if (sb.Length > 0 && !prevDash)
                {
                    sb.Append('-');
                    prevDash = true;
                }
            }
        }
        return sb.ToString().TrimEnd('-');
    }

    internal static string NormalizeServiceSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return string.Empty;
        string s = SlugifyGameName(slug);
        if (s.EndsWith("-ubisoft", StringComparison.Ordinal) && s.Length > "-ubisoft".Length)
            s = s[..^"-ubisoft".Length];
        else if (s.EndsWith("-uplay", StringComparison.Ordinal) && s.Length > "-uplay".Length)
            s = s[..^"-uplay".Length];
        return s.TrimEnd('-');
    }

    internal static (string InstallPath, string? Exe)? ResolveUbisoftGame(
        string prefix,
        string serviceId,
        string? gameName = null,
        string? slug = null)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) return null;

        var installed = DiscoverUbisoftGamesInPrefix(prefix);
        if (installed.Count == 0) return null;

        // 1. Direct numeric Uplay install ID match (e.g. "8006")
        string trimmedId = serviceId.Trim();
        var byId = installed.FirstOrDefault(g => string.Equals(g.UplayId, trimmedId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(byId.InstallPath))
            return (byId.InstallPath, byId.Exe);

        // 2. Lutris GraphQL Ubisoft service stores `spaceId` UUIDs in `service_id` while Wine's `system.reg` only stores numeric
        //    install IDs + DisplayName/InstallDir. Match normalized game title / Lutris slug against DisplayName and folder name.
        var targetSlugs = new HashSet<string>(StringComparer.Ordinal);
        string nameSlug = SlugifyGameName(gameName);
        if (nameSlug.Length > 0) targetSlugs.Add(nameSlug);
        string rowSlug = NormalizeServiceSlug(slug);
        if (rowSlug.Length > 0) targetSlugs.Add(rowSlug);

        if (targetSlugs.Count > 0)
        {
            foreach (var g in installed)
            {
                string displaySlug = SlugifyGameName(g.Name);
                string folderSlug = SlugifyGameName(Path.GetFileName(g.InstallPath));
                if ((displaySlug.Length > 0 && targetSlugs.Contains(displaySlug)) ||
                    (folderSlug.Length > 0 && targetSlugs.Contains(folderSlug)))
                {
                    return (g.InstallPath, g.Exe);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads installed Ubisoft Connect games registered in a Wine prefix's `system.reg` under
    /// `Software\Wow6432Node\Ubisoft\Launcher\Installs\&lt;id&gt;` (plus official titles from `Uninstall\Uplay Install &lt;id&gt;`).
    /// </summary>
    internal static IReadOnlyList<(string UplayId, string Name, string InstallPath, string Exe)> DiscoverUbisoftGamesInPrefix(string prefix)
    {
        var results = new List<(string UplayId, string Name, string InstallPath, string Exe)>();
        if (string.IsNullOrWhiteSpace(prefix) || !Directory.Exists(prefix))
            return results;

        string? regFile = new[]
        {
            Path.Combine(prefix, "system.reg"),
            Path.Combine(prefix, "pfx", "system.reg")
        }.FirstOrDefault(File.Exists);

        if (regFile == null)
            return results;

        var rawEntries = new List<(string UplayId, string WinInstallDir)>();
        var displayNamesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? currentInstallUplayId = null;
            string? currentUninstallUplayId = null;
            foreach (var rawLine in File.ReadLines(regFile))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line[0] == '[')
                {
                    currentInstallUplayId = TryParseUbisoftInstallsRegistryHeader(line);
                    currentUninstallUplayId = currentInstallUplayId == null
                        ? TryParseUbisoftUninstallRegistryHeader(line)
                        : null;
                    continue;
                }

                if (currentInstallUplayId != null && TryParseWineRegStringValue(line, "InstallDir", out string installDirWin))
                {
                    rawEntries.Add((currentInstallUplayId, installDirWin));
                    currentInstallUplayId = null;
                }
                else if (currentUninstallUplayId != null && TryParseWineRegStringValue(line, "DisplayName", out string displayName))
                {
                    if (!string.IsNullOrWhiteSpace(displayName))
                        displayNamesById[currentUninstallUplayId] = displayName.Trim();
                    currentUninstallUplayId = null;
                }
            }
        }
        catch
        {
            return results;
        }

        var seenCanonical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (uplayId, winInstallDir) in rawEntries)
        {
            string? mapped = WindowsPathToLinux(prefix, winInstallDir);
            if (string.IsNullOrWhiteSpace(mapped)) continue;

            string installPath = Path.TrimEndingDirectorySeparator(mapped);
            if (!Directory.Exists(installPath)) continue;

            string canonical = AppPaths.GetCanonicalPath(installPath);
            if (!seenCanonical.Add(canonical)) continue;

            string? exe = GameEngineDetector.FindExecutable(installPath);
            if (string.IsNullOrEmpty(exe)) continue;

            string folderName = Path.GetFileName(installPath);
            string gameName = displayNamesById.TryGetValue(uplayId, out var dn) && !string.IsNullOrWhiteSpace(dn)
                ? dn
                : (!string.IsNullOrWhiteSpace(folderName) ? folderName : $"Ubisoft Game {uplayId}");
            results.Add((uplayId, gameName, installPath, exe));
        }

        return results;
    }

    private static string? TryParseUbisoftInstallsRegistryHeader(string headerLine)
    {
        int closeBracket = headerLine.IndexOf(']');
        if (closeBracket <= 1) return null;

        ReadOnlySpan<char> keyPath = headerLine.AsSpan(1, closeBracket - 1);
        string[] prefixes =
        {
            @"Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\",
            @"Software\\Ubisoft\\Launcher\\Installs\\",
            @"Software\Wow6432Node\Ubisoft\Launcher\Installs\",
            @"Software\Ubisoft\Launcher\Installs\"
        };

        foreach (var pfx in prefixes)
        {
            if (keyPath.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
            {
                ReadOnlySpan<char> remainder = keyPath[pfx.Length..];
                if (remainder.IsEmpty || remainder.Contains('\\'))
                    return null;
                return remainder.ToString();
            }
        }

        return null;
    }

    private static string? TryParseUbisoftUninstallRegistryHeader(string headerLine)
    {
        int closeBracket = headerLine.IndexOf(']');
        if (closeBracket <= 1) return null;

        ReadOnlySpan<char> keyPath = headerLine.AsSpan(1, closeBracket - 1);
        string[] prefixes =
        {
            @"Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Uplay Install ",
            @"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Uplay Install ",
            @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install ",
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install "
        };

        foreach (var pfx in prefixes)
        {
            if (keyPath.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
            {
                ReadOnlySpan<char> remainder = keyPath[pfx.Length..].Trim();
                if (remainder.IsEmpty || remainder.Contains('\\'))
                    return null;
                return remainder.ToString();
            }
        }

        return null;
    }

    private static bool TryParseWineRegStringValue(string trimmedLine, string expectedName, out string value)
    {
        value = string.Empty;
        string prefix = $"\"{expectedName}\"=\"";
        if (!trimmedLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || trimmedLine.Length <= prefix.Length)
            return false;

        var sb = new System.Text.StringBuilder(trimmedLine.Length - prefix.Length);
        for (int i = prefix.Length; i < trimmedLine.Length; i++)
        {
            char c = trimmedLine[i];
            if (c == '\\' && i + 1 < trimmedLine.Length)
            {
                char next = trimmedLine[++i];
                sb.Append(next switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => next
                });
            }
            else if (c == '"')
            {
                break;
            }
            else
            {
                sb.Append(c);
            }
        }

        value = sb.ToString();
        return value.Length > 0;
    }

    /// <summary>A Windows path inside a Wine prefix ("C:\Program Files\X") as a Linux path; null for a drive it can't map.</summary>
    internal static string? WindowsPathToLinux(string prefix, string? winPath)
    {
        if (string.IsNullOrWhiteSpace(winPath) || winPath.Length < 3 || winPath[1] != ':') return null;

        char letter = char.ToLowerInvariant(winPath[0]);
        string rest = winPath[2..].Replace('\\', '/').TrimStart('/');

        string root = letter == 'c'
            ? Path.Combine(prefix, "drive_c")
            : Path.Combine(prefix, "dosdevices", $"{letter}:");   // other drives are symlinks in dosdevices
        return Directory.Exists(root) ? Path.Combine(root, rest) : null;
    }
}
