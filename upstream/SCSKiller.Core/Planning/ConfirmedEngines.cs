using SCSKiller.Core.App;

namespace SCSKiller.Core.Planning;

/// <summary>The Unreal versions and engine forks whose RootSig rule a real game has confirmed: confirmed-engines.json,
/// embedded in Core and served at /v1/content/confirmed-engines.json so a
/// confirmation can be added without a release. An entry is a version and its fork ("GAME_..." as EngineInfo.Fork has
/// it; none = stock). The list in use is the embedded one plus the server's: the server's copy only adds
/// (<see cref="Contains"/>), and is used when it passes <see cref="TryParse"/> (<see cref="ContentFile"/>).</summary>
public sealed class ConfirmedEngines
{
    public const string ContentPath = "v1/content/confirmed-engines.json";
    public const int MaxEngines = 500;

    sealed record Entry(string Version, string Game, string Evidence, string? Fork = null);
    sealed record ListFile(Entry[] Engines, string? About = null);

    readonly HashSet<(string Version, string? Fork)> set;

    ConfirmedEngines(ListFile file) => set = file.Engines.Select(e => (e.Version, e.Fork)).ToHashSet();

    /// <summary>The list if the document is valid JSON of the right shape (no unknown fields) within the caps, every
    /// version "major.minor" and every fork a "GAME_" id; else null.</summary>
    public static ConfirmedEngines? TryParse(string json) =>
        ContentFile.Read<ListFile>(json) is { Engines.Length: >= 1 and <= MaxEngines } f && (f.About == null || ContentFile.Text(f.About, 1000))
        && f.Engines.All(Valid) ? new(f) : null;

    static bool Valid(Entry? e) => e is not null &&
        e.Version is { Length: >= 3 and <= 5 } v && v.Count(c => c == '.') == 1 && v.All(c => char.IsAsciiDigit(c) || c == '.') && char.IsAsciiDigit(v[0]) && char.IsAsciiDigit(v[^1])
        && (e.Fork == null || e.Fork is { Length: > 5 and <= 64 } && e.Fork.StartsWith("GAME_", StringComparison.Ordinal) && e.Fork.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        && ContentFile.Text(e.Game, 100) && ContentFile.Text(e.Evidence, 300);

    public static ConfirmedEngines Embedded { get => field ??= TryParse(ContentFile.Embedded("SCSKiller.Core.Planning.confirmed-engines.json"))!; }

    /// <summary>The server's copy when there is one, else the embedded list.</summary>
    public static ConfirmedEngines Current { get => field ??= Embedded; set; }

    public bool Contains(EngineInfo e) => set.Contains((e.Version, e.Fork)) || this != Embedded && Embedded.Contains(e);

    public static ConfirmedEngines? Cached(string file) => ContentFile.Cached(file, TryParse);

    public static Task<ConfirmedEngines?> FetchAsync(string cacheFile, RouteFailover? routes = null, TimeSpan? budget = null) =>
        ContentFile.FetchAsync(ContentPath, cacheFile, TryParse, routes, budget);
}
