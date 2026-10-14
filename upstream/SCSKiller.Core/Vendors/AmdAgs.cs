using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Vendors;

/// <summary>The names an Unreal game registers with the AMD driver when it creates its device through AGS
/// (<c>agsDriverExtensionsDX12_CreateDevice</c>, UE 4.25-5.6 D3D12Adapter.cpp): the project name as the app,
/// "UnrealEngine&lt;major&gt;.&lt;minor&gt;" as the engine, both versions unspecified. AMD keys the DxcCache on the app name,
/// not on the exe name, unless a driver profile matches (<see cref="AmdAppCache.AgsKey"/>). <paramref name="ExeUnread"/>: the
/// exe can't be read (an Xbox app game's), so AGS is assumed; only the keys the game is seen holding prove it.</summary>
public sealed record AgsRegistration(string App, string Engine, bool ExeUnread = false);

public static class AmdAgs
{
    public const string DllName = "amd_ags_x64.dll";
    const string CreateDevice = "agsDriverExtensionsDX12_CreateDevice";

    /// <summary>Null unless the game is Unreal 4.25 or later (earlier versions create the D3D12 device without AGS, though
    /// they link it: 4.20-4.24 call only agsInit), its project name is known and it links AGS or its exe can't be read.</summary>
    public static AgsRegistration? Of(Game g, EngineInfo? engine)
    {
        if (engine?.Family != "Unreal" || Regex.Match(engine.Version, @"^(\d+)\.(\d+)") is not { Success: true } v) return null;
        var (major, minor) = (int.Parse(v.Groups[1].Value), int.Parse(v.Groups[2].Value));
        if (major < 4 || major == 4 && minor < 25 || ProjectName(g) is not { } project) return null;
        var uses = UsesAgs(g.ExePath);
        return uses == false ? null : new(project, $"UnrealEngine{major}.{minor}", ExeUnread: uses == null);
    }

    /// <summary>UE's FApp::GetProjectName as packaged: the folder holding <c>Binaries\&lt;platform&gt;\</c> the exe is in,
    /// else the only <c>&lt;install&gt;\&lt;Project&gt;\Content\Paks</c>. The case is the driver's: the key hashes it as is.</summary>
    public static string? ProjectName(Game g)
    {
        if (UnrealUserCache.Project(g.ExePath) is { } p) return p;
        if (!Directory.Exists(g.InstallDir)) return null;
        var projects = Directory.EnumerateDirectories(g.InstallDir)
            .Where(d => !Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(d, "Content", "Paks")))
            .Select(Path.GetFileName).Take(2).ToList();
        return projects.Count == 1 ? projects[0] : null;
    }

    /// <summary>The AGS DLL a warm creates its device through: the game's own next to its exe when it is AGS 6 (the ABI
    /// scskiller_warm speaks; loaded read-only), else <paramref name="bundled"/>. UE links AGS statically, so most get
    /// the bundled one: the key depends on the app name only, not on the AGS build.</summary>
    public static string? DllFor(Game g, string? bundled)
    {
        var own = Path.Combine(Path.GetDirectoryName(g.ExePath) ?? "", DllName);
        try { return File.Exists(own) && FileVersionInfo.GetVersionInfo(own).FileMajorPart == 6 ? own : bundled; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return bundled; }
    }

    static readonly ConcurrentDictionary<(string, long, DateTime), bool?> exports = new();

    /// <summary>The game creates its device through AGS: an <see cref="DllName"/> next to the exe, or AGS linked into the exe
    /// (its static library declares every function dllexport, so a monolithic UE exe exports them). Null: the exe can't be
    /// read (access denied, as an Xbox app game's Shipping exe is), so it isn't known.</summary>
    public static bool? UsesAgs(string exePath)
    {
        if (File.Exists(Path.Combine(Path.GetDirectoryName(exePath) ?? "", DllName))) return true;
        var fi = new FileInfo(exePath);
        if (!fi.Exists) return false;
        return exports.GetOrAdd((fi.FullName.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc), _ =>
        {
            try
            {
                using var pe = PeFile.Open(fi.FullName);
                return PeFile.ExportNames(pe).Contains(CreateDevice);
            }
            catch (UnauthorizedAccessException) { return null; }
            catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException) { return false; }
        });
    }
}
