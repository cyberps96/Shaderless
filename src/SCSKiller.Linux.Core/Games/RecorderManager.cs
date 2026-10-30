using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Games;

/// <summary>Record mode on Linux, as SCSKiller.Core's ScsKiller.Install / ImportRecording / WriteKeys do on Windows:
/// the d3d12.dll recorder (Native/d3d12.dll, built from Native/recorder.cpp with mingw-w64) goes next to the game's exe,
/// where Wine loads it before the prefix's vkd3d-proton d3d12.dll. It writes shaderless.db there (the inbox); Shaderless
/// imports it into games/&lt;id&gt;/recording.db and writes shaderless.keys back so nothing is recorded twice.</summary>
public static class RecorderManager
{
    private static readonly AsyncLocal<string?> _testDataDirOverride = new();

    internal static string? TestDataDirOverride
    {
        get => _testDataDirOverride.Value;
        set => _testDataDirOverride.Value = value;
    }

    public static string DataDir =>
        !string.IsNullOrEmpty(TestDataDirOverride)
            ? TestDataDirOverride
            : SCSKiller.Linux.Core.AppPaths.DataDir;
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
            if ((File.Exists(legacyArmed) || File.Exists(legacyIni)) && File.Exists(dll) && ScsKiller.IsOurProxy(dll))
            {
                if (ProxySource() is { } src)
                {
                    File.Copy(src, dll, overwrite: true);
                }
                if (File.Exists(legacyIni))
                {
                    File.WriteAllText(Path.Combine(dir, "shaderless.ini"), IniText);
                    File.Delete(legacyIni);
                }
                if (File.Exists(legacyArmed))
                {
                    File.WriteAllText(Path.Combine(dir, "shaderless.armed"), "[shaderless]\r\narmed=1\r\n");
                    File.Delete(legacyArmed);
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
            if (File.Exists(p) && ScsKiller.IsOurProxy(p)) return p;
        return null;
    }

    /// <summary>The folder of the exe that really runs (an Unreal game's Shipping exe or resolved main game binary, not its launcher stub).</summary>
    public static string? ExeDir(GameInfo game)
    {
        var exe = GameEngineDetector.ResolveRealGameExecutable(game.InstallPath, game.ExecutablePath);
        return string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
    }

    public static bool IsInstalled(GameInfo game) => ExeDir(game) is { } dir && ScsKiller.IsOurProxy(Path.Combine(dir, "d3d12.dll"));

    /// <summary>Pipelines recorded so far: Shaderless's copy plus what the inbox holds that the copy doesn't. Each file's keys
    /// are read again only when its size or write time changed (a scan runs this for every game).</summary>
    public static int RecordedPsos(GameInfo game)
    {
        var keys = new HashSet<string>();
        try
        {
            var exeDir = ExeDir(game);
            MigrateLegacyFiles(exeDir);
            foreach (var path in new[] { RecordingPath(game.Id), exeDir is { } d ? Path.Combine(d, "shaderless.db") : null })
                if (path != null && new FileInfo(path) is { Exists: true, Length: > 0 } f)
                    keys.UnionWith(KeysOf(f));
        }
        catch { }
        return keys.Count;
    }

    /// <summary>Pipelines recorded in recording.db / shaderless.db that have not yet been warmed into warmed-keys.txt.</summary>
    public static int UnwarmedRecordedPsos(GameInfo game)
    {
        try
        {
            var warmed = LoadWarmedKeys(game.Id);
            if (warmed.Count == 0) return 0;
            var exeDir = ExeDir(game);
            MigrateLegacyFiles(exeDir);
            var recorded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in new[] { RecordingPath(game.Id), exeDir is { } d ? Path.Combine(d, "shaderless.db") : null })
                if (path != null && new FileInfo(path) is { Exists: true, Length: > 0 } f)
                    recorded.UnionWith(KeysOf(f));
            recorded.ExceptWith(warmed);
            return recorded.Count;
        }
        catch
        {
            return 0;
        }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime Written, HashSet<string> Keys)> _keys = new();

    static HashSet<string> KeysOf(FileInfo f)
    {
        if (_keys.TryGetValue(f.FullName, out var c) && c.Length == f.Length && c.Written == f.LastWriteTimeUtc) return c.Keys;
        var keys = PsoDb.Read(f.FullName).Where(r => r.Tag is not ('B' or 'N' or 'L' or 'W')).Select(r => r.Key).ToHashSet();
        _keys[f.FullName] = (f.Length, f.LastWriteTimeUtc, keys);
        return keys;
    }

    static SCSKiller.Core.Game ToCore(GameInfo g, string exeDir) => new(g.Id, g.Name,
        g.Source switch { GameSource.Steam => SCSKiller.Core.Store.Steam, GameSource.Heroic => SCSKiller.Core.Store.Epic, _ => SCSKiller.Core.Store.Manual },
        g.InstallPath, File.Exists(g.ExecutablePath) && Path.GetDirectoryName(g.ExecutablePath) == exeDir ? g.ExecutablePath
            : Directory.EnumerateFiles(exeDir, "*.exe").FirstOrDefault() ?? g.ExecutablePath);

    public const string IniText = "[shaderless]\r\n; written by Shaderless (unofficial Linux port of SCSKiller): the recorder records every pipeline the game creates into shaderless.db\r\nmode=record\r\n";

    public static string WarmedKeysPath(string gameId) => Path.Combine(GameDir(gameId), "warmed-keys.txt");

    /// <summary>The anti-cheat found in the game's folder (None if there is none, or the game's exe wasn't found).</summary>
    public static SCSKiller.Core.AntiCheat DetectAntiCheat(GameInfo game)
    {
        try { return ExeDir(game) is { } dir ? SCSKiller.Core.Games.GameFiles.DetectAntiCheat(ToCore(game, dir)) : SCSKiller.Core.AntiCheat.None; }
        catch { return SCSKiller.Core.AntiCheat.None; }
    }

    /// <summary>Attempts to automatically install the recorder on an eligible game (used when AutoRecordAllGames is enabled).
    /// Always skips games with anti-cheat or existing third-party d3d12.dll files.</summary>
    public static bool TryAutoInstall(GameInfo game)
    {
        try
        {
            if (IsInstalled(game)) return true;
            if (DetectAntiCheat(game) != SCSKiller.Core.AntiCheat.None) return false;
            var dir = ExeDir(game);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var dll = Path.Combine(dir, "d3d12.dll");
            if (File.Exists(dll) && !ScsKiller.IsOurProxy(dll)) return false;
            Install(game);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Installs and arms the recorder. Refuses next to anti-cheat (a d3d12.dll there could get the account banned)
    /// and over another mod's d3d12.dll.</summary>
    public static void Install(GameInfo game)
    {
        var dir = ExeDir(game) ?? throw new InvalidOperationException($"{game.Name}: the game's exe wasn't found");
        var src = ProxySource() ?? throw new FileNotFoundException("the recorder (Native/d3d12.dll) was not found next to Shaderless");
        var ac = SCSKiller.Core.Games.GameFiles.DetectAntiCheat(ToCore(game, dir));
        if (ac != SCSKiller.Core.AntiCheat.None)
            throw new InvalidOperationException($"{game.Name} uses {ac}: a d3d12.dll in its folder could get the account banned. Not installing.");
        var dll = Path.Combine(dir, "d3d12.dll");
        if (File.Exists(dll) && !ScsKiller.IsOurProxy(dll))
            throw new InvalidOperationException($"{dll} already exists and is not Shaderless's (another mod or wrapper). Not replacing it.");
        MigrateLegacyFiles(dir);
        MigrateLegacyFiles(GameDir(game.Id));
        File.Copy(src, dll, overwrite: true);
        File.WriteAllText(Path.Combine(dir, "shaderless.ini"), IniText);
        File.WriteAllText(Path.Combine(dir, "shaderless.armed"), "[shaderless]\r\narmed=1\r\n");
        WriteKeys(game, dir);
        SyncWarmedFileToExeDir(game, dir);
    }

    /// <summary>Imports what was recorded, then removes the recorder and its files.</summary>
    public static void Uninstall(GameInfo game)
    {
        var dir = ExeDir(game) ?? throw new InvalidOperationException($"{game.Name}: the game's exe wasn't found");
        Import(game);
        var dll = Path.Combine(dir, "d3d12.dll");
        if (ScsKiller.IsOurProxy(dll)) File.Delete(dll);
        foreach (var f in new[] { "shaderless.ini", "shaderless.armed", "shaderless.log", "shaderless_creates.csv", Recordings.KeysFile, Recordings.WarmedFile, "scskiller.ini", "scskiller.armed", "scskiller.log", "scskiller_creates.csv", "scskiller.keys", "scskiller.warmed" })
            try { File.Delete(Path.Combine(dir, f)); } catch { }
        foreach (var dbName in new[] { "shaderless.db", "scskiller.db" })
        {
            var inbox = Path.Combine(dir, dbName);
            if (!File.Exists(inbox) || new FileInfo(inbox).Length == 0) try { File.Delete(inbox); } catch { }
        }
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

    /// <summary>shaderless.keys next to the recorder: what the copy already holds, so the next session records only what's new.</summary>
    static void WriteKeys(GameInfo game, string dir)
    {
        if (!ScsKiller.IsOurProxy(Path.Combine(dir, "d3d12.dll"))) return;
        var store = RecordingPath(game.Id);
        if (!File.Exists(store)) return;
        using (Recordings.Lock(store)) Recordings.WriteKeys(store, null, Path.Combine(dir, Recordings.KeysFile));
    }

    static void SyncWarmedFileToExeDir(GameInfo game, string dir)
    {
        try
        {
            if (!ScsKiller.IsOurProxy(Path.Combine(dir, "d3d12.dll"))) return;
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

            if (!string.IsNullOrEmpty(savedProton) &&
                !string.IsNullOrEmpty(currentProtonStamp) &&
                !string.Equals(savedProton, "unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentProtonStamp, "unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(savedProton, currentProtonStamp, StringComparison.Ordinal))
            {
                return "Proton updated";
            }
        }
        catch { }

        return null;
    }

    public static HashSet<string> LoadWarmedKeys(string gameId, string? expectedRuntimeStamp = null, string? previousWorkDir = null)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        string path = WarmedKeysPath(gameId);
        if (!File.Exists(path))
        {
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
                        foreach (var r in SCSKiller.Core.Planning.Planner.PlanBody(prevPlanBin))
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

        try
        {
            using var sr = new StreamReader(path);
            string? firstLine = sr.ReadLine();
            if (firstLine == null) return result;

            if (firstLine.StartsWith("#STAMP:", StringComparison.Ordinal))
            {
                string savedStamp = firstLine["#STAMP:".Length..].Trim();
                if (!string.IsNullOrEmpty(expectedRuntimeStamp) &&
                    !string.Equals(savedStamp, expectedRuntimeStamp, StringComparison.Ordinal))
                {
                    return result; // Driver or Proton runtime changed -> full rebuild required
                }
            }
            else if (firstLine.Length == 40)
            {
                result.Add(firstLine.Trim());
            }

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 40)
                    result.Add(trimmed);
            }
        }
        catch { }
        return result;
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

    public static void SaveWarmedKeys(
        GameInfo game,
        IEnumerable<string> newKeys,
        string runtimeStamp,
        IEnumerable<string>? dbs = null,
        byte[]? prebuiltWarmedSnapshot = null)
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

            byte[]? warmedBytes = prebuiltWarmedSnapshot;
            if (warmedBytes == null && dbs != null)
            {
                warmedBytes = BuildWarmedSnapshot(dbs);
            }

            if (warmedBytes != null && warmedBytes.Length > 0)
            {
                string warmedPath = Path.Combine(gameDir, Recordings.WarmedFile);
                File.WriteAllBytes(warmedPath, warmedBytes);
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

    /// <summary>Inspects the planned shaderless.db and shaderless_gen.db files and filters out pipeline items that are
    /// already present in <paramref name="warmedKeys"/> so an incremental re-compile only compiles brand-new pipelines.</summary>
    public static (HashSet<string> AllItemKeys, int UnwarmedCount, int AlreadyWarmedCount) FilterDatabasesToUnwarmed(
        string recDbPath,
        string genDbPath,
        IReadOnlySet<string> warmedKeys)
    {
        var allItemKeys = new HashSet<string>(StringComparer.Ordinal);
        var unwarmedItemKeys = new HashSet<string>(StringComparer.Ordinal);
        var requiredTemplateKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dbPath in new[] { recDbPath, genDbPath })
        {
            if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) continue;
            foreach (var r in PsoDb.Read(dbPath))
            {
                if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A' or 'P' or '1' or '2')
                {
                    string key = r.Key;
                    allItemKeys.Add(key);
                    if (warmedKeys.Count == 0 || !warmedKeys.Contains(key))
                    {
                        unwarmedItemKeys.Add(key);
                        if ((r.Tag == 'P' || r.Tag == 'A') && r.Payload.Length >= 20)
                        {
                            requiredTemplateKeys.Add(Convert.ToHexStringLower(r.Payload.AsSpan(0, 20)));
                        }
                    }
                }
            }
        }

        int alreadyWarmed = allItemKeys.Count - unwarmedItemKeys.Count;
        if (warmedKeys.Count == 0 || alreadyWarmed <= 0 || unwarmedItemKeys.Count == 0)
        {
            return (allItemKeys, unwarmedItemKeys.Count, Math.Max(0, alreadyWarmed));
        }

        // Rewrite recDbPath and genDbPath keeping only blobs ('B'), required templates, and unwarmed items
        int keptExecutable = 0;
        foreach (var dbPath in new[] { recDbPath, genDbPath })
        {
            if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) continue;
            var recordsToKeep = new List<PsoDb.Rec>();
            foreach (var r in PsoDb.Read(dbPath))
            {
                if (r.Tag == 'B')
                {
                    recordsToKeep.Add(r);
                }
                else if (r.Tag is 'G' or 'C' or 'S' or 'R' or 'A')
                {
                    string key = r.Key;
                    if (unwarmedItemKeys.Contains(key) || requiredTemplateKeys.Contains(key))
                    {
                        recordsToKeep.Add(r);
                        keptExecutable++;
                    }
                }
                else if (r.Tag is 'P' or '1' or '2')
                {
                    string key = r.Key;
                    if (unwarmedItemKeys.Contains(key))
                    {
                        recordsToKeep.Add(r);
                        keptExecutable++;
                    }
                }
            }

            string tmp = $"{dbPath}.{Guid.NewGuid():N}.tmp";
            using (var fs = File.Create(tmp))
            using (var bs = new BufferedStream(fs, 1 << 20))
            {
                foreach (var rec in recordsToKeep)
                    PsoDb.Write(bs, rec.Tag, rec.Payload);
            }
            File.Move(tmp, dbPath, overwrite: true);
        }

        return (allItemKeys, keptExecutable, alreadyWarmed);
    }

    /// <summary>Planner.HasDraws: a graphics PSO with a vertex shader, so a state-dependent cache's exact layouts are known.</summary>
    public static bool HasDraws(string db)
    {
        foreach (var r in PsoDb.Read(db))
            try { if (r.Tag is 'G' or 'S' && PsoDb.Parse(r).Stages.ContainsKey((int)SCSKiller.Core.Stage.Vertex)) return true; }
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
        SCSKiller.Core.Game scsGame,
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
