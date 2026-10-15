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
                cmd.CommandText = "SELECT id, slug, name, runner, directory, installed, service, service_id FROM games WHERE installed = 1";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string id = reader.GetInt64(0).ToString();
                    string slug = reader.IsDBNull(1) ? id : reader.GetString(1);
                    string name = reader.IsDBNull(2) ? slug : reader.GetString(2);
                    string? directory = reader.IsDBNull(4) ? null : reader.GetString(4);
                    string? service = reader.IsDBNull(6) ? null : reader.GetString(6);
                    string? serviceId = reader.IsDBNull(7) ? null : reader.GetString(7);

                    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;

                    string installPath = directory;
                    string? exe = null;

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
                        PrefixPath: directory,
                        BannerPath: string.IsNullOrEmpty(banner) ? null : banner,
                        Engine: engine,
                        State: CacheState.NeedsCompilation,
                        TotalShadersEstimated: estimated,
                        CompiledShadersCount: 0,
                        LastCompiledAt: null
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
