using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Games;

/// <summary>A game the user added by its exe: <see cref="Exe"/> is the resolved D3D12 process (<see cref="ManualSource.Resolve"/>).
/// <see cref="Confirmed"/>: the user confirmed <see cref="InstallDir"/> as the game's folder (<see cref="ManualSource.RootProblem"/>
/// passed), the folder the anti-cheat check covers before the recorder is allowed; an entry saved without one isn't.</summary>
public sealed record ManualEntry(string Exe, string InstallDir, string Name, bool Confirmed = false);

/// <summary>Games the user added (manual-games.json in the data folder). Id "manual:" + a hash of the exe's full path, so
/// the same exe added again is the same game. No build id: a changed exe (size, write time) is what marks a patch, as for
/// the stores that give none. An entry whose exe is missing (a drive unplugged) is kept and listed again once it's back.</summary>
public sealed class ManualSource(AppStore store) : IGameSource
{
    // the app, the CLI and the scheduled task may change the list at once: one read-modify-write at a time per data folder
    readonly string _mutex = @"Local\SCSKiller.manual-games." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(store.DataDir).ToUpperInvariant())))[..16];

    public Store Store => Store.Manual;

    // game id -> its folder and whether it is confirmed, as the last read of the list had it (replaced whole under the mutex)
    volatile Dictionary<string, (string Root, bool Confirmed)> _roots = [];

    public IReadOnlyList<Game> Discover() => Entries().Where(e => File.Exists(e.Exe)).Select(ToGame).ToList();

    /// <summary>The well-formed entries: an entry with no exe or install folder, or a path that isn't a full one, is left out.</summary>
    /// <remarks>Read under the mutex, so a read that started before a change never publishes the folders it had after it.</remarks>
    public List<ManualEntry> Entries() => Locked(() =>
    {
        var list = store.LoadManualGames().Where(Valid).ToList();
        Read?.Invoke();
        _roots = list.GroupBy(e => IdOf(e.Exe)).ToDictionary(x => x.Key, x => (GameFiles.DirKey(x.First().InstallDir), x.First().Confirmed));
        return list;
    });

    /// <summary>The game's folder is the one the user confirmed (as the list was last read): the recorder may go in.</summary>
    public bool Confirmed(Game g) => _roots.TryGetValue(g.Id, out var r) && r.Confirmed && Same(r.Root, GameFiles.DirKey(g.InstallDir));

    /// <summary>Tests: runs between reading the list and publishing its folders.</summary>
    internal Action? Read { get; set; }

    /// <summary><see cref="Confirmed"/>, the list read again now (under the mutex): for arming.</summary>
    public bool ConfirmedNow(Game g)
    {
        Entries();
        return Confirmed(g);
    }

    /// <summary>A copy of the game from before its folder changed: its state is for another folder than the list's.</summary>
    public bool Stale(Game g) => _roots.TryGetValue(g.Id, out var r) && !Same(r.Root, GameFiles.DirKey(g.InstallDir));

    static bool Valid(ManualEntry e) =>
        e is { Exe.Length: > 0, InstallDir.Length: > 0 } && e.Exe.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(e.Exe)
        && e.InstallDir.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(e.InstallDir);

    public static Game ToGame(ManualEntry e) =>
        new(IdOf(e.Exe), e.Name is { Length: > 0 } n ? n : Path.GetFileName(Path.TrimEndingDirectorySeparator(e.InstallDir)), Store.Manual, e.InstallDir, e.Exe);

    public static string IdOf(string exe) =>
        "manual:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant())))[..16];

    /// <summary>The game and whether it was already added. A confirmed folder replaces the one an entry had.</summary>
    public (Game Game, bool Existed) Add(ManualEntry entry) => Locked(() =>
    {
        var list = Entries();
        var id = IdOf(entry.Exe);
        var i = list.FindIndex(e => IdOf(e.Exe) == id);
        if (i >= 0 && !entry.Confirmed) return (ToGame(list[i]), true);
        if (i >= 0) list[i] = list[i] with { InstallDir = entry.InstallDir, Confirmed = true };
        else list.Add(entry);
        store.SaveManualGames(list);
        Entries();
        return (ToGame(i >= 0 ? list[i] : entry), i >= 0);
    });

    public bool Remove(string gameId) => Locked(() =>
    {
        var list = Entries();
        if (list.RemoveAll(e => IdOf(e.Exe) == gameId) == 0) return false;
        store.SaveManualGames(list);
        Entries();
        return true;
    });

    T Locked<T>(Func<T> change)
    {
        using var m = new Mutex(false, _mutex);
        try { m.WaitOne(); }
        catch (AbandonedMutexException) { }   // its holder died: the file was replaced whole or not at all (WriteAtomic)
        try { return change(); }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>The game an exe the user picked belongs to: its install root (Unreal's, above Engine\ and
    /// &lt;Project&gt;\Binaries; REDengine's, above bin\x64), the process that creates the D3D12 device found as for the
    /// stores (<see cref="GameFiles.GameExe"/>, <see cref="GameFiles.FindExe"/>: a launcher stub becomes its Shipping exe), and a name. ArgumentException
    /// with the message to show for a pick that is no 64-bit program, sits at a drive's root, or launches one of several
    /// exes SCSKiller can't tell apart.</summary>
    public static ManualEntry Resolve(string picked)
    {
        var pick = Path.GetFullPath(picked);
        var file = Path.GetFileName(pick);
        if (!pick.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{file} isn't a program: pick the game's .exe file.");
        if (!File.Exists(pick)) throw new ArgumentException($"{pick} doesn't exist.");
        CheckX64(pick, file);
        var root = InstallRoot(pick);
        if (Path.GetPathRoot(root) == root) throw new ArgumentException($"{file} is at the root of a drive: SCSKiller needs the game in a folder of its own.");
        // as discovery: no import table is read in an install with anti-cheat
        var clean = GameFiles.DetectAntiCheat(new Game("", "", Store.Manual, root, pick)) == AntiCheat.None;
        var graphics = clean ? GameFiles.ImportsGraphics(pick) : null;
        var exe = graphics == true || GameFiles.IsShipping(pick) ? GameFiles.GameExe(root, pick) : GameFiles.FindExe(root, Path.GetRelativePath(root, pick)) ?? pick;
        if (Same(exe, pick) && graphics == false)   // a launcher FindExe kept: the one exe of the install that loads D3D, if there is one
            switch (GameFiles.GraphicsExes(root).Where(f => !Same(f, pick) && X64(f)).ToList())   // a 32-bit launcher is never the game
            {
                case [var one]: exe = one; break;
                case { Count: > 1 } several:
                    throw new ArgumentException($"{file} looks like a launcher, and SCSKiller can't tell which program it starts: "
                        + string.Join(", ", several.Take(4).Select(Path.GetFileName)) + (several.Count > 4 ? "…" : "")
                        + ". Pick the one that opens the game's window.");
            }
        if (!Same(exe, pick)) CheckX64(exe, Path.GetFileName(exe));   // what is added is the game's exe, not the pick
        return new ManualEntry(exe, root, NameOf(exe, pick, root));
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static bool X64(string path)
    {
        try { CheckX64(path, Path.GetFileName(path)); return true; }
        catch (ArgumentException) { return false; }
    }

    static void CheckX64(string path, string file)
    {
        PEHeaders h;
        try
        {
            using var pe = PeFile.Open(path);
            h = pe.PEHeaders;
        }
        catch (BadImageFormatException) { throw new ArgumentException($"{file} isn't a Windows program."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ArgumentException($"Couldn't read {file}: {e.Message}"); }
        if (h.IsDll || h.PEHeader == null) throw new ArgumentException($"{file} isn't a Windows program.");
        if (h.CoffHeader.Machine != Machine.Amd64 || h.PEHeader.Magic != PEMagic.PE32Plus)
            throw new ArgumentException(h.CoffHeader.Machine == Machine.I386
                ? $"{file} is a 32-bit program: SCSKiller compiles 64-bit DirectX 12 games."
                : $"{file} isn't an x64 program: SCSKiller compiles 64-bit DirectX 12 games.");
    }

    /// <summary>The folder suggested as the game's (the user confirms or changes it): Unreal's, holding Engine\ and the project
    /// (the project's own folder when Engine\ isn't beside it); bin*\ or bin\x64*\: the folder above bin. Else the exe's
    /// folder. Nothing above it is read: the folders beside it may be other games.</summary>
    static string InstallRoot(string exe)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var root = dir;
        if (Path.GetDirectoryName(dir) is { } parent)
        {
            if (Path.GetFileName(parent).Equals("Binaries", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(parent) is { } project)
                root = Path.GetDirectoryName(project) is { } above && Directory.Exists(Path.Combine(above, "Engine")) ? above : project;
            else if (Path.GetFileName(parent).Equals("bin", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(parent) is { } game)
                root = game;
            else if (Path.GetFileName(dir).StartsWith("bin", StringComparison.OrdinalIgnoreCase)) root = parent;
        }
        return root;
    }

    // stores' and launchers' folders of games, and Windows' own: never one game's folder
    static readonly string[] Libraries = ["steamapps", "SteamLibrary", "XboxGames", "WindowsApps", "ModifiableWindowsApps", "Epic Games",
        "EpicGames", "GOG Games", "GOG Galaxy", "EA Games", "Origin Games", "Ubisoft Game Launcher", "Battle.net", "Program Files", "Program Files (x86)",
        "ProgramData", "Windows", "Users", "Desktop", "Downloads", "Documents", "OneDrive"];

    /// <summary>Why <paramref name="root"/> can't be confirmed as the game's folder; null = it can. Refused: a missing folder, a
    /// drive or share root, a store's or Windows' folder of many (<see cref="Libraries"/>, steamapps\common, the user's own
    /// folder), a folder that holds another game SCSKiller lists (<paramref name="listed"/>), or one whose subfolders look like
    /// several games (two or more with a program directly in them).</summary>
    public static string? RootProblem(string root, string exe, IEnumerable<Game> listed)
    {
        string full;
        try { full = GameFiles.DirKey(root); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return $"{root} isn't a folder."; }
        if (!Directory.Exists(full)) return $"{full} doesn't exist.";
        if (Path.GetPathRoot(full) is { } drive && GameFiles.DirKey(drive).Equals(full, StringComparison.OrdinalIgnoreCase))
            return $"{full} is a whole drive: pick the game's own folder.";
        if (IsLibrary(full)) return $"{full} holds many programs or games: pick the game's own folder.";
        if (listed.FirstOrDefault(g => !Same(g.ExePath, exe) && (GameFiles.DirKey(g.InstallDir).Equals(full, StringComparison.OrdinalIgnoreCase)
                || GameFiles.Inside(full, g.InstallDir))) is { } other)
            return $"{full} holds {other.Name} too: pick this game's own folder.";
        try
        {
            var games = Directory.EnumerateDirectories(full).Where(d => !NotAGame.Any(n => Path.GetFileName(d).Contains(n, StringComparison.OrdinalIgnoreCase))
                && Directory.EnumerateFiles(d, "*.exe").Any()).Take(2).Count();
            if (games >= 2) return $"{full} looks like a folder of several games: pick this game's own folder.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Couldn't read {full}: {e.Message}"; }
        return null;
    }

    // the user's AppData folders hold launchers' and anti-cheats' own folders (a BattlEye folder in Local): never one game's
    static readonly Environment.SpecialFolder[] SpecialFolders = [Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData];

    /// <summary>A store's or Windows' folder of many (<see cref="Libraries"/>, steamapps\common, the user's own and AppData folders);
    /// <paramref name="full"/> as <see cref="GameFiles.DirKey"/> gives it.</summary>
    internal static bool IsLibrary(string full)
    {
        var name = Path.GetFileName(full);
        return Libraries.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.Equals("common", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Path.GetDirectoryName(full)).Equals("steamapps", StringComparison.OrdinalIgnoreCase)
            || SpecialFolders.Select(Environment.GetFolderPath).Any(f => f.Length > 0 && GameFiles.DirKey(f).Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    // a game's own subfolders that hold programs of their own
    static readonly string[] NotAGame = ["redist", "directx", "crash", "setup", "install", "support", "tools", "launcher", "anticheat", "battleye"];

    // Unreal's launcher stub, engine defaults and launchers name no game
    static readonly string[] Generic = ["UnrealGame", "UE4Game", "UE5Game", "Unity"];

    static string NameOf(string exe, string pick, string root)
    {
        foreach (var f in new[] { exe, pick }.Distinct())
        {
            var v = FileVersionInfo.GetVersionInfo(f);
            foreach (var n in new[] { v.ProductName, v.FileDescription })
                if (n?.Trim() is { Length: > 0 } name && !Generic.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !name.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase) && !name.Contains("launcher", StringComparison.OrdinalIgnoreCase))
                    return name;
        }
        return Path.GetFileName(root);
    }
}
