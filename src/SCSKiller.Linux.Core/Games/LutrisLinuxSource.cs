using Microsoft.Data.Sqlite;
using SCSKiller.Linux.Core.Models;
using System.Text.Json;

namespace SCSKiller.Linux.Core.Games;

/// <summary>
/// Lutris games from its pga.db (both Native/XDG and Flatpak net.lutris.Lutris).
/// Layouts handled:
/// 1. a game installed in its own folder (the `directory` column is the game's folder);
/// 2. an Epic Games Store game installed through Lutris (service "egs"): `directory` is the launcher's Wine prefix, and the
///    game's real folder comes from the Epic launcher's manifest inside that prefix (matched by the service id);
/// 3. a game added by its executable path (`directory` is empty and the game's YAML config names `exe` and `prefix`).
/// A Wine prefix with no game resolved from it (for example the Epic Games Store launcher entry itself) is not a game and is skipped.
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
                string xdgConfig = AppPaths.GetXdgConfigHome(home, _xdgConfigOverride);
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
                                else if (Directory.Exists(Path.Combine(directory, "drive_c")))
                                {
                                    // Check if the YAML config names a specific game exe inside or outside the prefix
                                    var cfg = ReadGameConfigFromDirs(configDirs, configPath);
                                    if (cfg.TryGetValue("exe", out var cfgExe) && File.Exists(cfgExe) &&
                                        !cfgExe.EndsWith("EpicGamesLauncher.exe", StringComparison.OrdinalIgnoreCase))
                                    {
                                        exe = cfgExe;
                                        installPath = InferInstallRoot(cfgExe);
                                        if (cfg.TryGetValue("prefix", out var cfgPrefix) && !string.IsNullOrWhiteSpace(cfgPrefix))
                                            prefix = cfgPrefix;
                                    }
                                    else
                                    {
                                        skipped.Add(new SkippedItemInfo(label, directory, "Bare Wine prefix / launcher container (not a standalone game)", instance.IsFlatpak));
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

                            var (engine, estimated) = GameEngineDetector.Detect(installPath);

                            string banner = Path.Combine(instance.DataDir, "coverart", $"{slug}.jpg");
                            if (!File.Exists(banner))
                            {
                                banner = Path.Combine(instance.DataDir, "banners", $"{slug}.jpg");
                                if (!File.Exists(banner)) banner = string.Empty;
                            }

                            string gameId = instance.IsFlatpak ? $"flatpak_{rawId}" : rawId;

                            discovered.Add(new GameInfo(
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
                                IsFlatpak: instance.IsFlatpak
                            ));
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
    internal static Dictionary<string, string> ParseGameSection(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inGame = false;
        foreach (var raw in lines)
        {
            if (raw.Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            if (!char.IsWhiteSpace(raw[0])) { inGame = raw.TrimEnd() == "game:"; continue; }
            if (!inGame || raw.Length < 3 || raw[0] != ' ' || raw[1] != ' ' || raw[2] == ' ') continue;
            int colon = raw.IndexOf(':');
            if (colon < 0) continue;
            string key = raw[2..colon].Trim();
            string value = raw[(colon + 1)..].Trim().Trim('\'', '"');
            if (key.Length > 0 && value.Length > 0) result[key] = value;
        }
        return result;
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
