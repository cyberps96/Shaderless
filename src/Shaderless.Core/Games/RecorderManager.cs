using Shaderless.Engine.App;
using Shaderless.Engine.Planning;
using Shaderless.Core.Drivers;
using Shaderless.Core.Models;

namespace Shaderless.Core.Games;

/// <summary>Record mode on Linux, as Shaderless.Engine's ShaderEngine.Install / ImportRecording / WriteKeys do on Windows:
/// the d3d12.dll recorder (Native/d3d12.dll, built from Native/recorder.cpp with mingw-w64) goes next to the game's exe,
/// where Wine loads it before the prefix's vkd3d-proton d3d12.dll. It writes shaderless.db there (the inbox); Shaderless
/// imports it into games/&lt;id&gt;/recording.db and writes shaderless.keys back so nothing is recorded twice.</summary>
public static class RecorderManager
{
    internal static string? TestDataDirOverride
    {
        get => Shaderless.Core.AppPaths.TestDataDirOverride;
        set => Shaderless.Core.AppPaths.TestDataDirOverride = value;
    }

    public static string DataDir => Shaderless.Core.AppPaths.DataDir;
    public static string GameDir(string gameId) => Path.Combine(DataDir, "games", Sanitize(gameId));
    public static string RecordingPath(string gameId) => Path.Combine(GameDir(gameId), "recording.db");
    public static string CompileNotePath(string gameId) => Path.Combine(GameDir(gameId), "compile-note.txt");

    /// <summary>Remembers a note about the last compile (null removes it), shown on the game's card while its cache exists.</summary>
    public static void SaveCompileNote(string gameId, string? note)
    {
        try
        {
            string path = CompileNotePath(gameId);
            if (string.IsNullOrWhiteSpace(note))
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(GameDir(gameId));
            File.WriteAllText(path, note.Trim());
        }
        catch { }
    }

    public static string? LoadCompileNote(string gameId)
    {
        try
        {
            string path = CompileNotePath(gameId);
            if (!File.Exists(path)) return null;
            string note = File.ReadAllText(path).Trim();
            return note.Length > 0 ? note : null;
        }
        catch
        {
            return null;
        }
    }

    static string Sanitize(string id) => string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));

    static readonly (string Legacy, string Current)[] LegacyFilePairs =
    [
        ("scskiller.db", "shaderless.db"),
        ("scskiller_gen.db", "shaderless_gen.db"),
        ("scskiller.ini", "shaderless.ini"),
        ("scskiller.armed", "shaderless.armed"),
        ("scskiller.keys", "shaderless.keys"),
        ("scskiller.warmed", "shaderless.warmed"),
        ("scskiller.log", "shaderless.log"),
        ("scskiller_creates.csv", "shaderless_creates.csv"),
        ("scskiller_failed.keys", "shaderless_failed.keys")
    ];

    public static void MigrateLegacyFiles(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            string dll = Path.Combine(dir, "d3d12.dll");
            string legacyArmed = Path.Combine(dir, "scskiller.armed");
            string legacyIni = Path.Combine(dir, "scskiller.ini");
            bool hasLegacyConfigs = File.Exists(legacyArmed) || File.Exists(legacyIni);
            if (File.Exists(dll) && ShaderEngine.IsOurProxy(dll) && (hasLegacyConfigs || ShaderEngine.IsLegacyProxy(dll)))
            {
                bool inUse = IsFileOpenByExternalProcess(dll) || IsFileMappedByExternalProcess(dll);
                if (!inUse && ProxySource() is { } src)
                {
                    ReplaceFileAtomically(src, dll);
                    var dxgi = Path.Combine(dir, "dxgi.dll");
                    if (File.Exists(dxgi) && ShaderEngine.IsOurProxy(dxgi) && DxgiProxySource() is { } dxgiSrc)
                    {
                        ReplaceFileAtomically(dxgiSrc, dxgi);
                    }
                    if (File.Exists(legacyIni))
                    {
                        ReplaceTextAtomically(Path.Combine(dir, "shaderless.ini"), IniText);
                        File.Delete(legacyIni);
                    }
                    if (File.Exists(legacyArmed))
                    {
                        ReplaceTextAtomically(Path.Combine(dir, "shaderless.armed"), "[shaderless]\r\narmed=1\r\n");
                        File.Delete(legacyArmed);
                    }
                }
            }
        }
        catch { }

        foreach (var (legacy, current) in LegacyFilePairs)
        {
            try
            {
                string legacyPath = Path.Combine(dir, legacy);
                if (!File.Exists(legacyPath)) continue;
                string currentPath = Path.Combine(dir, current);
                if (!File.Exists(currentPath))
                    File.Move(legacyPath, currentPath, overwrite: false);
            }
            catch { }
        }
    }

    static string? ProxySource()
    {
        foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "Native", "d3d12.dll"), Path.Combine(AppContext.BaseDirectory, "d3d12.dll") })
            if (File.Exists(p) && ShaderEngine.IsOurProxy(p)) return p;
        return null;
    }

    static string? DxgiProxySource()
    {
        foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "Native", "dxgi.dll"), Path.Combine(AppContext.BaseDirectory, "dxgi.dll") })
            if (File.Exists(p) && ShaderEngine.IsOurProxy(p)) return p;
        return null;
    }

    /// <summary>The folder of the exe that really runs (an Unreal game's Shipping exe or resolved main game binary, not its launcher stub).</summary>
    public static string? ExeDir(GameInfo game)
    {
        var exe = GameEngineDetector.ResolveRealGameExecutable(game.InstallPath, game.ExecutablePath);
        return string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long ExeLen, long ExeTicks, bool NeedsDxgi)> _streamlineCache =
        new(StringComparer.Ordinal);

    /// <summary>True when NVIDIA Streamline's sl.interposer.dll is beside the game's executable and neither the executable
    /// nor its local static imports import d3d12.dll. In those games, Streamline / NGX loads System32's d3d12.dll first and
    /// bypasses &lt;GameDir&gt;\d3d12.dll unless our companion &lt;GameDir&gt;\dxgi.dll preloads &lt;GameDir&gt;\d3d12.dll at process startup.</summary>
    public static bool NeedsStreamlineCompanion(GameInfo game)
    {
        try
        {
            var exe = GameEngineDetector.ResolveRealGameExecutable(game.InstallPath, game.ExecutablePath);
            if (string.IsNullOrEmpty(exe)) return false;
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "sl.interposer.dll"))) return false;
            var fi = new FileInfo(exe);
            if (!fi.Exists) return true;
            long len = fi.Length;
            long ticks = fi.LastWriteTimeUtc.Ticks;
            if (len >= 4096 && _streamlineCache.TryGetValue(fi.FullName, out var cached) && cached.ExeLen == len && cached.ExeTicks == ticks)
                return cached.NeedsDxgi;

            bool needs = Shaderless.Engine.Games.GameFiles.StreamlineFirst(fi.FullName) != false;
            if (len >= 4096)
                _streamlineCache[fi.FullName] = (len, ticks, needs);
            return needs;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsInstalled(GameInfo game) =>
        ExeDir(game) is { } dir &&
        ShaderEngine.IsOurProxy(Path.Combine(dir, "d3d12.dll")) &&
        (ShaderEngine.IsOurProxy(Path.Combine(dir, "dxgi.dll")) || !NeedsStreamlineCompanion(game));

    public sealed record RecorderCountsEntry(string Stamp, int Recorded, int Unwarmed);

    private static readonly object _countsCacheLock = new();
    private static string? _loadedCountsDataDir;
    private static Dictionary<string, RecorderCountsEntry> _countsCache = new(StringComparer.Ordinal);

    private static string GetFileStamp(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "-";
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= 0) return "0";
            return $"{fi.FullName}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return "err";
        }
    }

    private static void EnsureCountsCacheLoadedNoLock(string dataDir)
    {
        if (string.Equals(_loadedCountsDataDir, dataDir, StringComparison.Ordinal))
            return;

        _loadedCountsDataDir = dataDir;
        _countsCache = new Dictionary<string, RecorderCountsEntry>(StringComparer.Ordinal);

        string path = Path.Combine(dataDir, "recorder-counts.json");
        if (!File.Exists(path))
            return;

        try
        {
            string json = File.ReadAllText(path);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, RecorderCountsEntry>>(json);
            if (loaded != null)
            {
                foreach (var kv in loaded)
                    _countsCache[kv.Key] = kv.Value;
            }
        }
        catch { }
    }

    private static void SaveCountsCacheNoLock(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            string path = Path.Combine(dataDir, "recorder-counts.json");
            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            string json = System.Text.Json.JsonSerializer.Serialize(
                _countsCache,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    internal static void InvalidateCountsCache(string gameId)
    {
        try
        {
            string dataDir = DataDir;
            lock (_countsCacheLock)
            {
                EnsureCountsCacheLoadedNoLock(dataDir);
                if (_countsCache.Remove(gameId))
                    SaveCountsCacheNoLock(dataDir);
            }
        }
        catch { }
    }

    /// <summary>Returns both total recorded pipelines and unwarmed recorded pipelines for <paramref name="game"/>,
    /// backed by a stamp-validated persistent cache (<c>recorder-counts.json</c>) so startup scans never re-parse
    /// hundreds of MB of unchanged <c>recording.db</c> or <c>warmed-keys.txt</c> files.</summary>
    public static (int Recorded, int Unwarmed) GetRecordedAndUnwarmedCounts(GameInfo game)
    {
        try
        {
            string dataDir = DataDir;
            var exeDir = ExeDir(game);
            MigrateLegacyFiles(exeDir);

            string recPath = RecordingPath(game.Id);
            string? inboxPath = exeDir is { } d ? Path.Combine(d, "shaderless.db") : null;
            string warmedPath = WarmedKeysPath(game.Id);

            string recStamp = GetFileStamp(recPath);
            string inboxStamp = GetFileStamp(inboxPath);
            if (recStamp == "0" && (inboxStamp == "-" || inboxStamp == "0"))
                return (0, 0);

            string warmedStamp = GetFileStamp(warmedPath);
            string combinedStamp = $"{recStamp}|{inboxStamp}|{warmedStamp}";

            lock (_countsCacheLock)
            {
                EnsureCountsCacheLoadedNoLock(dataDir);
                if (_countsCache.TryGetValue(game.Id, out var cached) &&
                    string.Equals(cached.Stamp, combinedStamp, StringComparison.Ordinal))
                {
                    return (cached.Recorded, cached.Unwarmed);
                }
            }

            var recordedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in new[] { recPath, inboxPath })
            {
                if (path != null && new FileInfo(path) is { Exists: true, Length: > 0 } f)
                    recordedKeys.UnionWith(KeysOf(f));
            }

            int recordedCount = recordedKeys.Count;
            int unwarmedCount = 0;
            if (recordedCount > 0)
            {
                var warmed = GetCachedWarmedKeysReadOnly(game.Id);
                if (warmed.Count > 0)
                {
                    foreach (var k in recordedKeys)
                    {
                        if (!warmed.Contains(k))
                            unwarmedCount++;
                    }
                }
            }

            lock (_countsCacheLock)
            {
                EnsureCountsCacheLoadedNoLock(dataDir);
                _countsCache[game.Id] = new RecorderCountsEntry(combinedStamp, recordedCount, unwarmedCount);
                SaveCountsCacheNoLock(dataDir);
            }

            return (recordedCount, unwarmedCount);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>Pipelines recorded so far: Shaderless's copy plus what the inbox holds that the copy doesn't.
    /// Validated by file path, byte length, and write timestamp in <see cref="GetRecordedAndUnwarmedCounts"/>.</summary>
    public static int RecordedPsos(GameInfo game) => GetRecordedAndUnwarmedCounts(game).Recorded;

    /// <summary>Pipelines recorded in recording.db / shaderless.db that have not yet been warmed into warmed-keys.txt.</summary>
    public static int UnwarmedRecordedPsos(GameInfo game) => GetRecordedAndUnwarmedCounts(game).Unwarmed;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime Written, HashSet<string> Keys)> _keys = new();

    static HashSet<string> KeysOf(FileInfo f)
    {
        if (_keys.TryGetValue(f.FullName, out var c) && c.Length == f.Length && c.Written == f.LastWriteTimeUtc) return c.Keys;
        var keys = PsoDb.Read(f.FullName).Where(r => r.Tag is not ('B' or 'N' or 'L' or 'W')).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        _keys[f.FullName] = (f.Length, f.LastWriteTimeUtc, keys);
        return keys;
    }

    static Shaderless.Engine.Game ToCore(GameInfo g, string exeDir) => new(g.Id, g.Name,
        g.Source switch { GameSource.Steam => Shaderless.Engine.Store.Steam, GameSource.Heroic => Shaderless.Engine.Store.Epic, _ => Shaderless.Engine.Store.Manual },
        g.InstallPath, File.Exists(g.ExecutablePath) && Path.GetDirectoryName(g.ExecutablePath) == exeDir ? g.ExecutablePath
            : Directory.EnumerateFiles(exeDir, "*.exe").FirstOrDefault() ?? g.ExecutablePath);

    public const string IniText = "[shaderless]\r\n; written by Shaderless: the recorder records every pipeline the game creates into shaderless.db\r\nmode=record\r\n";

    public static string WarmedKeysPath(string gameId) => Path.Combine(GameDir(gameId), "warmed-keys.txt");

    /// <summary>The anti-cheat found in the game's folder (None if there is none, or the game's exe wasn't found).</summary>
    public static Shaderless.Engine.AntiCheat DetectAntiCheat(GameInfo game)
    {
        try { return ExeDir(game) is { } dir ? Shaderless.Engine.Games.GameFiles.DetectAntiCheat(ToCore(game, dir)) : Shaderless.Engine.AntiCheat.None; }
        catch { return Shaderless.Engine.AntiCheat.None; }
    }

    /// <summary>Attempts to automatically install the recorder on an eligible game (used when AutoRecordAllGames is enabled).
    /// Always skips games with anti-cheat or existing third-party d3d12.dll / dxgi.dll files.</summary>
    public static bool TryAutoInstall(GameInfo game)
    {
        try
        {
            if (IsInstalled(game)) return true;
            if (DetectAntiCheat(game) != Shaderless.Engine.AntiCheat.None) return false;
            var dir = ExeDir(game);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var dll = Path.Combine(dir, "d3d12.dll");
            if (File.Exists(dll) && !ShaderEngine.IsOurProxy(dll)) return false;
            if (NeedsStreamlineCompanion(game))
            {
                var dxgi = Path.Combine(dir, "dxgi.dll");
                if (File.Exists(dxgi) && !ShaderEngine.IsOurProxy(dxgi)) return false;
            }
            Install(game);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Installs and arms the recorder. Refuses next to anti-cheat (a d3d12.dll there could get the account banned)
    /// and over another mod's d3d12.dll (or dxgi.dll when Streamline companion proxy is required).</summary>
    public static void Install(GameInfo game)
    {
        var dir = ExeDir(game) ?? throw new InvalidOperationException($"{game.Name}: the game's exe wasn't found");
        var src = ProxySource() ?? throw new FileNotFoundException("the recorder (Native/d3d12.dll) was not found next to Shaderless");
        var ac = Shaderless.Engine.Games.GameFiles.DetectAntiCheat(ToCore(game, dir));
        if (ac != Shaderless.Engine.AntiCheat.None)
            throw new InvalidOperationException($"{game.Name} uses {ac}: a d3d12.dll in its folder could get the account banned. Not installing.");
        var dll = Path.Combine(dir, "d3d12.dll");
        if (File.Exists(dll) && !ShaderEngine.IsOurProxy(dll))
            throw new InvalidOperationException($"{dll} already exists and is not Shaderless's (another mod or wrapper). Not replacing it.");
        bool needsDxgi = NeedsStreamlineCompanion(game);
        string? dxgiSrc = null;
        string? dxgiDll = null;
        if (needsDxgi)
        {
            dxgiDll = Path.Combine(dir, "dxgi.dll");
            if (File.Exists(dxgiDll) && !ShaderEngine.IsOurProxy(dxgiDll))
                throw new InvalidOperationException($"{dxgiDll} already exists and is not Shaderless's (another mod or wrapper). Streamline games (sl.interposer.dll) require dxgi.dll to record D3D12 pipelines.");
            dxgiSrc = DxgiProxySource() ?? throw new FileNotFoundException("the Streamline companion proxy (Native/dxgi.dll) was not found next to Shaderless");
        }
        MigrateLegacyFiles(dir);
        MigrateLegacyFiles(GameDir(game.Id));
        var goverlayVars = Path.Combine(dir, "goverlay.vars");
        if (File.Exists(goverlayVars) &&
            !File.Exists(Path.Combine(dir, "OptiScaler.dll")) &&
            !File.Exists(Path.Combine(dir, "ReShade64.dll")))
        {
            try { File.Delete(goverlayVars); } catch { }
        }
        ReplaceFileAtomically(src, dll);
        if (needsDxgi && dxgiSrc != null && dxgiDll != null)
        {
            ReplaceFileAtomically(dxgiSrc, dxgiDll);
        }
        var refStorageDir = Path.Combine(dir, "_storage_");
        if (Directory.Exists(refStorageDir))
        {
            var refDll = Path.Combine(refStorageDir, "d3d12.dll");
            if (ShaderEngine.IsOurProxy(refDll)) try { File.Delete(refDll); } catch { }
            var refDxgi = Path.Combine(refStorageDir, "dxgi.dll");
            if (ShaderEngine.IsOurProxy(refDxgi)) try { File.Delete(refDxgi); } catch { }
        }
        ReplaceTextAtomically(Path.Combine(dir, "shaderless.ini"), IniText);
        ReplaceTextAtomically(Path.Combine(dir, "shaderless.armed"), "[shaderless]\r\narmed=1\r\n");
        WriteKeys(game, dir);
        SyncWarmedFileToExeDir(game, dir);
    }

    /// <summary>Imports what was recorded, then removes the recorder and its files.</summary>
    public static void Uninstall(GameInfo game)
    {
        var dir = ExeDir(game) ?? throw new InvalidOperationException($"{game.Name}: the game's exe wasn't found");
        Import(game);
        var dll = Path.Combine(dir, "d3d12.dll");
        if (ShaderEngine.IsOurProxy(dll)) File.Delete(dll);
        var dxgi = Path.Combine(dir, "dxgi.dll");
        if (ShaderEngine.IsOurProxy(dxgi)) File.Delete(dxgi);
        var refStorageDir = Path.Combine(dir, "_storage_");
        if (Directory.Exists(refStorageDir))
        {
            var refDll = Path.Combine(refStorageDir, "d3d12.dll");
            if (ShaderEngine.IsOurProxy(refDll)) try { File.Delete(refDll); } catch { }
            var refDxgi = Path.Combine(refStorageDir, "dxgi.dll");
            if (ShaderEngine.IsOurProxy(refDxgi)) try { File.Delete(refDxgi); } catch { }
        }
        try
        {
            foreach (var tmp in Directory.EnumerateFiles(dir, "d3d12.dll.*.tmp"))
                try { File.Delete(tmp); } catch { }
            foreach (var tmp in Directory.EnumerateFiles(dir, "dxgi.dll.*.tmp"))
                try { File.Delete(tmp); } catch { }
        }
        catch { }
        foreach (var f in new[] { "shaderless.ini", "shaderless.armed", "shaderless.log", "shaderless_creates.csv", Recordings.KeysFile, Recordings.WarmedFile, "scskiller.ini", "scskiller.armed", "scskiller.log", "scskiller_creates.csv", "scskiller.keys", "scskiller.warmed" })
            try { File.Delete(Path.Combine(dir, f)); } catch { }
        foreach (var dbName in new[] { "shaderless.db", "scskiller.db" })
        {
            var inbox = Path.Combine(dir, dbName);
            if (!File.Exists(inbox) || new FileInfo(inbox).Length == 0) try { File.Delete(inbox); } catch { }
        }
        InvalidateCountsCache(game.Id);
    }

    /// <summary>Merges the game folder's shaderless.db into Shaderless's compact copy and empties it (only while the game
    /// is not running and no external process holds shaderless.db open on Linux). Returns the pipelines added.</summary>
    public static int Import(GameInfo game, Func<GameInfo, bool>? isGameRunning = null)
    {
        if (ExeDir(game) is not { } dir) return 0;
        MigrateLegacyFiles(dir);
        var inbox = new FileInfo(Path.Combine(dir, "shaderless.db"));
        if (!inbox.Exists || inbox.Length == 0) return 0;

        var checkRunning = isGameRunning ?? (g => ProtonRunner.IsGameRunning(g));
        try
        {
            if (checkRunning(game)) return 0;
        }
        catch { }

        if (isGameRunning == null && IsFileOpenByExternalProcess(inbox.FullName))
            return 0;

        var store = RecordingPath(game.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        int added;
        using (Recordings.Lock(store))
        {
            inbox.Refresh();
            if (!inbox.Exists || inbox.Length == 0) return 0;

            added = Recordings.Merge(store, inbox.FullName, null).Count;

            bool stillIdle = true;
            try { stillIdle = !checkRunning(game); } catch { }
            if (stillIdle && (isGameRunning != null || !IsFileOpenByExternalProcess(inbox.FullName)))
            {
                Recordings.Rotate(inbox.FullName, inbox.Length);
            }
        }
        InvalidateCountsCache(game.Id);
        WriteKeys(game, dir);
        return added;
    }

    /// <summary>Checks Linux /proc/&lt;pid&gt;/fd symlinks to see if any process other than Shaderless currently holds
    /// <paramref name="filePath"/> open (for example Wine/wineserver or a running game writing shaderless.db).</summary>
    internal static bool IsFileOpenByExternalProcess(string filePath, string procRoot = "/proc", int? selfPid = null)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !Directory.Exists(procRoot)) return false;
            string fullPath = Path.GetFullPath(filePath);
            string fileNameSuffix = "/" + Path.GetFileName(fullPath);
            int myPid = selfPid ?? Environment.ProcessId;
            bool hasTargetInode = ProtonRunner.TryGetLinuxFileIdentity(fullPath, out var targetInode, out _);

            foreach (var pidDir in Directory.EnumerateDirectories(procRoot))
            {
                string pidStr = Path.GetFileName(pidDir);
                if (!int.TryParse(pidStr, out int pid) || pid == myPid) continue;

                string fdDir = Path.Combine(pidDir, "fd");
                IEnumerable<string> fds;
                try
                {
                    if (!Directory.Exists(fdDir)) continue;
                    fds = Directory.EnumerateFileSystemEntries(fdDir);
                }
                catch { continue; }

                foreach (var fd in fds)
                {
                    try
                    {
                        string? linkTarget = new FileInfo(fd).LinkTarget;
                        if (string.IsNullOrEmpty(linkTarget)) continue;
                        if (string.Equals(linkTarget, fullPath, StringComparison.Ordinal))
                            return true;
                        if (hasTargetInode &&
                            linkTarget.EndsWith(fileNameSuffix, StringComparison.Ordinal) &&
                            ProtonRunner.TryGetLinuxFileIdentity(linkTarget, out var fdInode, out _) &&
                            fdInode == targetInode)
                        {
                            return true;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Checks Linux /proc/&lt;pid&gt;/maps to see if any process other than Shaderless has <paramref name="filePath"/>
    /// memory-mapped (Wine/Proton maps PE DLLs like d3d12.dll into memory and closes the file descriptor, so /proc/pid/fd alone won't show it).</summary>
    internal static bool IsFileMappedByExternalProcess(string filePath, string procRoot = "/proc", int? selfPid = null)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !Directory.Exists(procRoot)) return false;
            string fullPath = Path.GetFullPath(filePath);
            string fileNameSuffix = "/" + Path.GetFileName(fullPath);
            int myPid = selfPid ?? Environment.ProcessId;
            bool hasTargetId = ProtonRunner.TryGetLinuxFileIdentity(fullPath, out var targetId, out _);
            string inodeToken = hasTargetId ? targetId.Ino.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

            foreach (var pidDir in Directory.EnumerateDirectories(procRoot))
            {
                string pidStr = Path.GetFileName(pidDir);
                if (!int.TryParse(pidStr, out int pid) || pid == myPid) continue;

                string mapsPath = Path.Combine(pidDir, "maps");
                try
                {
                    if (!File.Exists(mapsPath)) continue;
                    foreach (var line in File.ReadLines(mapsPath))
                    {
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.Length > fullPath.Length &&
                            line[line.Length - fullPath.Length - 1] == ' ' &&
                            line.EndsWith(fullPath, StringComparison.Ordinal))
                        {
                            return true;
                        }
                        if (hasTargetId && line.EndsWith(fileNameSuffix, StringComparison.Ordinal))
                        {
                            // /proc/<pid>/maps format: addr perms offset dev inode pathname
                            var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 5 && string.Equals(parts[4], inodeToken, StringComparison.Ordinal))
                                return true;
                        }
                    }
                }
                catch { continue; }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Writes <paramref name="sourceFile"/> to a sibling temp file and renames it over <paramref name="destinationFile"/>,
    /// allocating a fresh inode so any process that already memory-mapped the previous file is never truncated in-place (no SIGBUS).</summary>
    internal static void ReplaceFileAtomically(string sourceFile, string destinationFile)
    {
        string tmp = $"{destinationFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourceFile, tmp, overwrite: true);
            File.Move(tmp, destinationFile, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    internal static void ReplaceTextAtomically(string destinationFile, string content)
    {
        string tmp = $"{destinationFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, destinationFile, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>shaderless.keys next to the recorder: what the copy already holds, so the next session records only what's new.</summary>
    static void WriteKeys(GameInfo game, string dir)
    {
        if (!ShaderEngine.IsOurProxy(Path.Combine(dir, "d3d12.dll"))) return;
        var store = RecordingPath(game.Id);
        if (!File.Exists(store)) return;
        using (Recordings.Lock(store)) Recordings.WriteKeys(store, null, Path.Combine(dir, Recordings.KeysFile));
    }

    static void SyncWarmedFileToExeDir(GameInfo game, string dir)
    {
        try
        {
            if (!ShaderEngine.IsOurProxy(Path.Combine(dir, "d3d12.dll"))) return;
            MigrateLegacyFiles(GameDir(game.Id));
            string src = Path.Combine(GameDir(game.Id), Recordings.WarmedFile);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(dir, Recordings.WarmedFile), overwrite: true);
        }
        catch { }
    }

    public static string? GetWarmedStampMismatchReason(string gameId, string? currentDriverVersion, string? currentProtonStamp)
    {
        try
        {
            string path = WarmedKeysPath(gameId);
            if (!File.Exists(path)) return null;

            using var sr = new StreamReader(path);
            string? firstLine = sr.ReadLine();
            if (string.IsNullOrEmpty(firstLine) || !firstLine.StartsWith("#STAMP:", StringComparison.Ordinal))
                return null;

            string savedStamp = firstLine["#STAMP:".Length..].Trim();
            int firstPipe = savedStamp.IndexOf('|');
            if (firstPipe <= 0) return null;

            string savedDriver = savedStamp[..firstPipe];
            string savedProton = savedStamp[(firstPipe + 1)..];

            if (!string.IsNullOrEmpty(savedDriver) &&
                !string.IsNullOrEmpty(currentDriverVersion) &&
                !string.Equals(savedDriver, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentDriverVersion, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(savedDriver, currentDriverVersion, StringComparison.Ordinal))
            {
                return "driver updated";
            }

            string? protonOrLaunchDiff = Drivers.ProtonRunner.CompareProtonStamps(savedProton, currentProtonStamp);
            if (protonOrLaunchDiff != null)
            {
                return protonOrLaunchDiff;
            }
        }
        catch { }

        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long LastWriteTicks, string? SavedStamp, HashSet<string> Keys)> _warmedFileCache =
        new(StringComparer.Ordinal);

    private static (string? SavedStamp, HashSet<string> Keys) ReadAndCacheWarmedFile(FileInfo fi)
    {
        long len = fi.Length;
        long ticks = fi.LastWriteTimeUtc.Ticks;
        if (_warmedFileCache.TryGetValue(fi.FullName, out var cached) && cached.Length == len && cached.LastWriteTicks == ticks)
            return (cached.SavedStamp, cached.Keys);

        var parsed = new HashSet<string>(StringComparer.Ordinal);
        string? savedStamp = null;
        try
        {
            using var sr = new StreamReader(fi.FullName);
            string? firstLine = sr.ReadLine();
            if (firstLine != null)
            {
                if (firstLine.StartsWith("#STAMP:", StringComparison.Ordinal))
                {
                    savedStamp = firstLine["#STAMP:".Length..].Trim();
                }
                else if (firstLine.Length == 40)
                {
                    parsed.Add(firstLine.Trim());
                }

                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 40)
                        parsed.Add(trimmed);
                }
            }
        }
        catch { }

        _warmedFileCache[fi.FullName] = (len, ticks, savedStamp, parsed);
        return (savedStamp, parsed);
    }

    private static IReadOnlySet<string> GetCachedWarmedKeysReadOnly(string gameId)
    {
        try
        {
            var fi = new FileInfo(WarmedKeysPath(gameId));
            if (!fi.Exists || fi.Length <= 0)
                return new HashSet<string>(StringComparer.Ordinal);
            return ReadAndCacheWarmedFile(fi).Keys;
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static HashSet<string> LoadWarmedKeys(string gameId, string? expectedRuntimeStamp = null, string? previousWorkDir = null)
    {
        var fi = new FileInfo(WarmedKeysPath(gameId));
        if (!fi.Exists)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(previousWorkDir) && Directory.Exists(previousWorkDir))
            {
                try
                {
                    MigrateLegacyFiles(previousWorkDir);
                    string prevRecDb = Path.Combine(previousWorkDir, "shaderless.db");
                    if (File.Exists(prevRecDb) && new FileInfo(prevRecDb).Length > 0)
                    {
                        foreach (var r in PsoDb.Read(prevRecDb))
                        {
                            if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A' or 'P' or '1' or '2')
                                result.Add(r.Key);
                        }
                    }

                    string prevPlanBin = Path.Combine(previousWorkDir, "plan.bin");
                    if (File.Exists(prevPlanBin) && new FileInfo(prevPlanBin).Length > 0)
                    {
                        foreach (var r in Shaderless.Engine.Planning.Planner.PlanBody(prevPlanBin))
                        {
                            if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A' or 'P' or '1' or '2')
                                result.Add(r.Key);
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        var (savedStamp, cachedKeys) = ReadAndCacheWarmedFile(fi);
        if (savedStamp != null &&
            !string.IsNullOrEmpty(expectedRuntimeStamp) &&
            !Drivers.ProtonRunner.AreDriverAndProtonStampsEquivalent(savedStamp, expectedRuntimeStamp))
        {
            return new HashSet<string>(StringComparer.Ordinal); // Driver, Proton, or shader launch options changed -> full rebuild required
        }

        return new HashSet<string>(cachedKeys, StringComparer.Ordinal);
    }

    public static byte[]? BuildWarmedSnapshot(IEnumerable<string> dbs)
    {
        try
        {
            var existingDbs = dbs.Where(f => File.Exists(f) && new FileInfo(f).Length > 0).ToList();
            if (existingDbs.Count == 0) return null;
            return Recordings.Warmed(existingDbs, Array.Empty<string>());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Merges two sorted <c>SCSKWRM1</c> (<c>shaderless.warmed</c>) binary snapshots in <c>O(N + M)</c> time
    /// without string allocations so incremental delta compiles preserve all previously warmed keys and tuple hashes.
    /// </summary>
    public static byte[] MergeWarmedSnapshots(byte[]? existingBytes, byte[] deltaBytes)
    {
        if (existingBytes == null || existingBytes.Length < 12)
            return deltaBytes;
        if (deltaBytes.Length < 12)
            return existingBytes;

        ReadOnlySpan<byte> magic = "SCSKWRM1"u8;
        if (!existingBytes.AsSpan(0, 8).SequenceEqual(magic) ||
            !deltaBytes.AsSpan(0, 8).SequenceEqual(magic) ||
            (existingBytes.Length - 12) % 20 != 0 ||
            (deltaBytes.Length - 12) % 20 != 0)
        {
            return deltaBytes;
        }

        uint existingKeyCount = BitConverter.ToUInt32(existingBytes, 8);
        uint deltaKeyCount = BitConverter.ToUInt32(deltaBytes, 8);
        long existingKeysBytes = (long)existingKeyCount * 20L;
        long deltaKeysBytes = (long)deltaKeyCount * 20L;

        if (12L + existingKeysBytes > existingBytes.Length || 12L + deltaKeysBytes > deltaBytes.Length)
            return deltaBytes;

        static byte[] MergeSorted20ByteSpans(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, out int mergedCount)
        {
            int maxBytes = a.Length + b.Length;
            if (maxBytes == 0)
            {
                mergedCount = 0;
                return Array.Empty<byte>();
            }

            byte[] buf = new byte[maxBytes];
            int ia = 0, ib = 0, outPos = 0;
            while (ia < a.Length && ib < b.Length)
            {
                var sa = a.Slice(ia, 20);
                var sb = b.Slice(ib, 20);
                int cmp = sa.SequenceCompareTo(sb);
                if (cmp < 0)
                {
                    sa.CopyTo(buf.AsSpan(outPos, 20));
                    outPos += 20;
                    ia += 20;
                }
                else if (cmp > 0)
                {
                    sb.CopyTo(buf.AsSpan(outPos, 20));
                    outPos += 20;
                    ib += 20;
                }
                else
                {
                    sa.CopyTo(buf.AsSpan(outPos, 20));
                    outPos += 20;
                    ia += 20;
                    ib += 20;
                }
            }
            while (ia < a.Length)
            {
                a.Slice(ia, 20).CopyTo(buf.AsSpan(outPos, 20));
                outPos += 20;
                ia += 20;
            }
            while (ib < b.Length)
            {
                b.Slice(ib, 20).CopyTo(buf.AsSpan(outPos, 20));
                outPos += 20;
                ib += 20;
            }

            mergedCount = outPos / 20;
            return outPos == buf.Length ? buf : buf[..outPos];
        }

        int eSplit = 12 + (int)existingKeysBytes;
        int dSplit = 12 + (int)deltaKeysBytes;

        byte[] mergedKeys = MergeSorted20ByteSpans(
            existingBytes.AsSpan(12, (int)existingKeysBytes),
            deltaBytes.AsSpan(12, (int)deltaKeysBytes),
            out int totalKeys);
        byte[] mergedTuples = MergeSorted20ByteSpans(
            existingBytes.AsSpan(eSplit),
            deltaBytes.AsSpan(dSplit),
            out _);

        byte[] output = new byte[12 + mergedKeys.Length + mergedTuples.Length];
        magic.CopyTo(output.AsSpan(0, 8));
        BitConverter.TryWriteBytes(output.AsSpan(8, 4), (uint)totalKeys);
        mergedKeys.CopyTo(output.AsSpan(12));
        mergedTuples.CopyTo(output.AsSpan(12 + mergedKeys.Length));
        return output;
    }

    public static void SaveWarmedKeys(
        GameInfo game,
        IEnumerable<string> newKeys,
        string runtimeStamp,
        IEnumerable<string>? dbs = null,
        byte[]? prebuiltWarmedSnapshot = null,
        bool mergeWithExistingWarmedSnapshot = false)
    {
        try
        {
            string gameDir = GameDir(game.Id);
            Directory.CreateDirectory(gameDir);

            var combined = LoadWarmedKeys(game.Id, runtimeStamp);
            foreach (var k in newKeys)
            {
                if (!string.IsNullOrWhiteSpace(k))
                    combined.Add(k.Trim());
            }

            string path = WarmedKeysPath(game.Id);
            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            using (var sw = new StreamWriter(tmp, append: false))
            {
                sw.WriteLine($"#STAMP:{runtimeStamp}");
                foreach (var k in combined.OrderBy(x => x, StringComparer.Ordinal))
                    sw.WriteLine(k);
            }
            File.Move(tmp, path, overwrite: true);
            _warmedFileCache.TryRemove(Path.GetFullPath(path), out _);
            InvalidateCountsCache(game.Id);

            byte[]? warmedBytes = prebuiltWarmedSnapshot;
            if (warmedBytes == null && dbs != null)
            {
                warmedBytes = BuildWarmedSnapshot(dbs);
            }

            if (warmedBytes != null && warmedBytes.Length > 0)
            {
                string warmedPath = Path.Combine(gameDir, Recordings.WarmedFile);
                if (mergeWithExistingWarmedSnapshot && File.Exists(warmedPath))
                {
                    try
                    {
                        warmedBytes = MergeWarmedSnapshots(File.ReadAllBytes(warmedPath), warmedBytes);
                    }
                    catch { }
                }

                string wTmp = $"{warmedPath}.{Guid.NewGuid():N}.tmp";
                File.WriteAllBytes(wTmp, warmedBytes);
                File.Move(wTmp, warmedPath, overwrite: true);
                if (ExeDir(game) is { } dir)
                    SyncWarmedFileToExeDir(game, dir);
            }
        }
        catch { }
    }

    public static void ClearWarmedKeys(string gameId, GameInfo? game = null)
    {
        try
        {
            string keysPath = WarmedKeysPath(gameId);
            if (File.Exists(keysPath)) File.Delete(keysPath);
            _warmedFileCache.TryRemove(Path.GetFullPath(keysPath), out _);
            InvalidateCountsCache(gameId);
            string warmedPath = Path.Combine(GameDir(gameId), Recordings.WarmedFile);
            if (File.Exists(warmedPath)) File.Delete(warmedPath);
            if (game != null && ExeDir(game) is { } dir)
            {
                string exeWarmed = Path.Combine(dir, Recordings.WarmedFile);
                if (File.Exists(exeWarmed)) File.Delete(exeWarmed);
            }
        }
        catch { }
    }

    /// <summary>Inspects the planned shaderless.db and shaderless_gen.db files, filters out pipeline items that are
    /// already present in <paramref name="warmedKeys"/>, preserves transitive template/state-object dependencies and NVAPI/layer
    /// metadata, and prunes unreferenced <c>'B'</c> blobs so incremental runs write compact databases.</summary>
    public static (HashSet<string> AllItemKeys, int UnwarmedCount, int AlreadyWarmedCount) FilterDatabasesToUnwarmed(
        string recDbPath,
        string genDbPath,
        IReadOnlySet<string> warmedKeys)
    {
        var allItemKeys = new HashSet<string>(StringComparer.Ordinal);
        var unwarmedItemKeys = new HashSet<string>(StringComparer.Ordinal);
        var recByKey = new Dictionary<string, PsoDb.Rec>(StringComparer.Ordinal);
        var unwarmedSeedRecs = new List<PsoDb.Rec>();

        foreach (var dbPath in new[] { recDbPath, genDbPath })
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) continue;
            foreach (var r in PsoDb.Read(dbPath))
            {
                if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A')
                {
                    string key = r.Key;
                    recByKey.TryAdd(key, r);
                    allItemKeys.Add(key);
                    if (warmedKeys.Count == 0 || !warmedKeys.Contains(key))
                    {
                        if (unwarmedItemKeys.Add(key))
                            unwarmedSeedRecs.Add(r);
                    }
                }
                else if (r.Tag is 'P' or '1' or '2')
                {
                    string key = r.Key;
                    allItemKeys.Add(key);
                    if (warmedKeys.Count == 0 || !warmedKeys.Contains(key))
                    {
                        if (unwarmedItemKeys.Add(key))
                            unwarmedSeedRecs.Add(r);
                    }
                }
            }
        }

        int alreadyWarmed = allItemKeys.Count - unwarmedItemKeys.Count;
        if (warmedKeys.Count == 0 || alreadyWarmed <= 0 || unwarmedItemKeys.Count == 0)
        {
            return (allItemKeys, unwarmedItemKeys.Count, Math.Max(0, alreadyWarmed));
        }

        // Resolve full transitive template and base-state-object dependencies across both DBs
        var requiredTemplateKeys = new HashSet<string>(StringComparer.Ordinal);
        var depQueue = new Queue<string>();

        void EnqueueDependency(string depKey)
        {
            if (!unwarmedItemKeys.Contains(depKey) && recByKey.ContainsKey(depKey) && requiredTemplateKeys.Add(depKey))
                depQueue.Enqueue(depKey);
        }

        void EnqueueRecordDependencies(PsoDb.Rec r)
        {
            if ((r.Tag == 'P' || r.Tag == 'A') && r.Payload.Length >= 20)
                EnqueueDependency(PsoDb.Hex(r.Payload.AsSpan(0, 20)));
            if (PsoDb.IsStateObject(r.Tag))
            {
                try
                {
                    foreach (var dep in PsoDb.ParseStateObject(r).Depends)
                        EnqueueDependency(dep);
                }
                catch { }
            }
        }

        foreach (var seed in unwarmedSeedRecs)
            EnqueueRecordDependencies(seed);

        while (depQueue.Count > 0)
        {
            string depKey = depQueue.Dequeue();
            if (recByKey.TryGetValue(depKey, out var depRec))
                EnqueueRecordDependencies(depRec);
        }

        var keptExecutableKeys = new HashSet<string>(unwarmedItemKeys, StringComparer.Ordinal);
        keptExecutableKeys.UnionWith(requiredTemplateKeys);

        var perDbBlobs = new Dictionary<string, List<PsoDb.Rec>>(StringComparer.Ordinal);
        var perDbNonBlobs = new Dictionary<string, List<PsoDb.Rec>>(StringComparer.Ordinal);
        var allKeptNonBlobs = new List<PsoDb.Rec>();
        int keptExecutable = 0;

        foreach (var dbPath in new[] { recDbPath, genDbPath })
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) continue;
            var blobs = new List<PsoDb.Rec>();
            var nonBlobs = new List<PsoDb.Rec>();

            foreach (var r in PsoDb.Read(dbPath))
            {
                if (r.Tag == 'B')
                {
                    blobs.Add(r);
                }
                else if (r.Tag == 'W')
                {
                    nonBlobs.Add(r);
                }
                else if (r.Tag == 'N')
                {
                    try
                    {
                        if (keptExecutableKeys.Contains(PsoDb.NvState.Parse(r).Target))
                            nonBlobs.Add(r);
                    }
                    catch { }
                }
                else if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A')
                {
                    if (keptExecutableKeys.Contains(r.Key))
                    {
                        nonBlobs.Add(r);
                        allKeptNonBlobs.Add(r);
                        keptExecutable++;
                    }
                }
                else if (r.Tag is 'P' or '1' or '2')
                {
                    if (unwarmedItemKeys.Contains(r.Key))
                    {
                        nonBlobs.Add(r);
                        allKeptNonBlobs.Add(r);
                        keptExecutable++;
                    }
                }
            }

            perDbBlobs[dbPath] = blobs;
            perDbNonBlobs[dbPath] = nonBlobs;
        }

        HashSet<string>? neededBlobHashes = null;
        try
        {
            neededBlobHashes = Rehydrate.References(allKeptNonBlobs);
        }
        catch
        {
            neededBlobHashes = null; // Fallback: retain all blobs if parsing fails unexpectedly
        }

        foreach (var dbPath in new[] { recDbPath, genDbPath })
        {
            if (!perDbNonBlobs.TryGetValue(dbPath, out var nonBlobs))
                continue;
            var blobs = perDbBlobs[dbPath];

            var keptBlobs = neededBlobHashes == null
                ? blobs
                : blobs.Where(b => b.Payload.Length >= 20 && neededBlobHashes.Contains(PsoDb.Hex(b.Payload.AsSpan(0, 20)))).ToList();

            string tmp = $"{dbPath}.{Guid.NewGuid():N}.tmp";
            using (var fs = File.Create(tmp))
            using (var bs = new BufferedStream(fs, 1 << 20))
            {
                foreach (var rec in keptBlobs)
                    PsoDb.Write(bs, rec.Tag, rec.Payload);
                foreach (var rec in nonBlobs)
                    PsoDb.Write(bs, rec.Tag, rec.Payload);
            }
            File.Move(tmp, dbPath, overwrite: true);
        }

        return (allItemKeys, keptExecutable, alreadyWarmed);
    }

    public sealed record RecordedMaterializeResult(
        HashSet<string> AllRecordedKeys,
        HashSet<string> NewlyPlannedKeys,
        int UnwarmedTargetCount,
        int KeptExecutableCount,
        int AlreadyWarmedCount,
        byte[]? DeltaWarmedSnapshot);

    /// <summary>
    /// Extracts recorded pipelines directly from <paramref name="storePath"/> (<c>games/&lt;id&gt;/recording.db</c>) into
    /// <paramref name="outDbPath"/> (<c>work/&lt;id&gt;/shaderless.db</c>) without running <c>reader.Index</c> or <c>planner.Build</c>.
    /// Used by both <b>Recorded Only</b> mode and the <b>Fast Incremental Recorded-Delta</b> re-compile path.
    /// Rehydrates missing shader blobs from middleware DLLs or game archives on demand only when <c>recording.db</c> lacks them,
    /// and prunes unreferenced <c>'B'</c> blobs so incremental runs stay small and fast.
    /// </summary>
    public static RecordedMaterializeResult MaterializeRecordedPipelines(
        string storePath,
        Shaderless.Engine.Game scsGame,
        string outDbPath,
        IReadOnlySet<string> existingWarmedKeys,
        Func<(Shaderless.Engine.IEngineReader Reader, Shaderless.Engine.EngineInfo? Engine)>? lazyEngineReader = null,
        CancellationToken ct = default)
    {
        var emptySet = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(storePath) || !File.Exists(storePath) || new FileInfo(storePath).Length == 0)
            return new RecordedMaterializeResult(emptySet, emptySet, 0, 0, 0, null);

        List<PsoDb.Rec> records;
        using (Recordings.Lock(storePath))
        {
            records = PsoDb.Read(storePath).ToList();
        }

        if (records.Count == 0)
            return new RecordedMaterializeResult(emptySet, emptySet, 0, 0, 0, null);

        var blobsByHash = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var recByKey = new Dictionary<string, PsoDb.Rec>(StringComparer.Ordinal);
        var orderedExecs = new List<PsoDb.Rec>();
        var layerRecs = new List<PsoDb.Rec>();
        var nvRecs = new List<PsoDb.Rec>();
        var allRecordedKeys = new HashSet<string>(StringComparer.Ordinal);
        var unwarmedTargetKeys = new HashSet<string>(StringComparer.Ordinal);
        var unwarmedSeeds = new List<PsoDb.Rec>();

        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            if (r.Tag == 'B')
            {
                if (r.Payload.Length >= 20)
                {
                    string h = PsoDb.Hex(r.Payload.AsSpan(0, 20));
                    blobsByHash.TryAdd(h, r.Payload);
                }
            }
            else if (r.Tag == 'W')
            {
                layerRecs.Add(r);
            }
            else if (r.Tag == 'N')
            {
                nvRecs.Add(r);
            }
            else if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A' or 'P' or '1' or '2')
            {
                string key = r.Key;
                if (allRecordedKeys.Add(key))
                {
                    orderedExecs.Add(r);
                    if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A')
                        recByKey[key] = r;

                    if (existingWarmedKeys.Count == 0 || !existingWarmedKeys.Contains(key))
                    {
                        unwarmedTargetKeys.Add(key);
                        unwarmedSeeds.Add(r);
                    }
                }
            }
        }

        int alreadyWarmedRecorded = allRecordedKeys.Count - unwarmedTargetKeys.Count;
        if (allRecordedKeys.Count == 0 || unwarmedTargetKeys.Count == 0)
        {
            return new RecordedMaterializeResult(
                allRecordedKeys,
                new HashSet<string>(StringComparer.Ordinal),
                0,
                0,
                Math.Max(0, alreadyWarmedRecorded),
                null);
        }

        // Resolve transitive template / base state-object dependencies for unwarmed target records
        var depKeys = new HashSet<string>(StringComparer.Ordinal);
        var depQueue = new Queue<string>();

        void EnqueueDep(string k)
        {
            if (!unwarmedTargetKeys.Contains(k) && recByKey.ContainsKey(k) && depKeys.Add(k))
                depQueue.Enqueue(k);
        }

        void EnqueueRecDeps(PsoDb.Rec r)
        {
            if ((r.Tag == 'P' || r.Tag == 'A') && r.Payload.Length >= 20)
                EnqueueDep(PsoDb.Hex(r.Payload.AsSpan(0, 20)));
            if (PsoDb.IsStateObject(r.Tag))
            {
                try
                {
                    foreach (var d in PsoDb.ParseStateObject(r).Depends)
                        EnqueueDep(d);
                }
                catch { }
            }
        }

        foreach (var seed in unwarmedSeeds)
            EnqueueRecDeps(seed);

        while (depQueue.Count > 0)
        {
            string dk = depQueue.Dequeue();
            if (recByKey.TryGetValue(dk, out var dr))
                EnqueueRecDeps(dr);
        }

        var candidateSelected = orderedExecs
            .Where(r => unwarmedTargetKeys.Contains(r.Key) || depKeys.Contains(r.Key))
            .ToList();

        var neededBlobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in candidateSelected)
        {
            try
            {
                foreach (var h in Rehydrate.References([r]))
                    neededBlobs.Add(h);
            }
            catch { }
        }
        neededBlobs.Remove(PsoDb.Zero);

        var missingRefs = neededBlobs.Where(h => !blobsByHash.ContainsKey(h)).ToHashSet(StringComparer.Ordinal);
        var extraBlobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        if (missingRefs.Count > 0)
        {
            // 1. Check middleware DLLs beside the game executable (e.g. DLSS / Streamline / FSR)
            try
            {
                foreach (var (sha1, bytes) in Middleware.Blobs(scsGame, missingRefs))
                {
                    ct.ThrowIfCancellationRequested();
                    if (missingRefs.Remove(sha1))
                        extraBlobs[sha1] = bytes;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            // 2. If any shipped shader hashes are still missing, read ONLY those hashes from game archives (no full Index!)
            if (missingRefs.Count > 0 && lazyEngineReader != null)
            {
                try
                {
                    var (reader, engineInfo) = lazyEngineReader();
                    if (engineInfo != null && engineInfo.Unsupported == null)
                    {
                        reader.ReadShaders(
                            scsGame,
                            engineInfo,
                            missingRefs,
                            (sha1, bytes) =>
                            {
                                lock (extraBlobs)
                                {
                                    extraBlobs[sha1] = bytes;
                                }
                            },
                            ct);
                        foreach (var found in extraBlobs.Keys)
                            missingRefs.Remove(found);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        var availableBlobs = new HashSet<string>(blobsByHash.Keys, StringComparer.Ordinal);
        availableBlobs.UnionWith(extraBlobs.Keys);

        var keptKeys = new HashSet<string>(StringComparer.Ordinal);
        var newlyPlannedKeys = new HashSet<string>(StringComparer.Ordinal);
        var keptExecRecords = new List<PsoDb.Rec>();

        foreach (var r in candidateSelected)
        {
            try
            {
                if (!Rehydrate.References([r]).IsSubsetOf(availableBlobs))
                    continue;
                if (PsoDb.IsStateObject(r.Tag) && !PsoDb.ParseStateObject(r).Depends.All(keptKeys.Contains))
                    continue;

                keptExecRecords.Add(r);
                keptKeys.Add(r.Key);
                if (unwarmedTargetKeys.Contains(r.Key))
                    newlyPlannedKeys.Add(r.Key);
            }
            catch { }
        }

        if (newlyPlannedKeys.Count == 0)
        {
            return new RecordedMaterializeResult(
                allRecordedKeys,
                new HashSet<string>(StringComparer.Ordinal),
                0,
                0,
                Math.Max(0, alreadyWarmedRecorded),
                null);
        }

        HashSet<string> finalNeededBlobs;
        try
        {
            finalNeededBlobs = Rehydrate.References(keptExecRecords);
        }
        catch
        {
            finalNeededBlobs = neededBlobs;
        }

        var keptMetaRecords = new List<PsoDb.Rec>(layerRecs.Count + nvRecs.Count);
        keptMetaRecords.AddRange(layerRecs);
        foreach (var nRec in nvRecs)
        {
            try
            {
                if (keptKeys.Contains(PsoDb.NvState.Parse(nRec).Target))
                    keptMetaRecords.Add(nRec);
            }
            catch { }
        }

        string outDir = Path.GetDirectoryName(Path.GetFullPath(outDbPath))!;
        Directory.CreateDirectory(outDir);
        string tmp = $"{outDbPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var bs = new BufferedStream(fs, 1 << 20))
            {
                foreach (var blobHash in finalNeededBlobs)
                {
                    if (blobsByHash.TryGetValue(blobHash, out var payload))
                        PsoDb.Write(bs, 'B', payload);
                    else if (extraBlobs.TryGetValue(blobHash, out var rawBytes))
                        PsoDb.WriteBlob(bs, blobHash, rawBytes);
                }

                foreach (var rec in keptExecRecords)
                    PsoDb.Write(bs, rec.Tag, rec.Payload);

                foreach (var meta in keptMetaRecords)
                    PsoDb.Write(bs, meta.Tag, meta.Payload);
            }
            File.Move(tmp, outDbPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        byte[]? deltaSnapshot = BuildWarmedSnapshot([outDbPath]);
        return new RecordedMaterializeResult(
            allRecordedKeys,
            newlyPlannedKeys,
            newlyPlannedKeys.Count,
            keptExecRecords.Count,
            Math.Max(0, alreadyWarmedRecorded),
            deltaSnapshot);
    }

    /// <summary>Planner.HasDraws: a graphics PSO with a vertex shader, so a state-dependent cache's exact layouts are known.</summary>
    public static bool HasDraws(string db)
    {
        foreach (var r in PsoDb.Read(db))
            try { if (r.Tag is 'G' or 'S' && PsoDb.Parse(r).Stages.ContainsKey((int)Shaderless.Engine.Stage.Vertex)) return true; }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException) { }
        return false;
    }

    /// <summary>
    /// Materializes a standalone <c>recording.db</c> directly into <paramref name="outDbPath"/> (<c>shaderless.db</c>)
    /// when a game's static shader archives cannot be read (e.g. CryEngine, custom DX12 engines, or encrypted archives without a key).
    /// Preserves all recorded <c>'B'</c> bytecode/root-signature blobs, pulls any middleware shader blobs from DLLs beside the exe,
    /// and emits only self-contained, replayable pipeline records. Returns the number of replayable pipelines written.
    /// </summary>
    public static int MaterializeStandaloneRecording(
        string storePath,
        Shaderless.Engine.Game scsGame,
        string outDbPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(storePath) || !File.Exists(storePath) || new FileInfo(storePath).Length == 0)
            return 0;

        List<PsoDb.Rec> records;
        using (Recordings.Lock(storePath))
        {
            records = PsoDb.Read(storePath).ToList();
        }

        if (records.Count == 0)
            return 0;

        var availableBlobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.Tag == 'B' && r.Payload.Length >= 20)
                availableBlobs.Add(PsoDb.Hex(r.Payload.AsSpan(0, 20)));
        }

        var missingRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.Tag is 'B' or 'L' or 'W' or 'N') continue;
            try
            {
                foreach (var h in Rehydrate.References([r]))
                {
                    if (!availableBlobs.Contains(h))
                        missingRefs.Add(h);
                }
            }
            catch { }
        }

        var extraBlobs = new List<(string Sha1, byte[] Bytes)>();
        if (missingRefs.Count > 0)
        {
            try
            {
                foreach (var (sha1, bytes) in Middleware.Blobs(scsGame, missingRefs))
                {
                    ct.ThrowIfCancellationRequested();
                    if (availableBlobs.Add(sha1))
                        extraBlobs.Add((sha1, bytes));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        var keptKeys = new HashSet<string>(StringComparer.Ordinal);
        var keptRecords = new List<PsoDb.Rec>();

        foreach (var r in records)
        {
            if (r.Tag is 'B' or 'L' or 'N') continue;
            if (r.Tag == 'W')
            {
                keptRecords.Add(r);
                continue;
            }

            if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A' or 'P' or '1' or '2')
            {
                try
                {
                    if (!Rehydrate.References([r]).IsSubsetOf(availableBlobs))
                        continue;
                    if (PsoDb.IsStateObject(r.Tag) && !PsoDb.ParseStateObject(r).Depends.All(keptKeys.Contains))
                        continue;
                    keptRecords.Add(r);
                    keptKeys.Add(r.Key);
                }
                catch { }
            }
        }

        if (keptKeys.Count == 0)
            return 0;

        foreach (var r in records)
        {
            if (r.Tag != 'N') continue;
            try
            {
                if (keptKeys.Contains(PsoDb.NvState.Parse(r).Target))
                    keptRecords.Add(r);
            }
            catch { }
        }

        string outDir = Path.GetDirectoryName(Path.GetFullPath(outDbPath))!;
        Directory.CreateDirectory(outDir);
        string tmp = $"{outDbPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var bs = new BufferedStream(fs, 1 << 20))
            {
                foreach (var r in records)
                {
                    if (r.Tag == 'B')
                        PsoDb.Write(bs, 'B', r.Payload);
                }
                foreach (var (sha1, bytes) in extraBlobs)
                {
                    PsoDb.WriteBlob(bs, sha1, bytes);
                }
                foreach (var r in keptRecords)
                {
                    PsoDb.Write(bs, r.Tag, r.Payload);
                }
            }
            File.Move(tmp, outDbPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        return keptKeys.Count;
    }

    public sealed record WarmerBatchSlice(string BatchDbPath, int SkipPrefixCount, int ExecutableItemCount);

    public static int CountExecutableItems(string recDbPath, string genDbPath)
    {
        var seenRecKeys = new HashSet<string>(StringComparer.Ordinal);
        int planAnd11Count = 0;

        foreach (var dbPath in new[] { recDbPath, genDbPath })
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath) || new FileInfo(dbPath).Length == 0)
                continue;

            foreach (var r in PsoDb.Read(dbPath))
            {
                if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A')
                {
                    seenRecKeys.Add(r.Key);
                }
                else if (r.Tag is 'P' or '1' or '2')
                {
                    planAnd11Count++;
                }
            }
        }

        return seenRecKeys.Count + planAnd11Count;
    }

    /// <summary>
    /// Splits <paramref name="recDbPath"/> and <paramref name="genDbPath"/> into self-contained batch databases of at most
    /// <paramref name="maxItemsPerBatch"/> executable pipelines each so large games (e.g. 100k+ PSOs) never exceed NVIDIA's
    /// single-file ~2 GiB writable bucket limit during warming. Any template/state-object dependencies from earlier batches
    /// are prepended before the batch's executable records and skipped via <see cref="WarmerBatchSlice.SkipPrefixCount"/>
    /// (<c>--start</c> in <c>shaderless_warm.exe</c>).
    /// </summary>
    public static IReadOnlyList<WarmerBatchSlice> SplitDatabasesIntoBatches(
        string recDbPath,
        string genDbPath,
        string batchRootDir,
        int maxItemsPerBatch = 40_000)
    {
        if (maxItemsPerBatch < 1)
            maxItemsPerBatch = 1;

        var blobsByHash = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var uniqueRecs = new List<PsoDb.Rec>();
        var recByKey = new Dictionary<string, PsoDb.Rec>(StringComparer.Ordinal);
        var planItems = new List<PsoDb.Rec>();
        var d3d11Items = new List<PsoDb.Rec>();

        foreach (var dbPath in new[] { recDbPath, genDbPath })
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath) || new FileInfo(dbPath).Length == 0)
                continue;

            foreach (var r in PsoDb.Read(dbPath))
            {
                if (r.Tag == 'B')
                {
                    if (r.Payload.Length >= 20)
                    {
                        string h = PsoDb.Hex(r.Payload.AsSpan(0, 20));
                        blobsByHash.TryAdd(h, r.Payload);
                    }
                }
                else if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A')
                {
                    string key = r.Key;
                    if (recByKey.TryAdd(key, r))
                        uniqueRecs.Add(r);
                }
                else if (r.Tag == 'P')
                {
                    planItems.Add(r);
                }
                else if (r.Tag is '1' or '2')
                {
                    d3d11Items.Add(r);
                }
            }
        }

        var allExecutable = new List<PsoDb.Rec>(uniqueRecs.Count + planItems.Count + d3d11Items.Count);
        allExecutable.AddRange(uniqueRecs);
        allExecutable.AddRange(planItems);
        allExecutable.AddRange(d3d11Items);

        if (allExecutable.Count == 0)
            return Array.Empty<WarmerBatchSlice>();

        if (Directory.Exists(batchRootDir))
        {
            try { Directory.Delete(batchRootDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(batchRootDir);

        var slices = new List<WarmerBatchSlice>();
        int batchIndex = 0;

        for (int offset = 0; offset < allExecutable.Count; offset += maxItemsPerBatch)
        {
            int count = Math.Min(maxItemsPerBatch, allExecutable.Count - offset);
            var chunkRecs = new List<PsoDb.Rec>();
            var chunkRecKeys = new HashSet<string>(StringComparer.Ordinal);
            var chunkPlan = new List<PsoDb.Rec>();
            var chunk11 = new List<PsoDb.Rec>();

            for (int i = 0; i < count; i++)
            {
                var item = allExecutable[offset + i];
                if (item.Tag is 'G' or 'C' or 'S' or 'R' or 'A')
                {
                    chunkRecs.Add(item);
                    chunkRecKeys.Add(item.Key);
                }
                else if (item.Tag == 'P')
                {
                    chunkPlan.Add(item);
                }
                else if (item.Tag is '1' or '2')
                {
                    chunk11.Add(item);
                }
            }

            // Collect transitive template / base-state-object dependencies not already in chunkRecKeys
            var depKeys = new HashSet<string>(StringComparer.Ordinal);
            var workQueue = new Queue<string>();

            void EnqueueDepIfExternal(string key)
            {
                if (!chunkRecKeys.Contains(key) && recByKey.ContainsKey(key) && depKeys.Add(key))
                    workQueue.Enqueue(key);
            }

            foreach (var p in chunkPlan)
            {
                if (p.Payload.Length >= 20)
                    EnqueueDepIfExternal(PsoDb.Hex(p.Payload.AsSpan(0, 20)));
            }

            foreach (var r in chunkRecs)
            {
                if (r.Tag == 'A' && r.Payload.Length >= 20)
                    EnqueueDepIfExternal(PsoDb.Hex(r.Payload.AsSpan(0, 20)));
                if (PsoDb.IsStateObject(r.Tag))
                {
                    try
                    {
                        foreach (var dep in PsoDb.ParseStateObject(r).Depends)
                            EnqueueDepIfExternal(dep);
                    }
                    catch { }
                }
            }

            while (workQueue.Count > 0)
            {
                string depKey = workQueue.Dequeue();
                if (!recByKey.TryGetValue(depKey, out var depRec)) continue;
                if (depRec.Tag == 'A' && depRec.Payload.Length >= 20)
                    EnqueueDepIfExternal(PsoDb.Hex(depRec.Payload.AsSpan(0, 20)));
                if (PsoDb.IsStateObject(depRec.Tag))
                {
                    try
                    {
                        foreach (var subDep in PsoDb.ParseStateObject(depRec).Depends)
                            EnqueueDepIfExternal(subDep);
                    }
                    catch { }
                }
            }

            var depTemplates = depKeys.Count > 0
                ? uniqueRecs.Where(r => depKeys.Contains(r.Key)).ToList()
                : new List<PsoDb.Rec>();

            var allBatchNonBlobRecords = new List<PsoDb.Rec>(depTemplates.Count + chunkRecs.Count + chunkPlan.Count + chunk11.Count);
            allBatchNonBlobRecords.AddRange(depTemplates);
            allBatchNonBlobRecords.AddRange(chunkRecs);
            allBatchNonBlobRecords.AddRange(chunkPlan);
            allBatchNonBlobRecords.AddRange(chunk11);

            HashSet<string> neededBlobs;
            try
            {
                neededBlobs = Rehydrate.References(allBatchNonBlobRecords);
            }
            catch
            {
                neededBlobs = blobsByHash.Keys.ToHashSet(StringComparer.Ordinal);
            }

            string batchDir = Path.Combine(batchRootDir, $"batch_{batchIndex:D3}");
            Directory.CreateDirectory(batchDir);
            string batchDbPath = Path.Combine(batchDir, "shaderless.db");

            using (var fs = File.Create(batchDbPath))
            using (var bs = new BufferedStream(fs, 1 << 20))
            {
                foreach (var blobHash in neededBlobs)
                {
                    if (blobsByHash.TryGetValue(blobHash, out var blobPayload))
                        PsoDb.Write(bs, 'B', blobPayload);
                }

                foreach (var rec in allBatchNonBlobRecords)
                {
                    PsoDb.Write(bs, rec.Tag, rec.Payload);
                }
            }

            slices.Add(new WarmerBatchSlice(
                BatchDbPath: batchDbPath,
                SkipPrefixCount: depTemplates.Count,
                ExecutableItemCount: chunkRecs.Count + chunkPlan.Count + chunk11.Count));
            batchIndex++;
        }

        return slices;
    }
}
