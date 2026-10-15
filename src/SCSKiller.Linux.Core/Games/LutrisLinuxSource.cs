using Microsoft.Data.Sqlite;
using SCSKiller.Linux.Core.Models;
using System.Text.Json;

namespace SCSKiller.Linux.Core.Games;

/// <summary>
/// Lutris games from its pga.db. Two layouts are handled:
/// 1. a game installed in its own folder (the `directory` column is the game's folder);
/// 2. an Epic Games Store game installed through Lutris (service "egs"): `directory` is the launcher's Wine prefix, and the
///    game's real folder comes from the Epic launcher's manifest inside that prefix (matched by the service id).
/// A Wine prefix with no game resolved from it (for example the Epic Games Store launcher entry itself) is not a game and is skipped.
/// </summary>
public class LutrisLinuxSource : IGameSource
{
    public GameSource SourceType => GameSource.Lutris;

    public async Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<GameInfo>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dbPath = Path.Combine(home, ".local", "share", "lutris", "pga.db");

            if (!File.Exists(dbPath)) return results;

            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, slug, name, runner, directory, installed, service, service_id, configpath FROM games WHERE installed = 1";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string id = reader.GetInt64(0).ToString();
                    string slug = reader.IsDBNull(1) ? id : reader.GetString(1);
                    string name = reader.IsDBNull(2) ? slug : reader.GetString(2);
                    string? directory = reader.IsDBNull(4) ? null : reader.GetString(4);
                    string? service = reader.IsDBNull(6) ? null : reader.GetString(6);
                    string? serviceId = reader.IsDBNull(7) ? null : reader.GetString(7);
                    string? configPath = reader.IsDBNull(8) ? null : reader.GetString(8);

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
                            if (epic == null) continue;   // not installed (or not finished) in that prefix
                            (installPath, exe) = epic.Value;
                        }
                        else if (Directory.Exists(Path.Combine(directory, "drive_c")))
                        {
                            continue;   // a bare Wine prefix (a launcher such as the Epic Games Store itself), not a game
                        }
                    }
                    else
                    {
                        // A game added with its exe only: `directory` is empty and the game's config file names the exe and prefix.
                        // Lutris then points the NVIDIA cache at the exe's folder (measured on a running game).
                        var cfg = ReadGameConfig(home, configPath);
                        if (!cfg.TryGetValue("exe", out var cfgExe) || !File.Exists(cfgExe)) continue;
                        exe = cfgExe;
                        installPath = InferInstallRoot(cfgExe);
                        prefix = cfg.TryGetValue("prefix", out var cfgPrefix) && Directory.Exists(cfgPrefix) ? cfgPrefix : null;
                        glCachePath = Path.GetDirectoryName(cfgExe);
                    }

                    exe ??= GameEngineDetector.FindExecutable(installPath);
                    if (string.IsNullOrEmpty(exe)) continue;

                    var (engine, estimated) = GameEngineDetector.Detect(installPath);

                    string banner = Path.Combine(home, ".local", "share", "lutris", "coverart", $"{slug}.jpg");
                    if (!File.Exists(banner))
                    {
                        banner = Path.Combine(home, ".local", "share", "lutris", "banners", $"{slug}.jpg");
                        if (!File.Exists(banner)) banner = string.Empty;
                    }

                    results.Add(new GameInfo(
                        Id: id,
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
                        GlCachePath: glCachePath
                    ));
                }
            }
            catch
            {
                // Ignore DB read errors
            }

            return results;
        }, cancellationToken);
    }

    /// <summary>The `game:` section's simple values (exe, prefix, ...) of a Lutris game config file; empty when there is none.</summary>
    internal static Dictionary<string, string> ReadGameConfig(string home, string? configPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configPath)) return result;
        foreach (var dir in new[] { Path.Combine(home, ".local", "share", "lutris", "games"), Path.Combine(home, ".config", "lutris", "games") })
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

    /// <summary>The game's install folder from the path of its exe: an Unreal game's exe sits in &lt;root&gt;/&lt;Project&gt;/Binaries/Win64,
    /// and the root (with the Engine folder) is what the engine readers want; any other game: the exe's own folder.</summary>
    internal static string InferInstallRoot(string exePath)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        for (var dir = new DirectoryInfo(exeDir); dir?.Parent != null; dir = dir.Parent)
        {
            if (!dir.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase)) continue;
            var project = dir.Parent;
            var root = project.Parent;
            if (root != null && Directory.Exists(Path.Combine(root.FullName, "Engine"))) return root.FullName;
            return project.FullName;
        }
        return exeDir;
    }

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
