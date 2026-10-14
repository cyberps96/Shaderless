using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>A game of offline-eac.json: started as <paramref name="Exe"/> with steam_appid.txt holding <paramref name="AppId"/>
/// beside it, it runs offline without EasyAntiCheat.</summary>
public sealed record OfflineEacGame(string Id, string Name, string Exe, string AppId);

/// <summary>The games SCSKiller offers an offline recording session for (offline-eac.json, embedded only: the list changes
/// with a release, never from the server).</summary>
public static class OfflineEac
{
    sealed record Entry(string Id, string Name, string Exe, string[] Sources);
    sealed record ListFile(Entry[] Games, string? About = null);

    /// <summary>Replaceable for tests.</summary>
    public static IReadOnlyList<OfflineEacGame> Current { get; internal set; } = Parse(ContentFile.Embedded("SCSKiller.Core.Games.offline-eac.json"));

    static List<OfflineEacGame> Parse(string json) =>
        [.. (ContentFile.Read<ListFile>(json) ?? throw new InvalidDataException("offline-eac.json")).Games
            .Select(e => new OfflineEacGame(e.Id, e.Name, e.Exe, e.Id.StartsWith("steam:", StringComparison.Ordinal) && e.Id[6..].All(char.IsAsciiDigit) && e.Id.Length > 6
                ? e.Id[6..] : throw new InvalidDataException($"offline-eac.json: {e.Id} isn't a Steam app")))];

    /// <summary>The entry the game is, when it uses EasyAntiCheat and runs on D3D12: its id listed and its exe the listed one.</summary>
    public static OfflineEacGame? Of(Game g, AntiCheat antiCheat, EngineInfo? engine) =>
        antiCheat == AntiCheat.EasyAntiCheat && engine is { Unsupported: null } && engine.GraphicsApi.Contains("D3D12")
        && Current.FirstOrDefault(e => e.Id == g.Id) is { } entry && Path.GetFileName(g.ExePath).Equals(entry.Exe, StringComparison.OrdinalIgnoreCase) ? entry : null;
}
