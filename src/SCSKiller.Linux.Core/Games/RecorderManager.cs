using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Games;

/// <summary>Record mode on Linux, as SCSKiller.Core's ScsKiller.Install / ImportRecording / WriteKeys do on Windows:
/// the d3d12.dll recorder (Native/d3d12.dll, built from Native/recorder.cpp with mingw-w64) goes next to the game's exe,
/// where Wine loads it before the prefix's vkd3d-proton d3d12.dll. It writes shaderless.db there (the inbox); Shaderless
/// imports it into games/&lt;id&gt;/recording.db and writes shaderless.keys back so nothing is recorded twice.</summary>
public static class RecorderManager
{
    public static string DataDir => SCSKiller.Linux.Core.AppPaths.DataDir;
    public static string GameDir(string gameId) => Path.Combine(DataDir, "games", Sanitize(gameId));
    public static string RecordingPath(string gameId) => Path.Combine(GameDir(gameId), "recording.db");

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

    /// <summary>The folder of the exe that really runs (an Unreal game's Shipping exe, not its launcher stub).</summary>
    public static string? ExeDir(GameInfo game)
    {
        var exe = game.ExecutablePath;
        if (string.IsNullOrEmpty(exe)) exe = GameEngineDetector.FindExecutable(game.InstallPath) ?? "";
        if (string.IsNullOrEmpty(exe)) return null;
        try
        {
            var resolved = SCSKiller.Core.Games.GameFiles.GameExe(game.InstallPath, exe);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) exe = resolved;
        }
        catch { }
        return Path.GetDirectoryName(exe);
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

    /// <summary>Merges the game folder's shaderless.db into Shaderless's compact copy and empties it (Recordings.Rotate:
    /// only while the game doesn't hold it). Returns the pipelines added.</summary>
    public static int Import(GameInfo game)
    {
        if (ExeDir(game) is not { } dir) return 0;
        MigrateLegacyFiles(dir);
        var inbox = new FileInfo(Path.Combine(dir, "shaderless.db"));
        if (!inbox.Exists || inbox.Length == 0) return 0;
        var store = RecordingPath(game.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        int added;
        using (Recordings.Lock(store))
        {
            added = Recordings.Merge(store, inbox.FullName, null).Count;
            Recordings.Rotate(inbox.FullName, inbox.Length);
        }
        WriteKeys(game, dir);
        return added;
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
}
