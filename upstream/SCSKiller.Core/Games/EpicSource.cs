using System.Text.Json;

namespace SCSKiller.Core.Games;

/// <summary>Installed Epic games from the launcher's %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item.</summary>
public sealed class EpicSource(string? manifestsDir = null) : IGameSource
{
    public Store Store => Store.Epic;

    public static string DefaultManifestsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public IReadOnlyList<Game> Discover()
    {
        var dir = manifestsDir ?? DefaultManifestsDir;
        if (!Directory.Exists(dir)) return [];
        var games = new List<Game>();
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                if (S("AppName") is not { } app || S("InstallLocation") is not { } install) continue;
                if (S("MainGameAppName") is { Length: > 0 } main && main != app) continue;   // add-on sharing the main game's install (Legendary names a base game's own)
                if (GameFiles.FindExe(install, string.IsNullOrEmpty(S("LaunchExecutable")) ? null : S("LaunchExecutable")) is not { } exe) continue;
                games.Add(new Game($"epic:{app}", S("DisplayName") ?? app, Store.Epic, install, exe, S("AppVersionString")));
            }
            catch (JsonException) { }   // half-written manifest while the launcher installs
        }
        return games;
    }
}
