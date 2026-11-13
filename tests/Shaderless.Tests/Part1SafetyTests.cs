using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shaderless.Core.Drivers;
using Shaderless.Core.Models;
using Shaderless.Core.Pipelines;

namespace Shaderless.Tests;

[TestClass]
public class Part1SafetyTests
{
    private string? _defaultTestDataDir;

    [TestInitialize]
    public void InitTestDataDir()
    {
        _defaultTestDataDir = Path.Combine(Path.GetTempPath(), "shaderless-test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_defaultTestDataDir);
        Shaderless.Core.AppPaths.TestDataDirOverride = _defaultTestDataDir;
    }

    [TestCleanup]
    public void CleanupTestDataDir()
    {
        Shaderless.Core.AppPaths.TestDataDirOverride = null;
        if (!string.IsNullOrEmpty(_defaultTestDataDir) && Directory.Exists(_defaultTestDataDir))
        {
            try { Directory.Delete(_defaultTestDataDir, true); } catch { }
        }
        _defaultTestDataDir = null;
    }

    static GameInfo MakeGame(string id, GameSource source, string installPath, string exePath, string? prefixPath = null) =>
        new(id, "Test Game", source, installPath, exePath, prefixPath, null, GameEngine.UnrealEngine5, CacheState.NeedsCompilation, 1000, 0, null);

    [TestMethod]
    public void FinalizeDriverCaches_Merges_Into_Existing_Game_Folder_Vkd3d_Cache()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-p1-" + Guid.NewGuid().ToString("N"));
        string targetCache = Path.Combine(root, "shadercache");
        string vkd3dCache = Path.Combine(targetCache, "VKD3D_shader_cache");
        string glCache = Path.Combine(targetCache, "nvidiav1", "GLCache");
        string gameExeDir = Path.Combine(root, "game", "Binaries", "Win64");
        Directory.CreateDirectory(vkd3dCache);
        Directory.CreateDirectory(glCache);
        Directory.CreateDirectory(gameExeDir);

        try
        {
            byte[] header = Enumerable.Range(1, 48).Select(i => (byte)i).ToArray();
            byte[] existingPayload = new byte[] { 10, 20, 30, 40 };
            byte[] newPayload = new byte[] { 50, 60, 70, 80, 90, 100 };

            string gameCacheFile = Path.Combine(gameExeDir, "vkd3d-proton.cache");
            File.WriteAllBytes(gameCacheFile, header.Concat(existingPayload).ToArray());

            string compiledCacheFile = Path.Combine(vkd3dCache, "vkd3d-proton.Game.exe.cache");
            File.WriteAllBytes(compiledCacheFile, header.Concat(newPayload).ToArray());

            ProtonRunner.FinalizeDriverCaches(targetCache, vkd3dCache, glCache, gameExeDir);

            byte[] merged = File.ReadAllBytes(gameCacheFile);
            // 48-byte header + existingPayload (4) + newPayload (6) = 58 bytes
            Assert.AreEqual(48 + existingPayload.Length + newPayload.Length, merged.Length);
            CollectionAssert.AreEqual(header.Concat(existingPayload).Concat(newPayload).ToArray(), merged);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void IsGameRunning_Detects_Running_Game_And_Ignores_Shaderless_Warmer()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-proc-" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(root, "game");
        string procDir = Path.Combine(root, "proc");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(procDir);

        try
        {
            string exePath = Path.Combine(gameDir, "ValorMortis-Win64-Shipping.exe");
            File.WriteAllText(exePath, "x");
            var game = MakeGame("2828710", GameSource.Steam, gameDir, exePath);

            // 1. No processes -> false
            Assert.IsFalse(ProtonRunner.IsGameRunning(game, procDir));

            // 2. Shaderless's own staged warmer in /shaderless/work/ -> ignored (false)
            Directory.CreateDirectory(Path.Combine(procDir, "1001"));
            File.WriteAllText(
                Path.Combine(procDir, "1001", "cmdline"),
                "Z:\\home\\user\\.local\\share\\shaderless\\work\\2828710\\ValorMortis-Win64-Shipping.exe\0--threads\016\0");
            Assert.IsFalse(ProtonRunner.IsGameRunning(game, procDir));

            // 3. Actual running game process -> true
            Directory.CreateDirectory(Path.Combine(procDir, "2002"));
            File.WriteAllText(
                Path.Combine(procDir, "2002", "cmdline"),
                "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Valor Mortis\\ValorMortis\\Binaries\\Win64\\ValorMortis-Win64-Shipping.exe\0-dx12\0");
            Assert.IsTrue(ProtonRunner.IsGameRunning(game, procDir));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void VerifyCacheInstalled_Checks_Steam_And_Prefix_Installations()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-verify-" + Guid.NewGuid().ToString("N"));
        string steamRoot = Path.Combine(root, "Steam");
        string prefix = Path.Combine(root, "HeroicPrefix");
        Directory.CreateDirectory(steamRoot);
        Directory.CreateDirectory(prefix);

        try
        {
            // 1. Steam game with missing cache folder -> false
            var steamGame = MakeGame("12345", GameSource.Steam, root, Path.Combine(root, "Game.exe"));
            var r1 = ProtonRunner.VerifyCacheInstalled(steamGame, steamRoot);
            Assert.IsFalse(r1.Verified);

            // 2. Steam game with non-empty cache file -> true
            string steamVkd3d = Path.Combine(steamRoot, "steamapps", "shadercache", "12345", "VKD3D_shader_cache");
            Directory.CreateDirectory(steamVkd3d);
            File.WriteAllBytes(Path.Combine(steamVkd3d, "vkd3d-proton.Game.exe.cache"), new byte[128]);
            var r2 = ProtonRunner.VerifyCacheInstalled(steamGame, steamRoot);
            Assert.IsTrue(r2.Verified);

            // 3. Heroic game: missing manifest -> false
            var heroicGame = MakeGame("heroic_1", GameSource.Heroic, root, Path.Combine(root, "Game.exe"), prefix);
            Assert.IsFalse(ProtonRunner.VerifyCacheInstalled(heroicGame, steamRoot).Verified);

            // 4. Heroic game: manifest points to existing non-empty file + user_settings.py exists -> true
            string ownDir = Path.Combine(prefix, "shadercache", "heroic_1");
            string installedVkd3d = Path.Combine(prefix, "shadercache", "VKD3D_shader_cache", "vkd3d-proton.Game.exe.cache");
            Directory.CreateDirectory(ownDir);
            Directory.CreateDirectory(Path.GetDirectoryName(installedVkd3d)!);
            File.WriteAllBytes(installedVkd3d, new byte[128]);
            File.WriteAllLines(Path.Combine(ownDir, "installed-files.txt"), new[] { installedVkd3d });
            File.WriteAllText(Path.Combine(prefix, "user_settings.py"), "PROTON_LOCAL_SHADER_CACHE");

            var r4 = ProtonRunner.VerifyCacheInstalled(heroicGame, steamRoot);
            Assert.IsTrue(r4.Verified);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void FormatCompletionMessage_Includes_Failed_Count_And_Honest_Wording()
    {
        // 1. With failed pipelines and verified cache
        string msgWithFailed = ShaderCompilerCoordinator.FormatCompletionMessage(
            done: 2402, failed: 96, cacheBytes: 300 * 1024 * 1024L, cacheVerified: true);
        StringAssert.Contains(msgWithFailed, "2,306 compiled, 96 failed");
        StringAssert.Contains(msgWithFailed, "cache verified");
        Assert.IsFalse(msgWithFailed.Contains("0 stutter", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(msgWithFailed.Contains("zero", StringComparison.OrdinalIgnoreCase));

        // 2. Zero failed pipelines and verified cache
        string msgNoFailed = ShaderCompilerCoordinator.FormatCompletionMessage(
            done: 26500, failed: 0, cacheBytes: 2L * 1024 * 1024 * 1024, cacheVerified: true);
        StringAssert.Contains(msgNoFailed, "26,500 compiled");
        Assert.IsFalse(msgNoFailed.Contains("failed", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(msgNoFailed, "cache verified");

        // 3. Unverified cache
        string msgUnverified = ShaderCompilerCoordinator.FormatCompletionMessage(
            done: 500, failed: 4, cacheBytes: 0, cacheVerified: false, verifyDetail: "shader cache folder missing");
        StringAssert.Contains(msgUnverified, "496 compiled, 4 failed");
        StringAssert.Contains(msgUnverified, "couldn't confirm cache files in game folder");
    }

    [TestMethod]
    public async Task SteamLinuxSource_Discovers_Native_Xdg_And_Flatpak_And_Reports_Diagnostics()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-steam-p1b-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string xdgData = Path.Combine(root, "custom_xdg_data");

        string nativeSteam = Path.Combine(xdgData, "Steam");
        string flatpakSteam = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        string flatpakSymlinkParent = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data");

        Directory.CreateDirectory(Path.Combine(nativeSteam, "steamapps", "common", "NativeGame"));
        Directory.CreateDirectory(Path.Combine(nativeSteam, "steamapps", "common", "Proton - Experimental"));
        Directory.CreateDirectory(Path.Combine(flatpakSteam, "steamapps", "common", "FlatpakGame"));

        try
        {
            // Create symlink ~/.var/app/com.valvesoftware.Steam/data -> .local/share to test ancestor symlink deduplication
            Directory.CreateSymbolicLink(flatpakSymlinkParent, Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share"));

            File.WriteAllText(Path.Combine(nativeSteam, "steamapps", "common", "NativeGame", "NativeGame.exe"), "MZ");
            File.WriteAllText(Path.Combine(flatpakSteam, "steamapps", "common", "FlatpakGame", "FlatpakGame.exe"), "MZ");

            File.WriteAllText(Path.Combine(nativeSteam, "steamapps", "appmanifest_100.acf"),
                "\"AppState\" { \"appid\" \"100\" \"name\" \"Native Game\" \"installdir\" \"NativeGame\" }");
            File.WriteAllText(Path.Combine(nativeSteam, "steamapps", "appmanifest_1493710.acf"),
                "\"AppState\" { \"appid\" \"1493710\" \"name\" \"Proton Experimental\" \"installdir\" \"Proton - Experimental\" }");
            File.WriteAllText(Path.Combine(flatpakSteam, "steamapps", "appmanifest_200.acf"),
                "\"AppState\" { \"appid\" \"200\" \"name\" \"Flatpak Game\" \"installdir\" \"FlatpakGame\" }");

            var source = new Shaderless.Core.Games.SteamLinuxSource(home, xdgData);
            var diag = await source.DiagnoseAsync();

            // Should find exactly 2 games (1 Native from XDG, 1 Flatpak deduplicated despite data/Steam symlink)
            Assert.AreEqual(2, diag.DiscoveredGames.Count);
            Assert.IsTrue(diag.DiscoveredGames.Any(g => g.Id == "100" && !g.IsFlatpak && g.Name == "Native Game"));
            Assert.IsTrue(diag.DiscoveredGames.Any(g => g.Id == "flatpak_200" && g.IsFlatpak && g.RawId == "200" && g.Name == "Flatpak Game"));

            // Proton Experimental should be recorded in SkippedItems
            Assert.IsTrue(diag.SkippedItems.Any(s => s.NameOrId.Contains("Proton Experimental") && s.Reason.Contains("Proton")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task HeroicLinuxSource_Discovers_Native_Xdg_And_Flatpak()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-heroic-p1b-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string xdgConfig = Path.Combine(root, "custom_xdg_config");

        string nativeHeroic = Path.Combine(xdgConfig, "heroic", "legendaryConfig", "legendary");
        string flatpakHeroic = Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "legendaryConfig", "legendary");
        string gameDir = Path.Combine(root, "Games", "ShortHike");

        Directory.CreateDirectory(nativeHeroic);
        Directory.CreateDirectory(flatpakHeroic);
        Directory.CreateDirectory(gameDir);

        try
        {
            File.WriteAllText(Path.Combine(gameDir, "AShortHike.exe"), "MZ");
            string installedJson = $$"""
            {
              "shorthike": {
                "title": "A Short Hike",
                "install_path": "{{gameDir.Replace("\\", "\\\\")}}",
                "executable": "AShortHike.exe",
                "is_dlc": false
              }
            }
            """;
            File.WriteAllText(Path.Combine(nativeHeroic, "installed.json"), installedJson);
            File.WriteAllText(Path.Combine(flatpakHeroic, "installed.json"), installedJson);

            var source = new Shaderless.Core.Games.HeroicLinuxSource(home, xdgConfig);
            var games = await source.DiscoverGamesAsync();

            Assert.AreEqual(2, games.Count);
            Assert.IsTrue(games.Any(g => g.Id == "shorthike" && !g.IsFlatpak));
            Assert.IsTrue(games.Any(g => g.Id == "flatpak_shorthike" && g.IsFlatpak && g.RawId == "shorthike"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class ThrowingGameSource : Shaderless.Core.Games.IGameSource
    {
        public GameSource SourceType => GameSource.Lutris;
        public Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated corrupted SQLite database");
    }

    private sealed class StaticGameSource : Shaderless.Core.Games.IGameSource
    {
        private readonly GameInfo _game;
        public StaticGameSource(GameInfo game) => _game = game;
        public GameSource SourceType => _game.Source;
        public Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameInfo>>(new[] { _game });
    }

    [TestMethod]
    public async Task GameManager_Isolates_Broken_Launcher_During_Scan_And_Diagnose()
    {
        var goodGame = MakeGame("999", GameSource.Steam, "/tmp", "/tmp/Game.exe");
        var manager = new Shaderless.Core.Games.GameManager(new Shaderless.Core.Games.IGameSource[]
        {
            new ThrowingGameSource(),
            new StaticGameSource(goodGame)
        });

        var games = await manager.ScanAllGamesAsync();
        Assert.AreEqual(1, games.Count);
        Assert.AreEqual("999", games[0].Id);

        var report = await manager.DiagnoseAsync();
        Assert.AreEqual(2, report.Sources.Count);
        var broken = report.Sources.First(s => s.Source == GameSource.Lutris);
        StringAssert.Contains(broken.Error, "Simulated corrupted SQLite database");
        var healthy = report.Sources.First(s => s.Source == GameSource.Steam);
        Assert.AreEqual(1, healthy.DiscoveredGames.Count);
    }

    // =========================================================================
    // Part 2: Settings, Auto-Record, Incremental Re-Compile, Auto-Compile & Stale
    // =========================================================================

    [TestMethod]
    public void SettingsManager_Defaults_All_Options_To_Off_And_Persists_Changes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "shaderless_settings_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            var initial = Shaderless.Core.SettingsManager.Load(settingsPath);
            Assert.IsFalse(initial.AutoRecordAllGames);
            Assert.IsFalse(initial.AutoCompileOnExit);

            Shaderless.Core.SettingsManager.Save(new AppSettings(AutoRecordAllGames: true, AutoCompileOnExit: false), settingsPath);
            var reloaded = Shaderless.Core.SettingsManager.Load(settingsPath);
            Assert.IsTrue(reloaded.AutoRecordAllGames);
            Assert.IsFalse(reloaded.AutoCompileOnExit);

            var withOptOut = Shaderless.Core.SettingsManager.SetGameAutoRecordOptOut("2828710", optOut: true, settingsPath);
            Assert.IsTrue(withOptOut.IsAutoRecordDisabledForGame("2828710"));

            var clearedOptOut = Shaderless.Core.SettingsManager.SetGameAutoRecordOptOut("2828710", optOut: false, settingsPath);
            Assert.IsFalse(clearedOptOut.IsAutoRecordDisabledForGame("2828710"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void RecorderManager_TryAutoInstall_Skips_Games_With_AntiCheat_Or_Foreign_Dll()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "shaderless_autorec_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string exePath = Path.Combine(tempDir, "Game.exe");
            File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
            File.WriteAllBytes(Path.Combine(tempDir, "start_protected_game.exe"), new byte[] { 0x4D, 0x5A });

            var acGame = MakeGame("ac_1", GameSource.Steam, tempDir, exePath);
            Assert.IsFalse(Shaderless.Core.Games.RecorderManager.TryAutoInstall(acGame));
            Assert.IsFalse(File.Exists(Path.Combine(tempDir, "d3d12.dll")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void RecorderManager_FilterDatabasesToUnwarmed_Removes_AlreadyWarmed_Items()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "shaderless_inc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string recDb = Path.Combine(tempDir, "shaderless.db");
            string genDb = Path.Combine(tempDir, "shaderless_gen.db");

            var blobPayload = new byte[24];
            var pso1 = new Shaderless.Engine.Planning.PsoDb.Rec('G', new byte[] { 1, 2, 3, 4 });
            var pso2 = new Shaderless.Engine.Planning.PsoDb.Rec('C', new byte[] { 5, 6, 7, 8 });
            var d11Item = new Shaderless.Engine.Planning.PsoDb.Rec('1', new byte[24]);

            using (var fs = File.Create(recDb))
            {
                Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', blobPayload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, pso1.Tag, pso1.Payload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, pso2.Tag, pso2.Payload);
            }
            using (var fs = File.Create(genDb))
            {
                Shaderless.Engine.Planning.PsoDb.Write(fs, d11Item.Tag, d11Item.Payload);
            }

            // Mark pso1 and d11Item as already warmed; only pso2 is new
            var warmed = new HashSet<string>(StringComparer.Ordinal) { pso1.Key, d11Item.Key };
            var (allKeys, unwarmedCount, alreadyWarmedCount) =
                Shaderless.Core.Games.RecorderManager.FilterDatabasesToUnwarmed(recDb, genDb, warmed);

            Assert.AreEqual(3, allKeys.Count);
            Assert.AreEqual(1, unwarmedCount);
            Assert.AreEqual(2, alreadyWarmedCount);

            var remainingRecs = Shaderless.Engine.Planning.PsoDb.Read(recDb).ToList();
            Assert.IsTrue(remainingRecs.Any(r => r.Tag == 'B'));
            Assert.IsFalse(remainingRecs.Any(r => r.Key == pso1.Key));
            Assert.IsTrue(remainingRecs.Any(r => r.Key == pso2.Key));

            var remainingGen = Shaderless.Engine.Planning.PsoDb.Read(genDb).ToList();
            Assert.AreEqual(0, remainingGen.Count);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void GameExitMonitor_DetectExitedGames_Fires_Only_When_Game_Exits_And_No_Other_Game_Runs()
    {
        var g1 = MakeGame("1", GameSource.Steam, "/tmp/g1", "/tmp/g1/Game1.exe");
        var g2 = MakeGame("2", GameSource.Heroic, "/tmp/g2", "/tmp/g2/Game2.exe");
        var games = new[] { g1, g2 };
        var runningSet = new HashSet<string>(StringComparer.Ordinal);

        // Step 1: g1 starts running
        var exited1 = Shaderless.Core.Games.GameExitMonitor.DetectExitedGames(
            games, runningSet, g => g.Id == "1");
        Assert.AreEqual(0, exited1.Count);
        Assert.IsTrue(runningSet.Contains("1"));

        // Step 2: g1 exits, g2 is also running -> should suppress auto-compile while g2 is open
        var exited2 = Shaderless.Core.Games.GameExitMonitor.DetectExitedGames(
            games, runningSet, g => g.Id == "2");
        Assert.AreEqual(0, exited2.Count);
        Assert.IsFalse(runningSet.Contains("1"));
        Assert.IsTrue(runningSet.Contains("2"));

        // Step 3: g2 exits and no game is running -> returns g2
        var exited3 = Shaderless.Core.Games.GameExitMonitor.DetectExitedGames(
            games, runningSet, _ => false);
        Assert.AreEqual(1, exited3.Count);
        Assert.AreEqual("2", exited3[0].Id);
    }

    [TestMethod]
    public void GameManager_EvaluateStaleReason_Detects_Driver_Proton_And_Game_Updates()
    {
        // Matching stamps -> not stale
        Assert.IsNull(Shaderless.Core.Games.GameManager.EvaluateStaleReason(
            "595.58.03", "/proton|100|200", "1000:500|2:5000:900",
            "595.58.03", "/proton|100|200", "1000:500|2:5000:900"));

        // Null saved stamps (adopting legacy cache) -> not stale
        Assert.IsNull(Shaderless.Core.Games.GameManager.EvaluateStaleReason(
            null, null, null,
            "595.58.03", "/proton|100|200", "1000:500|2:5000:900"));

        // Driver updated
        Assert.AreEqual("driver updated", Shaderless.Core.Games.GameManager.EvaluateStaleReason(
            "590.00.00", "/proton|100|200", "1000:500|2:5000:900",
            "595.58.03", "/proton|100|200", "1000:500|2:5000:900"));

        // Proton updated
        Assert.AreEqual("Proton updated", Shaderless.Core.Games.GameManager.EvaluateStaleReason(
            "595.58.03", "/proton-9|100|200", "1000:500|2:5000:900",
            "595.58.03", "/proton-10|110|210", "1000:500|2:5000:900"));

        // Game updated (patch changed exe or archive)
        Assert.AreEqual("game updated", Shaderless.Core.Games.GameManager.EvaluateStaleReason(
            "595.58.03", "/proton|100|200", "1000:500|2:5000:900",
            "595.58.03", "/proton|100|200", "1024:600|3:6000:950"));
    }

    [TestMethod]
    public void FinalizeDriverCaches_Merges_Runtime_GlCache_Without_Shrinking_And_Deduplicates_Vkd3d()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-inc-finalize-" + Guid.NewGuid().ToString("N"));
        string targetCache = Path.Combine(root, "shadercache");
        string vkd3dCache = Path.Combine(targetCache, "VKD3D_shader_cache");
        string glCache = Path.Combine(targetCache, "nvidiav1", "GLCache");
        string glLeaf = Path.Combine(glCache, "ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        string gameExeDir = Path.Combine(root, "game", "Binaries", "Win64");
        Directory.CreateDirectory(vkd3dCache);
        Directory.CreateDirectory(glLeaf);
        Directory.CreateDirectory(gameExeDir);

        static (byte[] Bin, byte[] Toc) BuildSyntheticNvdc(params (ulong K0, ulong K1, byte[] Payload)[] entries)
        {
            byte[] header = new byte[32];
            header[0] = (byte)'N'; header[1] = (byte)'V'; header[2] = (byte)'D'; header[3] = (byte)'C';
            header[6] = 3;

            using var binMs = new MemoryStream();
            using var tocMs = new MemoryStream();
            binMs.Write(header);
            tocMs.Write(header);

            foreach (var (k0, k1, payload) in entries)
            {
                uint off = (uint)binMs.Length;
                uint len = (uint)payload.Length;

                byte[] recHdr = new byte[32];
                recHdr[0] = 0x9d; recHdr[1] = 0xa1; recHdr[2] = 0x46; recHdr[3] = 0x98;
                BitConverter.TryWriteBytes(recHdr.AsSpan(4, 8), k0);
                BitConverter.TryWriteBytes(recHdr.AsSpan(12, 8), k1);
                BitConverter.TryWriteBytes(recHdr.AsSpan(28, 4), len);
                binMs.Write(recHdr);
                binMs.Write(payload);

                byte[] tocEntry = new byte[24];
                BitConverter.TryWriteBytes(tocEntry.AsSpan(0, 8), k0);
                BitConverter.TryWriteBytes(tocEntry.AsSpan(8, 8), k1);
                BitConverter.TryWriteBytes(tocEntry.AsSpan(16, 4), off);
                BitConverter.TryWriteBytes(tocEntry.AsSpan(20, 4), len);
                tocMs.Write(tocEntry);
            }

            return (binMs.ToArray(), tocMs.ToArray());
        }

        try
        {
            // 1. Existing steam_shader_cache from Area 1 (2 entries) + smaller new runtime bucket from Area 2 (1 overlap + 1 new entry)
            var (area1Bin, area1Toc) = BuildSyntheticNvdc(
                (1UL, 100UL, new byte[200]),
                (2UL, 200UL, new byte[300]));
            var (area2Bin, area2Toc) = BuildSyntheticNvdc(
                (1UL, 100UL, new byte[200]),
                (3UL, 300UL, new byte[150]));

            string steamRoBin = Path.Combine(glLeaf, "steam_shader_cache.bin");
            string steamRoToc = Path.Combine(glLeaf, "steam_shader_cache.toc");
            File.WriteAllBytes(steamRoBin, area1Bin);
            File.WriteAllBytes(steamRoToc, area1Toc);

            // Warmer primary bucket (1024 bytes) + smaller Area 2 runtime bucket
            byte[] primaryBytes = Enumerable.Range(0, 1024).Select(i => (byte)(i & 0xFF)).ToArray();
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache0.bin"), primaryBytes);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache0.toc"), new byte[128]);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache1.bin"), area2Bin);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache1.toc"), area2Toc);

            // 2. VKD3D cache with existing gameExeDir cache + compiled cache
            byte[] header = Enumerable.Range(1, 48).Select(i => (byte)i).ToArray();
            byte[] existingPayload = new byte[] { 10, 20, 30, 40 };
            byte[] newPayload = new byte[] { 50, 60, 70, 80, 90, 100 };

            string gameCacheFile = Path.Combine(gameExeDir, "vkd3d-proton.cache");
            File.WriteAllBytes(gameCacheFile, header.Concat(existingPayload).ToArray());

            string compiledCacheFile = Path.Combine(vkd3dCache, "vkd3d-proton.Game.exe.cache");
            File.WriteAllBytes(compiledCacheFile, header.Concat(newPayload).ToArray());

            // Run FinalizeDriverCaches twice (simulating incremental runs)
            ProtonRunner.FinalizeDriverCaches(targetCache, vkd3dCache, glCache, gameExeDir);
            ProtonRunner.FinalizeDriverCaches(targetCache, vkd3dCache, glCache, gameExeDir);

            // steam_shader_cache.bin must contain Area 1 + Area 2 unique entry (3 entries total in .toc = 32 + 3*24 = 104 bytes)
            Assert.AreEqual(32 + 3 * 24, new FileInfo(steamRoToc).Length);
            Assert.AreEqual(area1Bin.Length + 32 + 150, new FileInfo(steamRoBin).Length);

            // VKD3D cache must NOT duplicate newPayload on the second FinalizeDriverCaches call
            byte[] mergedVkd3d = File.ReadAllBytes(gameCacheFile);
            Assert.AreEqual(48 + existingPayload.Length + newPayload.Length, mergedVkd3d.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void PrepareDriverCachesForWarmer_Resets_On_Driver_Or_Proton_Change_And_Full_Compile_But_Preserves_On_Incremental()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-prep-" + Guid.NewGuid().ToString("N"));
        string targetCache = Path.Combine(root, "shadercache");
        string vkd3dCache = Path.Combine(targetCache, "VKD3D_shader_cache");
        string glCache = Path.Combine(targetCache, "nvidiav1", "GLCache");
        string glLeaf = Path.Combine(glCache, "ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        string workDir = Path.Combine(root, "work");
        string gameExeDir = Path.Combine(root, "game", "Binaries", "Win64");
        Directory.CreateDirectory(vkd3dCache);
        Directory.CreateDirectory(glLeaf);
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(gameExeDir);

        try
        {
            string mergedBin = Path.Combine(glLeaf, "steamapp_merged_shader_cache.bin");
            string mergedToc = Path.Combine(glLeaf, "steamapp_merged_shader_cache.toc");
            string steamRoBin = Path.Combine(glLeaf, "steam_shader_cache.bin");
            string steamRoToc = Path.Combine(glLeaf, "steam_shader_cache.toc");
            string vkd3dFile = Path.Combine(vkd3dCache, "vkd3d-proton.Game.exe.cache");
            string exeVkd3d = Path.Combine(gameExeDir, "vkd3d-proton.cache");

            File.WriteAllBytes(Path.Combine(workDir, "d3d12core.dll"), new byte[100]);

            // Seed initial stamp via first full compile
            ProtonRunner.PrepareDriverCachesForWarmer(
                targetCache, vkd3dCache, glCache, "/proton-10.0/proton", workDir, isIncrementalRun: false, gameExeDir);

            string part1Bin = Path.Combine(glLeaf, "shaderless_part1.bin");
            string part1Toc = Path.Combine(glLeaf, "shaderless_part1.toc");

            // Populate compiled caches after first full compile (including multi-part spill file)
            File.WriteAllBytes(mergedBin, new byte[2048]);
            File.WriteAllBytes(mergedToc, new byte[128]);
            File.WriteAllBytes(part1Bin, new byte[1024]);
            File.WriteAllBytes(part1Toc, new byte[64]);
            File.WriteAllBytes(steamRoBin, new byte[512]);
            File.WriteAllBytes(steamRoToc, new byte[64]);
            File.WriteAllBytes(vkd3dFile, new byte[256]);
            File.WriteAllBytes(exeVkd3d, new byte[256]);

            // Case 1: Incremental run with UNCHANGED Proton/driver stamp -> preserves mergedBin, part1Bin, steamRoBin, vkd3d,
            // and leaves writable buckets empty so shaderless_warm.exe has full 1.85 GB headroom while reading all parts lock-free!
            ProtonRunner.PrepareDriverCachesForWarmer(
                targetCache, vkd3dCache, glCache, "/proton-10.0/proton", workDir, isIncrementalRun: true, gameExeDir);

            Assert.IsTrue(File.Exists(mergedBin));
            Assert.IsTrue(File.Exists(part1Bin));
            Assert.IsTrue(File.Exists(steamRoBin));
            Assert.IsTrue(File.Exists(vkd3dFile));
            Assert.IsTrue(File.Exists(exeVkd3d));
            Assert.IsFalse(File.Exists(Path.Combine(glLeaf, "steamapp_shader_cache0.bin")));
            Assert.IsFalse(File.Exists(Path.Combine(glLeaf, "steamapp_shader_cache1.bin")));

            // Case 2: Proton updated (/proton-11.0/proton) even with isIncrementalRun: true -> resets stale caches (including parts)
            ProtonRunner.PrepareDriverCachesForWarmer(
                targetCache, vkd3dCache, glCache, "/proton-11.0/proton", workDir, isIncrementalRun: true, gameExeDir);

            Assert.IsFalse(File.Exists(mergedBin));
            Assert.IsFalse(File.Exists(part1Bin));
            Assert.IsFalse(File.Exists(steamRoBin));
            Assert.IsFalse(File.Exists(vkd3dFile));
            Assert.IsFalse(File.Exists(exeVkd3d));

            // Re-populate caches under /proton-11.0/proton
            File.WriteAllBytes(mergedBin, new byte[2048]);
            File.WriteAllBytes(mergedToc, new byte[128]);
            File.WriteAllBytes(part1Bin, new byte[1024]);
            File.WriteAllBytes(part1Toc, new byte[64]);
            File.WriteAllBytes(vkd3dFile, new byte[256]);
            File.WriteAllBytes(exeVkd3d, new byte[256]);

            // Case 3: Full re-compile (isIncrementalRun: false) with same Proton -> resets caches so full compiles never stack
            ProtonRunner.PrepareDriverCachesForWarmer(
                targetCache, vkd3dCache, glCache, "/proton-11.0/proton", workDir, isIncrementalRun: false, gameExeDir);

            Assert.IsFalse(File.Exists(mergedBin));
            Assert.IsFalse(File.Exists(part1Bin));
            Assert.IsFalse(File.Exists(vkd3dFile));
            Assert.IsFalse(File.Exists(exeVkd3d));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void IncrementalCompile_Preserves_Full_ShaderlessWarmed_Snapshot_After_Filtering()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-warmed-snap-" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(root, "game");
        Directory.CreateDirectory(gameDir);
        string exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

        string testGameId = "test_snap_" + Guid.NewGuid().ToString("N");
        var game = MakeGame(testGameId, GameSource.Steam, gameDir, exePath);
        string workDir = Path.Combine(Shaderless.Core.AppPaths.DataDir, "work", testGameId);
        Directory.CreateDirectory(workDir);

        try
        {
            string recDb = Path.Combine(workDir, "shaderless.db");
            string genDb = Path.Combine(workDir, "shaderless_gen.db");

            var pso1 = new Shaderless.Engine.Planning.PsoDb.Rec('G', new byte[] { 1, 2, 3, 4 });
            var pso2 = new Shaderless.Engine.Planning.PsoDb.Rec('C', new byte[] { 5, 6, 7, 8 });
            var pso3 = new Shaderless.Engine.Planning.PsoDb.Rec('S', new byte[] { 9, 10, 11, 12 });

            using (var fs = File.Create(recDb))
            {
                Shaderless.Engine.Planning.PsoDb.Write(fs, pso1.Tag, pso1.Payload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, pso2.Tag, pso2.Payload);
            }
            using (var fs = File.Create(genDb))
            {
                Shaderless.Engine.Planning.PsoDb.Write(fs, pso3.Tag, pso3.Payload);
            }

            // Build snapshot BEFORE FilterDatabasesToUnwarmed strips pso1 & pso2 out of recDb
            byte[]? fullSnapshot = Shaderless.Core.Games.RecorderManager.BuildWarmedSnapshot(new[] { recDb, genDb });
            Assert.IsNotNull(fullSnapshot);
            // 8-byte "SCSKWARM" + 4-byte count (3) + 3 * 20-byte hashes = 72 bytes
            Assert.AreEqual(8 + 4 + 3 * 20, fullSnapshot.Length);

            var alreadyWarmed = new HashSet<string>(StringComparer.Ordinal) { pso1.Key, pso2.Key };
            var (allKeys, unwarmedCount, alreadyWarmedCount) =
                Shaderless.Core.Games.RecorderManager.FilterDatabasesToUnwarmed(recDb, genDb, alreadyWarmed);

            Assert.AreEqual(3, allKeys.Count);
            Assert.AreEqual(1, unwarmedCount);
            Assert.AreEqual(2, alreadyWarmedCount);

            Shaderless.Core.Games.RecorderManager.SaveWarmedKeys(
                game, allKeys, "stamp1", new[] { recDb, genDb }, fullSnapshot);

            string savedWarmed = Path.Combine(Shaderless.Core.Games.RecorderManager.GameDir(testGameId), "shaderless.warmed");
            Assert.IsTrue(File.Exists(savedWarmed));
            Assert.AreEqual(8 + 4 + 3 * 20, new FileInfo(savedWarmed).Length);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
            string recGameDir = Shaderless.Core.Games.RecorderManager.GameDir(testGameId);
            try { if (Directory.Exists(recGameDir)) Directory.Delete(recGameDir, true); } catch { }
        }
    }

    [TestMethod]
    public void LinuxPlatformGuards_KeyFiles_Middleware_GameFiles_And_ProcessMemory_DoNotRequireKernel32()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-mw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string dllPath = Path.Combine(root, "amd_fidelityfx_dx12.dll");
            File.WriteAllBytes(dllPath, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04 });

            string id = Shaderless.Engine.App.KeyFiles.Identity(dllPath);
            Assert.IsFalse(string.IsNullOrEmpty(id));
            Assert.AreEqual("", Shaderless.Engine.App.KeyFiles.Identity(Path.Combine(root, "missing.dll")));

            var detected = Shaderless.Engine.Planning.Middleware.Detect(root);
            Assert.HasCount(1, detected);
            Assert.AreEqual("amd", detected[0].Vendor);

            // Middleware.Scan calls internal KeyFiles.Stamp -> KeyFiles.Identity under the hood
            var scanned = Shaderless.Engine.Planning.Middleware.Scan(dllPath);
            Assert.AreEqual(8, scanned.Size);
            Assert.IsEmpty(scanned.Containers);

            string? finalDir = Shaderless.Engine.Games.GameFiles.FinalPath(root);
            Assert.IsNotNull(finalDir);
            Assert.IsNull(Shaderless.Engine.Games.GameFiles.FinalPath(Path.Combine(root, "does-not-exist")));

            var gfId = Shaderless.Engine.Games.GameFiles.Identity(dllPath);
            Assert.IsNotNull(gfId);
            Assert.IsNotNull(gfId.Value.Final);

            // Must be a clean no-op on Linux without P/Invoking kernel32.dll
            Shaderless.Engine.App.ProcessMemory.Trim();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void MigrateLegacyFiles_Renames_ScsKiller_Files_To_Shaderless()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "scskiller.db"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(root, "scskiller.keys"), "deadbeef\n");
            File.WriteAllText(Path.Combine(root, "scskiller_creates.csv"), "seq,ms\n1,0.2\n");

            Shaderless.Core.Games.RecorderManager.MigrateLegacyFiles(root);

            Assert.IsFalse(File.Exists(Path.Combine(root, "scskiller.db")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "shaderless.db")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "scskiller.keys")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "shaderless.keys")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "scskiller_creates.csv")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "shaderless_creates.csv")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void GameManager_FilterGames_FiltersByLauncher_Search_And_NeedsCompile()
    {
        var games = new List<Shaderless.Core.Models.GameInfo>
        {
            new(
                Id: "1285190",
                Name: "Borderlands 4",
                Source: Shaderless.Core.Models.GameSource.Steam,
                InstallPath: "/games/bl4",
                ExecutablePath: "/games/bl4/bl4.exe",
                PrefixPath: null,
                BannerPath: null,
                Engine: Shaderless.Core.Models.GameEngine.UnrealEngine5,
                State: Shaderless.Core.Models.CacheState.NeedsCompilation,
                TotalShadersEstimated: 20000,
                CompiledShadersCount: 0,
                LastCompiledAt: null),
            new(
                Id: "flatpak_kcd2",
                Name: "Kingdom Come Deliverance 2",
                Source: Shaderless.Core.Models.GameSource.Heroic,
                InstallPath: "/games/kcd2",
                ExecutablePath: "/games/kcd2/kcd2.exe",
                PrefixPath: null,
                BannerPath: null,
                Engine: Shaderless.Core.Models.GameEngine.DirectX12,
                State: Shaderless.Core.Models.CacheState.Cached,
                TotalShadersEstimated: 6000,
                CompiledShadersCount: 6000,
                LastCompiledAt: DateTime.UtcNow,
                IsFlatpak: true,
                NewRecordedPsos: 0),
            new(
                Id: "lutris_eoa",
                Name: "End of Abyss",
                Source: Shaderless.Core.Models.GameSource.Lutris,
                InstallPath: "/games/eoa",
                ExecutablePath: "/games/eoa/eoa.exe",
                PrefixPath: null,
                BannerPath: null,
                Engine: Shaderless.Core.Models.GameEngine.UnrealEngine5,
                State: Shaderless.Core.Models.CacheState.Cached,
                TotalShadersEstimated: 8000,
                CompiledShadersCount: 8000,
                LastCompiledAt: DateTime.UtcNow,
                NewRecordedPsos: 42)
        };

        // 1. Launcher filter
        var steamOnly = Shaderless.Core.Games.GameManager.FilterGames(games, Shaderless.Core.Models.GameSource.Steam);
        Assert.HasCount(1, steamOnly);
        Assert.AreEqual("1285190", steamOnly[0].Id);

        var heroicOnly = Shaderless.Core.Games.GameManager.FilterGames(games, Shaderless.Core.Models.GameSource.Heroic);
        Assert.HasCount(1, heroicOnly);
        Assert.AreEqual("flatpak_kcd2", heroicOnly[0].Id);

        // 2. Needs Compile filter (includes NeedsCompilation, Outdated, AND Cached games with NewRecordedPsos > 0)
        var needsCompile = Shaderless.Core.Games.GameManager.FilterGames(games, onlyNeedsCompile: true);
        Assert.HasCount(2, needsCompile);
        Assert.IsTrue(needsCompile.Any(g => g.Id == "1285190"));
        Assert.IsTrue(needsCompile.Any(g => g.Id == "lutris_eoa"));

        // 3. Search by name, engine, ID, or Flatpak tag
        Assert.HasCount(2, Shaderless.Core.Games.GameManager.FilterGames(games, searchQuery: "UnrealEngine5"));
        Assert.HasCount(1, Shaderless.Core.Games.GameManager.FilterGames(games, searchQuery: "kingdom"));
        Assert.HasCount(1, Shaderless.Core.Games.GameManager.FilterGames(games, searchQuery: "flatpak"));
        Assert.IsEmpty(Shaderless.Core.Games.GameManager.FilterGames(games, Shaderless.Core.Models.GameSource.Steam, searchQuery: "kingdom"));
    }

    [TestMethod]
    public void ResolveSteamProtonForApp_DetectsPerGameProtonSwitchImmediatelyBeforeGameLaunch()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-proton-switch-" + Guid.NewGuid().ToString("N"));
        string testGameId = "test_proton_stamp_" + Guid.NewGuid().ToString("N");
        try
        {
            string oldProtonDir = Path.Combine(root, "compatibilitytools.d", "Proton-Old");
            string newExpDir = Path.Combine(root, "steamapps", "common", "Proton - Experimental");
            string proton10Dir = Path.Combine(root, "steamapps", "common", "Proton 10.0");
            string customAliasDir = Path.Combine(root, "compatibilitytools.d", "CustomFolder");

            Directory.CreateDirectory(Path.Combine(oldProtonDir, "files"));
            Directory.CreateDirectory(newExpDir);
            Directory.CreateDirectory(proton10Dir);
            Directory.CreateDirectory(customAliasDir);

            string oldProtonBin = Path.Combine(oldProtonDir, "proton");
            string newExpBin = Path.Combine(newExpDir, "proton");
            string proton10Bin = Path.Combine(proton10Dir, "proton");
            string customAliasBin = Path.Combine(customAliasDir, "proton");

            File.WriteAllText(oldProtonBin, "#!/bin/sh\n");
            File.WriteAllText(newExpBin, "#!/bin/sh\n");
            File.WriteAllText(proton10Bin, "#!/bin/sh\n");
            File.WriteAllText(customAliasBin, "#!/bin/sh\n");

            File.WriteAllText(Path.Combine(customAliasDir, "compatibilitytool.vdf"),
                "\"compatibilitytools\"\n{\n  \"compat_tools\"\n  {\n    \"my-custom-cachyos-id\"\n    {\n      \"install_path\" \".\"\n    }\n  }\n}\n");

            // Simulate existing prefix config_info still pointing to Proton-Old (game has NOT been launched since switching)
            string compatDir = Path.Combine(root, "steamapps", "compatdata", "2828710");
            Directory.CreateDirectory(Path.Combine(compatDir, "pfx"));
            File.WriteAllText(Path.Combine(compatDir, "config_info"), $"10.0\n{oldProtonDir}/files/share/default_pfx/\n");

            Assert.IsTrue(Shaderless.Core.Drivers.ProtonRunner.DoesPrefixMatchProton(compatDir, oldProtonBin));
            Assert.IsFalse(Shaderless.Core.Drivers.ProtonRunner.DoesPrefixMatchProton(compatDir, newExpBin));

            string cfgDir = Path.Combine(root, "config");
            Directory.CreateDirectory(cfgDir);
            string cfgVdf = Path.Combine(cfgDir, "config.vdf");

            // 1. User switches Valor Mortis (2828710) in Steam dropdown to Proton - Experimental ("proton_experimental")
            File.WriteAllText(cfgVdf,
                "\"InstallConfigStore\"\n{\n  \"Software\"\n  {\n    \"Valve\"\n    {\n      \"Steam\"\n      {\n        \"CompatToolMapping\"\n        {\n          \"0\"\n          {\n            \"name\" \"proton_10\"\n            \"config\" \"\"\n            \"priority\" \"75\"\n          }\n          \"2828710\"\n          {\n            \"name\" \"proton_experimental\"\n            \"config\" \"\"\n            \"priority\" \"250\"\n          }\n        }\n      }\n    }\n  }\n}\n");

            string? resolved = Shaderless.Core.Drivers.ProtonRunner.ResolveSteamProtonForApp(
                root, "2828710", Path.Combine(compatDir, "pfx"), allowSystemCompatTools: false);
            Assert.AreEqual(newExpBin, resolved);

            // 2. User switches dropdown to a custom compatibilitytool.vdf alias ("my-custom-cachyos-id")
            File.WriteAllText(cfgVdf,
                "\"InstallConfigStore\"\n{\n  \"CompatToolMapping\"\n  {\n    \"2828710\"\n    {\n      \"name\" \"my-custom-cachyos-id\"\n    }\n  }\n}\n");

            string? resolvedAlias = Shaderless.Core.Drivers.ProtonRunner.ResolveSteamProtonForApp(
                root, "2828710", Path.Combine(compatDir, "pfx"), allowSystemCompatTools: false);
            Assert.AreEqual(customAliasBin, resolvedAlias);

            // 3. User unchecks "Force specific Steam Play compatibility tool" ("name" "") -> immediately falls back to "0" ("proton_10")
            File.WriteAllText(cfgVdf,
                "\"InstallConfigStore\"\n{\n  \"CompatToolMapping\"\n  {\n    \"0\"\n    {\n      \"name\" \"proton_10\"\n    }\n    \"2828710\"\n    {\n      \"name\" \"\"\n    }\n  }\n}\n");

            string? resolvedUnchecked = Shaderless.Core.Drivers.ProtonRunner.ResolveSteamProtonForApp(
                root, "2828710", Path.Combine(compatDir, "pfx"), allowSystemCompatTools: false);
            Assert.AreEqual(proton10Bin, resolvedUnchecked);

            // 4. Game with no per-game entry in CompatToolMapping uses its prefix config_info (Proton-Old)
            File.WriteAllText(cfgVdf,
                "\"InstallConfigStore\"\n{\n  \"CompatToolMapping\"\n  {\n    \"0\"\n    {\n      \"name\" \"proton_10\"\n    }\n  }\n}\n");

            string? resolvedNoOverride = Shaderless.Core.Drivers.ProtonRunner.ResolveSteamProtonForApp(
                root, "2828710", Path.Combine(compatDir, "pfx"), allowSystemCompatTools: false);
            Assert.AreEqual(oldProtonBin, resolvedNoOverride);

            // 5. Verify GetWarmedStampMismatchReason distinguishes Proton vs Driver updates
            string warmedPath = Shaderless.Core.Games.RecorderManager.WarmedKeysPath(testGameId);
            Directory.CreateDirectory(Path.GetDirectoryName(warmedPath)!);
            File.WriteAllText(warmedPath, "#STAMP:615.78.08|/proton/a|6316032|7131136\n");

            Assert.IsNull(Shaderless.Core.Games.RecorderManager.GetWarmedStampMismatchReason(
                testGameId, "615.78.08", "/proton/a|6316032|7131136"));
            Assert.AreEqual("Proton updated", Shaderless.Core.Games.RecorderManager.GetWarmedStampMismatchReason(
                testGameId, "615.78.08", "/proton/b|6369280|7131136"));
            Assert.AreEqual("driver updated", Shaderless.Core.Games.RecorderManager.GetWarmedStampMismatchReason(
                testGameId, "620.01.00", "/proton/a|6316032|7131136"));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            string recGameDir = Shaderless.Core.Games.RecorderManager.GameDir(testGameId);
            try { if (Directory.Exists(recGameDir)) Directory.Delete(recGameDir, true); } catch { }
        }
    }

    [TestMethod]
    public void Issue3_WorldOfTanks_CefPaks_And_HelperExes_DoNotMisidentifyAsUnreal_And_DagorIsDetected()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-wot-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Simulated World of Tanks Steam install (CEF .pak files + cef_browser_process.exe + launcher + real WorldOfTanks.exe)
            string wotDir = Path.Combine(root, "World_of_Tanks_EU");
            Directory.CreateDirectory(Path.Combine(wotDir, "cef", "locales"));
            Directory.CreateDirectory(Path.Combine(wotDir, "win64"));

            File.WriteAllBytes(Path.Combine(wotDir, "cef", "cef_browser_process.exe"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(wotDir, "cef", "resources.pak"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(wotDir, "cef", "chrome_100_percent.pak"), new byte[512]);
            File.WriteAllBytes(Path.Combine(wotDir, "cef", "locales", "en-US.pak"), new byte[256]);
            File.WriteAllBytes(Path.Combine(wotDir, "WoTLauncher.exe"), new byte[1024]);
            string realWotExe = Path.Combine(wotDir, "win64", "WorldOfTanks.exe");
            File.WriteAllBytes(realWotExe, new byte[16384]);

            var (wotEngine, _) = Shaderless.Core.Games.GameEngineDetector.Detect(wotDir);
            Assert.AreNotEqual(GameEngine.UnrealEngine4, wotEngine);
            Assert.AreEqual(GameEngine.DirectX11, wotEngine);

            string? foundExe = Shaderless.Core.Games.GameEngineDetector.FindExecutable(wotDir);
            Assert.AreEqual(realWotExe, foundExe);

            // 2. Simulated Gaijin Dagor Engine install (War Thunder / Enlisted)
            string dagorDir = Path.Combine(root, "War Thunder");
            Directory.CreateDirectory(Path.Combine(dagorDir, "compiledShaders"));
            Directory.CreateDirectory(Path.Combine(dagorDir, "win64"));
            string acesExe = Path.Combine(dagorDir, "win64", "aces.exe");
            File.WriteAllBytes(acesExe, new byte[8192]);

            Assert.AreEqual(dagorDir, Shaderless.Core.Games.GameEngineDetector.InferInstallRoot(acesExe));
            var (dagorEngine, dagorEst) = Shaderless.Core.Games.GameEngineDetector.Detect(dagorDir);
            Assert.AreEqual(GameEngine.Dagor, dagorEngine);
            Assert.AreEqual(8500, dagorEst);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Issue2_KingdomComeDeliverance2_HeroicSideload_InfersTrueRoot_DetectsCryEngineDx12_And_MaterializesRecording()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-kcd2-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string xdgConfig = Path.Combine(root, "xdg_config");
        try
        {
            string flatpakHeroic = Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic");
            Directory.CreateDirectory(Path.Combine(flatpakHeroic, "sideload_apps"));
            Directory.CreateDirectory(Path.Combine(flatpakHeroic, "GamesConfig"));

            string kcd2Root = Path.Combine(root, "Library", "KingdomComeDeliverance2");
            string kcd2BinDir = Path.Combine(kcd2Root, "Bin", "Win64SharedMasterSteamPGO");
            string kcd2EngineDir = Path.Combine(kcd2Root, "Engine");
            Directory.CreateDirectory(kcd2BinDir);
            Directory.CreateDirectory(kcd2EngineDir);

            string kcd2Exe = Path.Combine(kcd2BinDir, "KingdomCome.exe");
            File.WriteAllBytes(kcd2Exe, new byte[32768]);
            File.WriteAllBytes(Path.Combine(kcd2BinDir, "WHGame.dll"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(kcd2BinDir, "amd_fidelityfx_dx12.dll"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(kcd2EngineDir, "Engine.pak"), new byte[512]);
            File.WriteAllBytes(Path.Combine(kcd2EngineDir, "ShaderCache.pak"), new byte[512]);

            string prefixesBase = Path.Combine(root, "Prefixes");
            string kcd2Prefix = Path.Combine(prefixesBase, "Kingdom Come: Deliverance II");
            Directory.CreateDirectory(kcd2Prefix);

            // Real Heroic sideload_apps/library.json sets "folder_name" to Path.GetDirectoryName(executable) (the Bin/Win64* subfolder!)
            File.WriteAllText(Path.Combine(flatpakHeroic, "sideload_apps", "library.json"), $$"""
            {
              "games": [
                {
                  "app_name": "kcd2_sideload",
                  "title": "Kingdom Come: Deliverance II",
                  "folder_name": "{{kcd2BinDir.Replace("\\", "\\\\")}}",
                  "is_installed": true,
                  "install": {
                    "executable": "{{kcd2Exe.Replace("\\", "\\\\")}}",
                    "is_dlc": false
                  }
                }
              ]
            }
            """);

            // Real Heroic writes {} to GamesConfig/<id>.json until per-game settings are edited, storing defaultWinePrefix in config.json
            File.WriteAllText(Path.Combine(flatpakHeroic, "GamesConfig", "kcd2_sideload.json"), "{}");
            File.WriteAllText(Path.Combine(flatpakHeroic, "config.json"), $$"""
            {
              "defaultSettings": {
                "defaultWinePrefix": "{{prefixesBase.Replace("\\", "\\\\")}}",
                "winePrefix": "{{Path.Combine(prefixesBase, "shared").Replace("\\", "\\\\")}}"
              }
            }
            """);

            var source = new Shaderless.Core.Games.HeroicLinuxSource(home, xdgConfig);
            var games = await source.DiscoverGamesAsync();
            Assert.HasCount(1, games);

            var kcd2 = games[0];
            Assert.AreEqual("flatpak_kcd2_sideload", kcd2.Id);
            Assert.IsTrue(kcd2.IsFlatpak);
            Assert.AreEqual(kcd2Root, kcd2.InstallPath);
            Assert.AreEqual(kcd2Exe, kcd2.ExecutablePath);
            Assert.AreEqual(kcd2Prefix, kcd2.PrefixPath);
            Assert.AreEqual(GameEngine.DirectX12, kcd2.Engine);

            // Simulate recorded pipeline in inbox -> merged into Brotli recording.db -> materialized standalone
            byte[] fakeShaderBytes = new byte[] { (byte)'D', (byte)'X', (byte)'B', (byte)'C', 1, 2, 3, 4, 5, 6, 7, 8 };
            byte[] sha1Bytes = System.Security.Cryptography.SHA1.HashData(fakeShaderBytes);
            string sha1Hex = Convert.ToHexStringLower(sha1Bytes);

            byte[] d11Payload = new byte[24]; // 4-byte stage + 20-byte shader SHA-1
            BitConverter.TryWriteBytes(d11Payload.AsSpan(0, 4), 0u);
            sha1Bytes.CopyTo(d11Payload.AsSpan(4, 20));

            string inboxDb = Path.Combine(root, "inbox_shaderless.db");
            using (var fs = File.Create(inboxDb))
            {
                Shaderless.Engine.Planning.PsoDb.WriteBlob(fs, sha1Hex, fakeShaderBytes);
                Shaderless.Engine.Planning.PsoDb.Write(fs, '1', d11Payload);
            }

            string storeDb = Path.Combine(root, "recording.db");
            Shaderless.Engine.App.Recordings.Merge(storeDb, inboxDb, null);

            string materializedDb = Path.Combine(root, "work", "shaderless.db");
            var scsGame = new Shaderless.Engine.Game("kcd2_sideload", kcd2.Name, Shaderless.Engine.Store.Epic, kcd2.InstallPath, kcd2.ExecutablePath);
            int count = Shaderless.Core.Games.RecorderManager.MaterializeStandaloneRecording(storeDb, scsGame, materializedDb);

            Assert.AreEqual(1, count);
            Assert.IsTrue(File.Exists(materializedDb));
            var outRecs = Shaderless.Engine.Planning.PsoDb.Read(materializedDb).ToList();
            Assert.IsTrue(outRecs.Any(r => r.Tag == 'B'));
            Assert.IsTrue(outRecs.Any(r => r.Tag == '1'));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Issue2_AmdMesa_NonSteam_InstallsVerifiesAndClears_PrefixMesaAndAmdCaches()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-amd-mesa-" + Guid.NewGuid().ToString("N"));
        try
        {
            string prefix = Path.Combine(root, "Prefixes", "KCD2");
            string gameId = "flatpak_kcd2_mesa";
            string targetCachePath = ProtonRunner.GetNonSteamShaderCachePath(gameId, prefix);
            string vkd3dCache = Path.Combine(targetCachePath, "VKD3D_shader_cache");
            string glCache = Path.Combine(targetCachePath, "nvidiav1", "GLCache");
            string mesaSfLeaf = Path.Combine(targetCachePath, "mesa_shader_cache_sf", "deadbeef01234567");
            string amdDir = Path.Combine(targetCachePath, "AMDv1");
            string dxvkDir = Path.Combine(targetCachePath, "DXVK_state_cache");

            Directory.CreateDirectory(vkd3dCache);
            Directory.CreateDirectory(glCache);
            Directory.CreateDirectory(mesaSfLeaf);
            Directory.CreateDirectory(amdDir);
            Directory.CreateDirectory(dxvkDir);

            // Simulate AMD Mesa RADV (mesa_shader_cache_db default for Heroic/Lutris) + AMDVLK + DXVK caches written by warmer
            string mesaDbLeaf = Path.Combine(targetCachePath, "mesa_shader_cache_db", "deadbeef01234567");
            Directory.CreateDirectory(mesaDbLeaf);
            File.WriteAllBytes(Path.Combine(mesaDbLeaf, "part0"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(mesaSfLeaf, "foz_cache.foz"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(mesaSfLeaf, "index"), new byte[128]);
            File.WriteAllBytes(Path.Combine(amdDir, "steamapp_shader_cache"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(dxvkDir, "KingdomCome.dxvk-cache"), new byte[1024]);

            var game = new GameInfo(
                Id: gameId,
                Name: "Kingdom Come: Deliverance II",
                Source: GameSource.Heroic,
                InstallPath: root,
                ExecutablePath: Path.Combine(root, "KingdomCome.exe"),
                PrefixPath: prefix,
                BannerPath: null,
                Engine: GameEngine.DirectX12,
                State: CacheState.NeedsCompilation,
                TotalShadersEstimated: 6000,
                CompiledShadersCount: 0,
                LastCompiledAt: null,
                IsFlatpak: true);

            ProtonRunner.InstallCachesWhereGameReads(game, vkd3dCache, glCache);
            ProtonRunner.EnsureProtonLocalShaderCacheSetting(prefix);

            string installedMesaDb = Path.Combine(prefix, "shadercache", "mesa_shader_cache_db", "deadbeef01234567", "part0");
            string installedFoz = Path.Combine(prefix, "shadercache", "mesa_shader_cache_sf", "deadbeef01234567", "foz_cache.foz");
            string installedAmd = Path.Combine(prefix, "shadercache", "AMDv1", "steamapp_shader_cache");
            string installedDxvk = Path.Combine(prefix, "shadercache", "DXVK_state_cache", "KingdomCome.dxvk-cache");
            string userSettings = Path.Combine(prefix, "user_settings.py");

            Assert.IsTrue(File.Exists(installedMesaDb));
            Assert.IsTrue(File.Exists(installedFoz));
            Assert.IsTrue(File.Exists(installedAmd));
            Assert.IsTrue(File.Exists(installedDxvk));
            Assert.IsTrue(File.Exists(userSettings));
            StringAssert.Contains(File.ReadAllText(userSettings), "STEAM_COMPAT_DATA_PATH");

            // Execute user_settings.py with real python3 under Heroic (/pfx & bare prefix) and Steam env
            static Dictionary<string, string> RunUserSettingsWithPython(string pyDir, string compatShaderPath, string compatDataPath)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = "-c \"import sys, json; sys.path.insert(0, sys.argv[1]); import user_settings; print(json.dumps(user_settings.user_settings))\" \"" + pyDir + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                psi.Environment["STEAM_COMPAT_SHADER_PATH"] = compatShaderPath;
                psi.Environment["STEAM_COMPAT_DATA_PATH"] = compatDataPath;
                using var proc = System.Diagnostics.Process.Start(psi)!;
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                Assert.AreEqual(0, proc.ExitCode, $"user_settings.py execution failed: {stderr}");
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stdout.Trim())!;
            }

            var heroicDict = RunUserSettingsWithPython(prefix, "", Path.Combine(prefix, "pfx"));
            Assert.AreEqual(Path.Combine(prefix, "shadercache"), heroicDict["MESA_SHADER_CACHE_DIR"]);
            Assert.AreEqual(Path.Combine(prefix, "shadercache", "AMDv1"), heroicDict["AMD_VK_PIPELINE_CACHE_PATH"]);
            Assert.AreEqual(Path.Combine(prefix, "shadercache", "VKD3D_shader_cache"), heroicDict["VKD3D_shader_cache".Replace("VKD3D_shader_cache", "VKD3D_SHADER_CACHE_PATH")]);
            Assert.IsFalse(heroicDict.ContainsKey("MESA_DISK_CACHE_SINGLE_FILE"), "Non-Steam Heroic/Lutris must NOT force MESA_DISK_CACHE_SINGLE_FILE=1");

            string steamSc = Path.Combine(root, "steam_sc", "1407200");
            Directory.CreateDirectory(steamSc);
            var steamDict = RunUserSettingsWithPython(prefix, steamSc, prefix);
            Assert.AreEqual(steamSc, steamDict["MESA_SHADER_CACHE_DIR"]);
            Assert.AreEqual("1", steamDict["MESA_DISK_CACHE_SINGLE_FILE"]);

            var verify = ProtonRunner.VerifyCacheInstalled(game, Path.Combine(root, "Steam"));
            Assert.IsTrue(verify.Verified, $"Expected AMD Mesa Heroic cache to verify, got: {verify.Detail}");

            var mesaBackend = new MesaBackend(GpuVendor.Amd);
            var (statsBytes, statsFiles, _) = await mesaBackend.GetGameCacheStatsAsync(gameId, GameSource.Heroic, prefix);
            Assert.AreEqual(2048 + 4096 + 128 + 2048 + 1024, statsBytes);
            Assert.AreEqual(5, statsFiles);

            await mesaBackend.ClearGameCacheAsync(gameId, GameSource.Heroic, prefix);
            Assert.IsFalse(File.Exists(installedMesaDb));
            Assert.IsFalse(File.Exists(installedFoz));
            Assert.IsFalse(File.Exists(installedAmd));
            Assert.IsFalse(File.Exists(installedDxvk));
            Assert.IsFalse(Directory.Exists(targetCachePath));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class MockCacheDriverBackend : IDriverBackend
    {
        public GpuVendor Vendor => GpuVendor.Nvidia;
        public Task<DriverInfo> GetDriverInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DriverInfo(
                GpuVendor.Nvidia, "Mock GPU", "NVIDIA", "615.00", "/tmp", 0, 0, 0, 0, 0, DriverCacheStatus.Healthy, null));

        public Task<(long Bytes, int Files, string Path)> GetGameCacheStatsAsync(string gameId, GameSource source, string? prefixPath = null) =>
            Task.FromResult((50L * 1024 * 1024, 2, "/tmp/shadercache/" + gameId));

        public Task ClearCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearGameCacheAsync(string gameId, GameSource source, string? prefixPath = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAllCachesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string GetRecommendedEnvironmentVariables() => "";
    }

    [TestMethod]
    public async Task Issue3_HonestCachedStatus_SteamOwnShaderCacheWithoutShaderlessArtifacts_RemainsNeedsCompilation()
    {
        string uncompiledId = "test_uncompiled_wot_" + Guid.NewGuid().ToString("N");
        string compiledId = "test_compiled_sl_" + Guid.NewGuid().ToString("N");

        var uncompiledGame = MakeGame(uncompiledId, GameSource.Steam, "/tmp/wot", "/tmp/wot/WorldOfTanks.exe");
        var compiledGame = MakeGame(compiledId, GameSource.Steam, "/tmp/compiled", "/tmp/compiled/Game.exe");

        try
        {
            // Seed Shaderless warmed-keys artifact ONLY for compiledGame
            string warmedPath = Shaderless.Core.Games.RecorderManager.WarmedKeysPath(compiledId);
            Directory.CreateDirectory(Path.GetDirectoryName(warmedPath)!);
            string protonStamp = ProtonRunner.GetProtonStamp(compiledGame);
            File.WriteAllText(warmedPath, $"#STAMP:615.00|{protonStamp}\n");

            var manager = new Shaderless.Core.Games.GameManager(new Shaderless.Core.Games.IGameSource[]
            {
                new StaticGameSource(uncompiledGame),
                new StaticGameSource(compiledGame)
            });

            var scanned = await manager.ScanAllGamesAsync(new MockCacheDriverBackend());
            var uncompiledResult = scanned.Single(g => g.Id == uncompiledId);
            var compiledResult = scanned.Single(g => g.Id == compiledId);

            // Uncompiled Steam game still shows Steam's 50 MB on-disk cache honestly, yet stays NeedsCompilation (never bogus "Cached")
            Assert.AreEqual(50L * 1024 * 1024, uncompiledResult.CacheSizeBytes);
            Assert.AreEqual(CacheState.NeedsCompilation, uncompiledResult.State);
            Assert.AreEqual(0, uncompiledResult.CompiledShadersCount);

            // Genuinely Shaderless-compiled game is Cached
            Assert.AreEqual(CacheState.Cached, compiledResult.State);
        }
        finally
        {
            string dir1 = Shaderless.Core.Games.RecorderManager.GameDir(uncompiledId);
            string dir2 = Shaderless.Core.Games.RecorderManager.GameDir(compiledId);
            try { if (Directory.Exists(dir1)) Directory.Delete(dir1, true); } catch { }
            try { if (Directory.Exists(dir2)) Directory.Delete(dir2, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Issue2_RealProtonWarmer_UnsupportedArchiveGame_CompilesRecordedD3D12PipelinesEndToEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-kcd2-e2e-" + Guid.NewGuid().ToString("N"));
        string gameId = "flatpak_kcd2_e2e_" + Guid.NewGuid().ToString("N");
        string workDir = Path.Combine(Shaderless.Core.AppPaths.DataDir, "work", gameId);
        string recGameDir = Shaderless.Core.Games.RecorderManager.GameDir(gameId);

        try
        {
            string kcd2Root = Path.Combine(root, "KingdomComeDeliverance2");
            string kcd2BinDir = Path.Combine(kcd2Root, "Bin", "Win64SharedMasterSteamPGO");
            string kcd2EngineDir = Path.Combine(kcd2Root, "Engine");
            string kcd2Prefix = Path.Combine(root, "Prefixes", "Kingdom Come Deliverance II");
            Directory.CreateDirectory(kcd2BinDir);
            Directory.CreateDirectory(kcd2EngineDir);
            Directory.CreateDirectory(kcd2Prefix);

            string kcd2Exe = Path.Combine(kcd2BinDir, "KingdomCome.exe");
            File.WriteAllBytes(kcd2Exe, new byte[32768]);
            File.WriteAllBytes(Path.Combine(kcd2BinDir, "WHGame.dll"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(kcd2EngineDir, "Engine.pak"), new byte[512]);
            File.WriteAllBytes(Path.Combine(kcd2EngineDir, "ShaderCache.pak"), new byte[512]);

            var kcd2Game = new GameInfo(
                Id: gameId,
                Name: "Kingdom Come: Deliverance II",
                Source: GameSource.Heroic,
                InstallPath: kcd2Root,
                ExecutablePath: kcd2Exe,
                PrefixPath: kcd2Prefix,
                BannerPath: null,
                Engine: GameEngine.DirectX12,
                State: CacheState.NeedsCompilation,
                TotalShadersEstimated: 6000,
                CompiledShadersCount: 0,
                LastCompiledAt: null,
                IsFlatpak: true);

            var backend = await DriverDetector.DetectActiveBackendAsync();
            var gm = new Shaderless.Core.Games.GameManager(new Shaderless.Core.Games.IGameSource[] { new StaticGameSource(kcd2Game) });
            var coordinator = new Shaderless.Core.Pipelines.ShaderCompilerCoordinator(gm, backend);

            // 1. Unrecorded unsupported-archive game must fail cleanly with actionable Record guidance (no crash, no fake Cached)
            var unrecordedSteps = new List<CompilationProgress>();
            await foreach (var step in coordinator.CompileGameShadersAsync(kcd2Game))
                unrecordedSteps.Add(step);

            var unrecordedFinal = unrecordedSteps.Last();
            Assert.IsTrue(unrecordedFinal.Failed);
            StringAssert.Contains(unrecordedFinal.ErrorMessage, "Record");
            Assert.IsFalse(ProtonRunner.HasShaderlessCompiledArtifacts(kcd2Game));

            // 2. Run full GPU compilation end-to-end using self-contained synthetic D3D12 Compute PSOs (zero host recording dependency!)
            if (ProtonRunner.FindWarmerBinary() == null || ProtonRunner.FindProtonBinaryForGame(kcd2Game) == null)
            {
                Assert.Inconclusive("[E2E] Skipping live Proton warmer run (no warmer binary or Proton runtime present on host).");
                return;
            }

            var (blobsByHash, chosenPsos) = BuildSyntheticD3D12ComputePsos(12);
            Assert.AreEqual(12, chosenPsos.Count);

            // Drop inbox shaderless.db right next to KingdomCome.exe (exact output of d3d12.dll recorder after gameplay)
            string inboxDb = Path.Combine(kcd2BinDir, "shaderless.db");
            var neededHashes = chosenPsos.SelectMany(p => Shaderless.Engine.Planning.Rehydrate.References([p])).Distinct().ToList();
            using (var fs = File.Create(inboxDb))
            {
                foreach (var h in neededHashes)
                    Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', blobsByHash[h]);
                foreach (var pso in chosenPsos)
                    Shaderless.Engine.Planning.PsoDb.Write(fs, pso.Tag, pso.Payload);
            }

            var e2eSteps = new List<CompilationProgress>();
            await foreach (var step in coordinator.CompileGameShadersAsync(kcd2Game))
            {
                e2eSteps.Add(step);
                Console.WriteLine($"[KCD2-E2E] {step.Percentage:F1}% | {step.ProcessedShaders}/{step.TotalShaders} | {step.CurrentAction}");
                Assert.IsFalse(step.Failed, $"End-to-end standalone recording compilation failed: {step.ErrorMessage}");
            }

            var finalStep = e2eSteps.Last();
            Assert.IsTrue(finalStep.IsComplete);
            Assert.AreEqual(100.0, finalStep.Percentage);

            // Inbox shaderless.db was imported into durable recording.db and rotated to 0 bytes
            Assert.AreEqual(0L, new FileInfo(inboxDb).Length);
            Assert.IsTrue(File.Exists(Shaderless.Core.Games.RecorderManager.RecordingPath(gameId)));

            // Verify compiled cache files landed inside the Heroic prefix and pass strict verification
            var verify = ProtonRunner.VerifyCacheInstalled(kcd2Game);
            Assert.IsTrue(verify.Verified, $"VerifyCacheInstalled failed after real Proton warmer run: {verify.Detail}");
            Assert.IsTrue(ProtonRunner.HasShaderlessCompiledArtifacts(kcd2Game));

            // Verify GameManager now reports the game as genuinely Cached with real compiled bytes on disk
            var scanned = await gm.ScanAllGamesAsync(backend);
            Assert.AreEqual(CacheState.Cached, scanned[0].State);
            Assert.IsTrue(scanned[0].CacheSizeBytes > 0, "Expected non-zero real GPU shader cache bytes on disk");
            Console.WriteLine($"[KCD2-E2E] SUCCESS! Compiled {chosenPsos.Count} real D3D12 PSOs -> {scanned[0].CacheSizeBytes:N0} bytes in Heroic prefix ({scanned[0].State}).");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
            try { if (Directory.Exists(recGameDir)) Directory.Delete(recGameDir, true); } catch { }
        }
    }

    private static (byte[] Bin, byte[] Toc) BuildSyntheticCdvnCache(byte driverTag, params (ulong K0, ulong K1, byte[] Payload)[] records)
    {
        byte[] header = new byte[32];
        // Real NVIDIA little-endian uint32 'NVDC' = 0x4e564443 -> bytes 'C','D','V','N'
        header[0] = (byte)'C';
        header[1] = (byte)'D';
        header[2] = (byte)'V';
        header[3] = (byte)'N';
        header[4] = 2; // version
        header[8] = driverTag;

        using var binMs = new MemoryStream();
        using var tocMs = new MemoryStream();
        binMs.Write(header);
        tocMs.Write(header);

        byte[] recHdr = new byte[32];
        byte[] tocEntry = new byte[24];
        foreach (var (k0, k1, payload) in records)
        {
            uint offset = (uint)binMs.Position;
            uint plen = (uint)payload.Length;

            // Real NVIDIA record header: magic 0x9846a19d at [0..3], payloadLen at [28..31]
            Array.Clear(recHdr);
            recHdr[0] = 0x9d;
            recHdr[1] = 0xa1;
            recHdr[2] = 0x46;
            recHdr[3] = 0x98;
            BitConverter.TryWriteBytes(recHdr.AsSpan(28, 4), plen);

            binMs.Write(recHdr);
            binMs.Write(payload);

            BitConverter.TryWriteBytes(tocEntry.AsSpan(0, 8), k0);
            BitConverter.TryWriteBytes(tocEntry.AsSpan(8, 8), k1);
            BitConverter.TryWriteBytes(tocEntry.AsSpan(16, 4), offset);
            BitConverter.TryWriteBytes(tocEntry.AsSpan(20, 4), plen);
            tocMs.Write(tocEntry);
        }

        return (binMs.ToArray(), tocMs.ToArray());
    }

    private static List<(ulong K0, ulong K1)> ReadTocKeys(string tocPath)
    {
        byte[] bytes = File.ReadAllBytes(tocPath);
        Assert.IsTrue(ProtonRunner.IsNvdcHeader(bytes), $"Expected valid NVDC/CDVN header in {tocPath}");
        var list = new List<(ulong K0, ulong K1)>();
        for (int pos = 32; pos + 24 <= bytes.Length; pos += 24)
        {
            list.Add((BitConverter.ToUInt64(bytes, pos), BitConverter.ToUInt64(bytes, pos + 8)));
        }
        return list;
    }

    [TestMethod]
    public void Issue1_Settings_MaxCacheSizeGb_PersistsClampsAndOverridesProtonSessionEnvInPython()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-issue1-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "settings.json");
        string? prevOverride = Shaderless.Core.SettingsManager.TestSettingsPathOverride;

        try
        {
            Shaderless.Core.SettingsManager.TestSettingsPathOverride = settingsPath;

            // 1. Default when missing is 10 GB
            var def = Shaderless.Core.SettingsManager.Load();
            Assert.AreEqual(10, def.EffectiveMaxCacheSizeGb);
            Assert.AreEqual(10L * 1024 * 1024 * 1024, def.MaxCacheSizeBytes);
            Assert.AreEqual("10G", def.MesaMaxCacheSize);

            // 2. Clamping bounds (min 5 GB, max 500 GB)
            Shaderless.Core.SettingsManager.Save(def with { MaxCacheSizeGb = 1 });
            Assert.AreEqual(5, Shaderless.Core.SettingsManager.Load().EffectiveMaxCacheSizeGb);

            Shaderless.Core.SettingsManager.Save(def with { MaxCacheSizeGb = 9999 });
            Assert.AreEqual(500, Shaderless.Core.SettingsManager.Load().EffectiveMaxCacheSizeGb);

            // 3. Save 25 GB and verify NvidiaBackend + MesaBackend environment recommendations
            Shaderless.Core.SettingsManager.Save(def with { MaxCacheSizeGb = 25 });
            var loaded = Shaderless.Core.SettingsManager.Load();
            Assert.AreEqual(25, loaded.EffectiveMaxCacheSizeGb);

            long expected25Bytes = 25L * 1024 * 1024 * 1024;
            string nvEnv = new NvidiaBackend().GetRecommendedEnvironmentVariables();
            string mesaEnv = new MesaBackend().GetRecommendedEnvironmentVariables();
            StringAssert.Contains(nvEnv, $"__GL_SHADER_DISK_CACHE_SIZE={expected25Bytes}");
            StringAssert.Contains(mesaEnv, "MESA_SHADER_CACHE_MAX_SIZE=25G");

            // 4. Execute generated user_settings.py under REAL python3 simulating Proton's g_session.init_session!
            // Steam pre-populates os.environ (and g_session.env) with 4 GB and without shaderless_part1..2.
            string shaderPath = Path.Combine(root, "shadercache", "2828710");
            string glLeaf = Path.Combine(shaderPath, "nvidiav1", "GLCache", "ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
            Directory.CreateDirectory(glLeaf);

            var (p1Bin, p1Toc) = BuildSyntheticCdvnCache(0x42, (1UL, 10UL, new byte[128]));
            var (p2Bin, p2Toc) = BuildSyntheticCdvnCache(0x42, (2UL, 20UL, new byte[128]));
            File.WriteAllBytes(Path.Combine(glLeaf, "shaderless_part1.bin"), p1Bin);
            File.WriteAllBytes(Path.Combine(glLeaf, "shaderless_part1.toc"), p1Toc);
            File.WriteAllBytes(Path.Combine(glLeaf, "shaderless_part2.bin"), p2Bin);
            File.WriteAllBytes(Path.Combine(glLeaf, "shaderless_part2.toc"), p2Toc);

            string compatDir = Path.Combine(root, "compatdata");
            Directory.CreateDirectory(compatDir);
            ProtonRunner.EnsureProtonLocalShaderCacheSetting(compatDir, null, loaded);

            string harnessPy = Path.Combine(root, "run_proton_harness.py");
            File.WriteAllText(harnessPy, """
import os, sys, json

class FakeSession:
    def __init__(self):
        self.env = dict(os.environ)

# Simulate Proton's __main__.g_session pre-populated from Steam's environment BEFORE importing user_settings
g_session = FakeSession()
sys.path.insert(0, sys.argv[1])
import user_settings as us

# Exact Proton g_session.init_session loop:
for key, value in us.user_settings.items():
    if key not in g_session.env:
        g_session.env[key] = value

print(json.dumps({
    "gl_size": g_session.env.get("__GL_SHADER_DISK_CACHE_SIZE"),
    "ro_names": g_session.env.get("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"),
    "mesa_size": g_session.env.get("MESA_SHADER_CACHE_MAX_SIZE"),
}))
""");

            var psi = new System.Diagnostics.ProcessStartInfo("python3")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(harnessPy);
            psi.ArgumentList.Add(compatDir);
            psi.Environment["STEAM_COMPAT_SHADER_PATH"] = shaderPath;
            psi.Environment["__GL_SHADER_DISK_CACHE_SIZE"] = "4294967296"; // Steam's old 4 GB value
            psi.Environment["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = "fossilize;steam_shader_cache;steamapp_merged_shader_cache";
            psi.Environment["MESA_SHADER_CACHE_MAX_SIZE"] = "4G";

            using var proc = System.Diagnostics.Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd().Trim();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            Assert.AreEqual(0, proc.ExitCode, $"python3 harness failed: {stderr}");

            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            Assert.AreEqual(expected25Bytes.ToString(), doc.RootElement.GetProperty("gl_size").GetString());
            Assert.AreEqual("25G", doc.RootElement.GetProperty("mesa_size").GetString());
            Assert.AreEqual(
                "steamapp_merged_shader_cache;shaderless_part1;shaderless_part2;fossilize;steam_shader_cache",
                doc.RootElement.GetProperty("ro_names").GetString());
        }
        finally
        {
            Shaderless.Core.SettingsManager.TestSettingsPathOverride = prevOverride;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void Issue1_NvidiaMultiPartPacker_CSharpAndPython_SplitsUnderCapDeduplicatesAndFixesCdvnEndianness()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-issue1-pack-" + Guid.NewGuid().ToString("N"));
        string shaderPath = Path.Combine(root, "steamapps", "shadercache", "2828710");
        string glLeaf = Path.Combine(shaderPath, "nvidiav1", "GLCache", "ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        Directory.CreateDirectory(glLeaf);

        try
        {
            // Cap each part at 700 bytes (~2 records of 256B payload + 32B recHdr + 32B fileHdr = 608B per part)
            const long cap = 700;
            byte[] pA = Enumerable.Repeat((byte)0xAA, 256).ToArray();
            byte[] pB = Enumerable.Repeat((byte)0xBB, 256).ToArray();
            byte[] pC = Enumerable.Repeat((byte)0xCC, 256).ToArray();
            byte[] pD = Enumerable.Repeat((byte)0xDD, 256).ToArray();
            byte[] pE = Enumerable.Repeat((byte)0xEE, 256).ToArray();
            byte[] pF = Enumerable.Repeat((byte)0xFF, 256).ToArray();

            // Simulate Valor Mortis scenario:
            // - Legacy oversized steamapp_merged_shader_cache (4 records > cap)
            // - Legacy 75%-duplicate steam_shader_cache (3 duplicate keys + 1 unique key #5)
            // - Runtime writable bucket steamapp_shader_cache0 (1 duplicate key + 1 brand-new key #6)
            var (legacyMergedBin, legacyMergedToc) = BuildSyntheticCdvnCache(
                0x77,
                (1, 100, pA),
                (2, 200, pB),
                (3, 300, pC),
                (4, 400, pD));
            var (legacySteamRoBin, legacySteamRoToc) = BuildSyntheticCdvnCache(
                0x77,
                (1, 100, pA),
                (2, 200, pB),
                (3, 300, pC),
                (5, 500, pE));
            var (wb0Bin, wb0Toc) = BuildSyntheticCdvnCache(
                0x77,
                (2, 200, pB),
                (6, 600, pF));

            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_merged_shader_cache.bin"), legacyMergedBin);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_merged_shader_cache.toc"), legacyMergedToc);
            File.WriteAllBytes(Path.Combine(glLeaf, "steam_shader_cache.bin"), legacySteamRoBin);
            File.WriteAllBytes(Path.Combine(glLeaf, "steam_shader_cache.toc"), legacySteamRoToc);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache0.bin"), wb0Bin);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache0.toc"), wb0Toc);

            ProtonRunner.PackNvidiaGlCacheDirectory(glLeaf, maxPartBytes: cap);

            // Duplicate steam_shader_cache and writable bucket0 must be compacted away
            Assert.IsFalse(File.Exists(Path.Combine(glLeaf, "steam_shader_cache.bin")));
            Assert.IsFalse(File.Exists(Path.Combine(glLeaf, "steamapp_shader_cache0.bin")));

            // 6 unique records (288B each) with 700B cap -> 2 records per file across merged + part1 + part2!
            string mergedBinPath = Path.Combine(glLeaf, "steamapp_merged_shader_cache.bin");
            string part1BinPath = Path.Combine(glLeaf, "shaderless_part1.bin");
            string part2BinPath = Path.Combine(glLeaf, "shaderless_part2.bin");
            Assert.IsTrue(File.Exists(mergedBinPath));
            Assert.IsTrue(File.Exists(part1BinPath));
            Assert.IsTrue(File.Exists(part2BinPath));

            foreach (var f in new[] { mergedBinPath, part1BinPath, part2BinPath })
            {
                long len = new FileInfo(f).Length;
                Assert.IsTrue(len <= cap, $"Part {Path.GetFileName(f)} length {len} exceeded cap {cap}!");
            }

            var allKeys = ReadTocKeys(Path.Combine(glLeaf, "steamapp_merged_shader_cache.toc"))
                .Concat(ReadTocKeys(Path.Combine(glLeaf, "shaderless_part1.toc")))
                .Concat(ReadTocKeys(Path.Combine(glLeaf, "shaderless_part2.toc")))
                .ToList();
            Assert.AreEqual(6, allKeys.Count);
            Assert.AreEqual(6, allKeys.Distinct().Count());

            // Now test REAL Python user_settings.py _merge_nv_runtime_buckets spilling into shaderless_part3 when game writes new shaders at runtime!
            byte[] pG = Enumerable.Repeat((byte)0x55, 256).ToArray();
            var (rtBin, rtToc) = BuildSyntheticCdvnCache(
                0x77,
                (1, 100, pA), // already in merged (must be skipped!)
                (5, 500, pE), // already in part2 (must be skipped!)
                (7, 700, pG)); // brand new -> since merged, part1, part2 are all at 608B (608+288 > 700), Python must create shaderless_part3!
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache2.bin"), rtBin);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache2.toc"), rtToc);

            string pyPath = Path.Combine(root, "user_settings.py");
            File.WriteAllText(pyPath, ProtonRunner.BuildProtonUserSettingsScript(new AppSettings(MaxCacheSizeGb: 25), maxNvidiaPartBytes: cap));

            var psi = new System.Diagnostics.ProcessStartInfo("python3")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(pyPath);
            psi.Environment["STEAM_COMPAT_SHADER_PATH"] = shaderPath;
            using (var proc = System.Diagnostics.Process.Start(psi)!)
            {
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit(10_000);
                Assert.AreEqual(0, proc.ExitCode, $"python3 user_settings.py failed: {err}");
            }

            Assert.IsFalse(File.Exists(Path.Combine(glLeaf, "steamapp_shader_cache2.bin")));
            string part3TocPath = Path.Combine(glLeaf, "shaderless_part3.toc");
            Assert.IsTrue(File.Exists(part3TocPath), "Python runtime merger should spill key #7 into shaderless_part3!");
            var part3Keys = ReadTocKeys(part3TocPath);
            Assert.AreEqual(1, part3Keys.Count);
            Assert.AreEqual((7UL, 700UL), part3Keys[0]);

            // Verify SumUniqueFileBytes deduplicates hard-linked files on Linux
            string hardLinkedCopy = Path.Combine(root, "hardlinked_part1.bin");
            var linkProc = System.Diagnostics.Process.Start("ln", new[] { part1BinPath, hardLinkedCopy })!;
            linkProc.WaitForExit(5_000);
            Assert.IsTrue(ProtonRunner.AreSameFileOnDisk(part1BinPath, hardLinkedCopy));
            var (uniqueSum, uniqueCount) = ProtonRunner.SumUniqueFileBytes(new[] { part1BinPath, hardLinkedCopy });
            Assert.AreEqual(new FileInfo(part1BinPath).Length, uniqueSum);
            Assert.AreEqual(1, uniqueCount);

            // Verify IsNvidiaCacheTruncated flags legacy >= 2.0 GB single-file cache (sparse 0-disk-usage test file)
            string truncSteamRoot = Path.Combine(root, "trunc_steam");
            string truncLeaf = Path.Combine(truncSteamRoot, "steamapps", "shadercache", "2828710", "nvidiav1", "GLCache", "a", "b");
            Directory.CreateDirectory(truncLeaf);
            string sparseMerged = Path.Combine(truncLeaf, "steamapp_merged_shader_cache.bin");
            using (var fs = File.Create(sparseMerged))
            {
                fs.SetLength(2_080_000_000L); // sparse file: uses 0 physical disk blocks on tmpfs/ext4/btrfs!
            }
            File.WriteAllBytes(Path.Combine(truncLeaf, "steamapp_merged_shader_cache.toc"), new byte[32]);

            var vmGame = MakeGame("2828710", GameSource.Steam, Path.Combine(truncSteamRoot, "steamapps", "common", "ValorMortis"), "/tmp/ValorMortis.exe");
            Assert.IsTrue(ProtonRunner.IsNvidiaCacheTruncated(vmGame, truncSteamRoot));

            // Multi-part cache under 1.85 GB per part is NOT flagged as truncated
            var healthyGame = MakeGame("2828710", GameSource.Steam, Path.Combine(root, "steamapps", "common", "ValorMortis"), "/tmp/ValorMortis.exe");
            Assert.IsFalse(ProtonRunner.IsNvidiaCacheTruncated(healthyGame, root));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void Issue1_MultiBatchDatabaseSplitter_PreservesAllTemplatesStateObjectsAndBlobs()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-issue1-batches-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var psos = new List<Shaderless.Engine.Planning.PsoDb.Rec>();

            string RegisterSyntheticBlob(string label)
            {
                byte[] body = System.Text.Encoding.ASCII.GetBytes(label);
                byte[] sha1 = System.Security.Cryptography.SHA1.HashData(body);
                string hex = Shaderless.Engine.Planning.PsoDb.Hex(sha1);
                byte[] payload = new byte[20 + body.Length];
                sha1.CopyTo(payload, 0);
                body.CopyTo(payload, 20);
                blobs[hex] = payload;
                return hex;
            }

            for (int i = 0; i < 6; i++)
            {
                string rs = RegisterSyntheticBlob($"synthetic-gfx-rs-{i}");
                string vs = RegisterSyntheticBlob($"synthetic-gfx-vs-{i}");
                string ps = RegisterSyntheticBlob($"synthetic-gfx-ps-{i}");
                byte[] streamPayload = Shaderless.Engine.Planning.PsoDb.Stream(
                    rs,
                    new Dictionary<int, string>
                    {
                        [1] = vs, // Vertex
                        [2] = ps  // Pixel
                    },
                    null,
                    4u,
                    [Shaderless.Engine.Planning.PsoDb.R16G16B16A16Float],
                    0u);
                psos.Add(new Shaderless.Engine.Planning.PsoDb.Rec('S', streamPayload));
            }

            for (int i = 6; i < 11; i++)
            {
                string rs = RegisterSyntheticBlob($"synthetic-compute-rs-{i}");
                string cs = RegisterSyntheticBlob($"synthetic-compute-cs-{i}");
                byte[] computePayload = Shaderless.Engine.Planning.PsoDb.Compute(rs, cs);
                psos.Add(new Shaderless.Engine.Planning.PsoDb.Rec('C', computePayload));
            }

            Assert.AreEqual(11, psos.Count);

            string recDb = Path.Combine(root, "shaderless.db");
            string genDb = Path.Combine(root, "shaderless_gen.db");

            // Put first 5 PSOs in shaderless.db and next 6 PSOs in shaderless_gen.db (plus a synthetic 'P' variant referencing recIdx 0)
            using (var fs = File.Create(recDb))
            {
                foreach (var h in psos.Take(5).SelectMany(p => Shaderless.Engine.Planning.Rehydrate.References([p])).Distinct())
                    Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', blobs[h]);
                foreach (var p in psos.Take(5))
                    Shaderless.Engine.Planning.PsoDb.Write(fs, p.Tag, p.Payload);
            }

            // Build a real 'P' plan variant record whose 20-byte template key points to psos[0] (which lives in Batch 0!):
            // Layout: templateKey[20] + rootSig[20] + count(u32=1) + stage(u32=1) + shaderHash[20] + keepLayout(u32=0xFFFFFFFF)
            var parsedPso0 = Shaderless.Engine.Planning.PsoDb.Parse(psos[0]);
            var (firstStage, firstShaderHash) = parsedPso0.Stages.First();
            byte[] pPayload = new byte[20 + 20 + 4 + 4 + 20 + 4];
            Convert.FromHexString(psos[0].Key).CopyTo(pPayload.AsSpan(0, 20));
            Convert.FromHexString(parsedPso0.Rs).CopyTo(pPayload.AsSpan(20, 20));
            BitConverter.TryWriteBytes(pPayload.AsSpan(40, 4), 1u);
            BitConverter.TryWriteBytes(pPayload.AsSpan(44, 4), (uint)firstStage);
            Convert.FromHexString(firstShaderHash).CopyTo(pPayload.AsSpan(48, 20));
            BitConverter.TryWriteBytes(pPayload.AsSpan(68, 4), 0xFFFFFFFFu);

            var pRefs = Shaderless.Engine.Planning.Rehydrate.References([new Shaderless.Engine.Planning.PsoDb.Rec('P', pPayload)]);

            using (var fs = File.Create(genDb))
            {
                foreach (var h in psos.Skip(5).SelectMany(p => Shaderless.Engine.Planning.Rehydrate.References([p])).Concat(pRefs).Distinct())
                    if (blobs.TryGetValue(h, out var b))
                        Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', b);
                foreach (var p in psos.Skip(5))
                    Shaderless.Engine.Planning.PsoDb.Write(fs, p.Tag, p.Payload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, 'P', pPayload);
            }

            Assert.AreEqual(12, Shaderless.Core.Games.RecorderManager.CountExecutableItems(recDb, genDb));

            var slices = Shaderless.Core.Games.RecorderManager.SplitDatabasesIntoBatches(
                recDb,
                genDb,
                Path.Combine(root, "batches"),
                maxItemsPerBatch: 5);

            // 12 items split by 5 -> 3 batches: 5, 5, 2
            Assert.AreEqual(3, slices.Count);
            Assert.AreEqual(5, slices[0].ExecutableItemCount);
            Assert.AreEqual(0, slices[0].SkipPrefixCount);
            Assert.AreEqual(5, slices[1].ExecutableItemCount);
            Assert.AreEqual(0, slices[1].SkipPrefixCount); // standalone G/C/S records need 0 external templates
            Assert.AreEqual(2, slices[2].ExecutableItemCount);
            Assert.AreEqual(1, slices[2].SkipPrefixCount); // 'P' variant in Batch 2 pulls in exact template psos[0] from Batch 0!

            foreach (var slice in slices)
            {
                var recs = Shaderless.Engine.Planning.PsoDb.Read(slice.BatchDbPath).ToList();
                var sliceBlobs = recs
                    .Where(r => r.Tag == 'B' && r.Payload.Length >= 20)
                    .Select(r => Shaderless.Engine.Planning.PsoDb.Hex(r.Payload.AsSpan(0, 20)))
                    .ToHashSet(StringComparer.Ordinal);
                var nonBlobs = recs.Where(r => r.Tag != 'B').ToList();
                Assert.AreEqual(slice.SkipPrefixCount + slice.ExecutableItemCount, nonBlobs.Count);

                var activeItems = nonBlobs.Skip(slice.SkipPrefixCount).ToList();
                var neededRefs = Shaderless.Engine.Planning.Rehydrate.References(activeItems);
                foreach (var reqHash in neededRefs)
                {
                    Assert.IsTrue(sliceBlobs.Contains(reqHash), $"Batch {slice.BatchDbPath} missing required blob {reqHash}!");
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Issue1_RealProtonAndNvidiaDriver_MultiBatchMultiPart_BothPartsLoadedAndHitByGpu()
    {
        var backend = await DriverDetector.DetectActiveBackendAsync();
        if (backend.Vendor != GpuVendor.Nvidia)
        {
            Assert.Inconclusive("[Issue1-E2E] Skipping NVIDIA live driver multi-part test on non-NVIDIA hardware.");
            return;
        }

        if (ProtonRunner.FindWarmerBinary() == null)
        {
            Assert.Inconclusive("[Issue1-E2E] Skipping live Proton multi-part test (missing warmer binary).");
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "shaderless-issue1-livegpu-" + Guid.NewGuid().ToString("N"));
        string gameId = "issue1_multipart_e2e_" + Guid.NewGuid().ToString("N");
        string workDir = Path.Combine(root, "work");
        string gameDir = Path.Combine(root, "Game");
        string prefixDir = Path.Combine(root, "Prefix");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(prefixDir);

        string exePath = Path.Combine(gameDir, "ValorTest.exe");
        File.WriteAllBytes(exePath, new byte[16384]);

        var testGame = new GameInfo(
            Id: gameId,
            Name: "Issue1 Multi-Part GPU Test",
            Source: GameSource.Heroic,
            InstallPath: gameDir,
            ExecutablePath: exePath,
            PrefixPath: prefixDir,
            BannerPath: null,
            Engine: GameEngine.UnrealEngine5,
            State: CacheState.NeedsCompilation,
            TotalShadersEstimated: 16,
            CompiledShadersCount: 0,
            LastCompiledAt: null);

        if (ProtonRunner.FindProtonBinaryForGame(testGame) == null)
        {
            try { Directory.Delete(root, true); } catch { }
            Assert.Inconclusive("[Issue1-E2E] Skipping live Proton multi-part test (missing Proton runtime on host).");
            return;
        }

        try
        {
            // Generate 16 self-contained synthetic D3D12 Compute PSOs with distinct shader hashes & constants
            // so NVIDIA compiles 16 distinct GPU shader records without requiring any host recording.db!
            var (blobsByHash, chosenPsos) = BuildSyntheticD3D12ComputePsos(16);
            Assert.AreEqual(16, chosenPsos.Count);

            string workDb = Path.Combine(workDir, "shaderless.db");
            void WriteFullWorkDb()
            {
                var needed = chosenPsos.SelectMany(p => Shaderless.Engine.Planning.Rehydrate.References([p])).Distinct().ToList();
                using var fs = File.Create(workDb);
                foreach (var h in needed)
                    Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', blobsByHash[h]);
                foreach (var p in chosenPsos)
                    Shaderless.Engine.Planning.PsoDb.Write(fs, p.Tag, p.Payload);
            }
            WriteFullWorkDb();

            // PASS 1: Run real multi-batch warmer (2 batches of 8 PSOs) with 4 KB maxNvidiaPartBytes
            // so real NVIDIA compiled compute shader records spill cleanly across contiguous shaderless_part1..N files!
            int halfBatch = Math.Max(4, chosenPsos.Count / 2);
            var pass1Events = new List<WarmerEvent>();
            bool pass1Ok = await ProtonRunner.RunWarmerAsync(
                testGame,
                workDir,
                ev => pass1Events.Add(ev),
                CancellationToken.None,
                isIncrementalRun: false,
                maxItemsPerBatch: halfBatch,
                maxNvidiaPartBytes: 4_096);

            Assert.IsTrue(pass1Ok, "Pass 1 multi-batch real Proton warmer failed!");
            var doneEv = pass1Events.LastOrDefault(e => e.Event == "done");
            Assert.IsNotNull(doneEv);
            Assert.AreEqual(chosenPsos.Count, doneEv.Total);

            string targetCache = Path.Combine(prefixDir, "shadercache", gameId);
            string glCache = Path.Combine(targetCache, "nvidiav1", "GLCache");
            var leafDir = Directory.GetFiles(glCache, "*.toc", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .FirstOrDefault(d => !string.IsNullOrEmpty(d));
            Assert.IsNotNull(leafDir, "Expected NVIDIA GLCache leaf directory with .toc files!");

            var partTocs = Directory.GetFiles(leafDir!, "shaderless_part*.toc")
                .OrderBy(ProtonRunner.TryParseShaderlessPartIndex)
                .ToList();
            Assert.IsTrue(partTocs.Count >= 1, "Expected real NVIDIA shader records to spill into at least shaderless_part1!");

            // Verify part numbering is 100% contiguous (shaderless_part1, shaderless_part2, ..., shaderless_partK)
            for (int idx = 0; idx < partTocs.Count; idx++)
            {
                Assert.AreEqual(idx + 1, ProtonRunner.TryParseShaderlessPartIndex(partTocs[idx]));
            }

            string mergedToc = Path.Combine(leafDir!, "steamapp_merged_shader_cache.toc");
            string mergedBin = Path.Combine(leafDir!, "steamapp_merged_shader_cache.bin");
            Assert.IsTrue(File.Exists(mergedToc) && File.Exists(mergedBin));

            // Verify real NVIDIA header begins with little-endian "CDVN" and zero duplicate keys exist across all parts
            byte[] realHdr = File.ReadAllBytes(mergedToc)[..32];
            Assert.AreEqual("CDVN", System.Text.Encoding.ASCII.GetString(realHdr, 0, 4));

            var pass1Keys = ReadTocKeys(mergedToc);
            foreach (var pt in partTocs)
                pass1Keys.AddRange(ReadTocKeys(pt));

            Assert.IsTrue(pass1Keys.Count >= 4, $"Expected multiple compiled NVIDIA shader entries, got {pass1Keys.Count}");
            Assert.AreEqual(pass1Keys.Count, pass1Keys.Distinct().Count(), "Multi-part cache must have zero duplicate shader keys!");

            string roEnv = ProtonRunner.BuildNvidiaReadOnlyAppNames(glCache);
            StringAssert.Contains(roEnv, "shaderless_part1");
            Console.WriteLine($"[Issue1-E2E] Pass 1 produced {1 + partTocs.Count} parts ({pass1Keys.Count} unique NVIDIA shader keys). RO={roEnv}");

            // PASS 2 (HARDWARE CACHE-HIT PROOF):
            // Move steamapp_merged_shader_cache into shaderless_part99 so ONLY shaderless_part1..99 exist on disk,
            // AND delete VKD3D's .cache files so VKD3D-Proton MUST call vkCreate*Pipelines in libnvidia-glcore.so for all 16 PSOs!
            int highPart = partTocs.Max(ProtonRunner.TryParseShaderlessPartIndex) + 1;
            File.Move(mergedBin, Path.Combine(leafDir!, $"shaderless_part{highPart}.bin"));
            File.Move(mergedToc, Path.Combine(leafDir!, $"shaderless_part{highPart}.toc"));
            Assert.IsFalse(File.Exists(mergedBin));

            foreach (var vf in Directory.GetFiles(targetCache, "*.cache*", SearchOption.AllDirectories))
                File.Delete(vf);
            string exeVkd3d = Path.Combine(gameDir, "vkd3d-proton.cache");
            if (File.Exists(exeVkd3d)) File.Delete(exeVkd3d);

            WriteFullWorkDb();
            bool pass2Ok = await ProtonRunner.RunWarmerAsync(
                testGame,
                workDir,
                _ => { },
                CancellationToken.None,
                isIncrementalRun: true,
                maxItemsPerBatch: 100,
                maxNvidiaPartBytes: ProtonRunner.DefaultMaxNvidiaPartBytes);
            Assert.IsTrue(pass2Ok, "Pass 2 incremental warmer run failed!");

            // Because libnvidia-glcore.so loaded ALL shaderless_part1..N via __GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME,
            // every single shader hit in the read-only multi-part files and ZERO shaders were recompiled!
            int recompiledMisses = File.Exists(mergedToc) ? ReadTocKeys(mergedToc).Count : 0;
            Console.WriteLine($"[Issue1-E2E] Pass 2 hardware cache misses across {pass1Keys.Count} shaders in shaderless_part1..{highPart}: {recompiledMisses}");
            Assert.AreEqual(0, recompiledMisses, "libnvidia-glcore.so should hit 100% of shaders from shaderless_part1..N with 0 cache misses!");
        }
        finally
        {
            // Also clean up any isolated Proton prefix created under ~/.local/share/shaderless/prefixes/<gameId>_*
            try
            {
                string prefixesRoot = Path.Combine(Shaderless.Core.AppPaths.DataDir, "prefixes");
                if (Directory.Exists(prefixesRoot))
                {
                    foreach (var d in Directory.GetDirectories(prefixesRoot, $"{gameId}*"))
                    {
                        try { Directory.Delete(d, true); } catch { }
                    }
                }
            }
            catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task GameExitMonitor_AutoRefreshesAndImportsOnExit_EvenWhenAutoCompileIsOff()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "shaderless-exitmon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");
        string? prevOverride = Shaderless.Core.SettingsManager.TestSettingsPathOverride;

        try
        {
            Shaderless.Core.SettingsManager.TestSettingsPathOverride = settingsPath;
            Shaderless.Core.SettingsManager.Save(new AppSettings(AutoRecordAllGames: false, AutoCompileOnExit: false));

            string exePath = Path.Combine(tempDir, "TestGame.exe");
            File.WriteAllBytes(exePath, new byte[64]);
            var game = MakeGame("exitmon_test_" + Guid.NewGuid().ToString("N"), GameSource.Heroic, tempDir, exePath);

            bool isRunning = true;
            int compileCalls = 0;
            var exitedSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (var monitor = new Shaderless.Core.Games.GameExitMonitor(
                () => new[] { game },
                onGameReadyToCompile: (_, _) =>
                {
                    Interlocked.Increment(ref compileCalls);
                    return Task.CompletedTask;
                },
                isGameRunning: _ => Volatile.Read(ref isRunning),
                onGameExited: (exited, _) =>
                {
                    exitedSignal.TrySetResult(exited.Id);
                    return Task.CompletedTask;
                }))
            {
                monitor.Start(pollInterval: TimeSpan.FromMilliseconds(35), settleDelay: TimeSpan.FromMilliseconds(35));
                await Task.Delay(110); // allow poll to observe game running
                Volatile.Write(ref isRunning, false);

                var completed = await Task.WhenAny(exitedSignal.Task, Task.Delay(3000));
                Assert.AreSame(exitedSignal.Task, completed, "Expected onGameExited UI refresh callback when game closes with AutoCompileOnExit=false.");
                Assert.AreEqual(game.Id, await exitedSignal.Task);
                Assert.AreEqual(0, compileCalls, "Should NOT auto-compile when AutoCompileOnExit is false.");
            }
        }
        finally
        {
            Shaderless.Core.SettingsManager.TestSettingsPathOverride = prevOverride;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task RecordingsLock_WaitsCleanlyOnLinuxContention_InsteadOfThrowingImmediately()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "shaderless-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string storePath = Path.Combine(tempDir, "recording.db");

        try
        {
            using var acquiredOnThread1 = new ManualResetEventSlim(false);
            using var releaseThread1 = new ManualResetEventSlim(false);

            var t1 = Task.Factory.StartNew(() =>
            {
                using (Shaderless.Engine.App.Recordings.Lock(storePath, TimeSpan.FromSeconds(3)))
                {
                    acquiredOnThread1.Set();
                    releaseThread1.Wait(TimeSpan.FromSeconds(5));
                }
            }, TaskCreationOptions.LongRunning);

            Assert.IsTrue(acquiredOnThread1.Wait(TimeSpan.FromSeconds(3)));

            var t2 = Task.Factory.StartNew(() =>
            {
                using (Shaderless.Engine.App.Recordings.Lock(storePath, TimeSpan.FromSeconds(3)))
                {
                    return true;
                }
            }, TaskCreationOptions.LongRunning);

            await Task.Delay(80);
            Assert.IsFalse(t2.IsCompleted, "Second holder must wait while first holder holds recording.db.lock.");

            releaseThread1.Set();
            await t1;
            Assert.IsTrue(await t2, "Second holder must acquire recording.db.lock cleanly on Linux once first holder releases it.");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void RecorderManager_Import_SkipsWhileGameIsRunning_OrFileOpenInProcFd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "shaderless-runimport-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(tempDir, "data");
        string gameDir = Path.Combine(tempDir, "game");
        string symlinkGameDir = Path.Combine(tempDir, "game-link");
        string fakeProcDir = Path.Combine(tempDir, "proc");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(fakeProcDir);
        File.CreateSymbolicLink(symlinkGameDir, gameDir);

        string? prevDataOverride = Shaderless.Core.Games.RecorderManager.TestDataDirOverride;
        try
        {
            Shaderless.Core.Games.RecorderManager.TestDataDirOverride = dataDir;

            string exePath = Path.Combine(gameDir, "ValorMortis-Win64-Shipping.exe");
            File.WriteAllBytes(exePath, new byte[64]);
            string inboxPath = Path.Combine(gameDir, "shaderless.db");

            var pso = new Shaderless.Engine.Planning.PsoDb.Rec('C', new byte[] { 11, 22, 33, 44 });
            using (var fs = File.Create(inboxPath))
            {
                Shaderless.Engine.Planning.PsoDb.Write(fs, pso.Tag, pso.Payload);
            }
            long initialInboxLen = new FileInfo(inboxPath).Length;
            Assert.IsTrue(initialInboxLen > 0);

            var game = MakeGame("2828710_test", GameSource.Steam, gameDir, exePath);

            // 1. Game is running -> Import must skip without touching shaderless.db or creating recording.db
            int importedWhileRunning = Shaderless.Core.Games.RecorderManager.Import(game, isGameRunning: _ => true);
            Assert.AreEqual(0, importedWhileRunning);
            Assert.AreEqual(initialInboxLen, new FileInfo(inboxPath).Length, "shaderless.db must not be truncated while game is running.");
            Assert.IsFalse(File.Exists(Shaderless.Core.Games.RecorderManager.RecordingPath(game.Id)));

            // 2. /proc/<pid>/fd open-handle detection (including symlinked parent directory path)
            Directory.CreateDirectory(Path.Combine(fakeProcDir, "100", "fd"));
            File.CreateSymbolicLink(Path.Combine(fakeProcDir, "100", "fd", "3"), inboxPath);
            Assert.IsFalse(
                Shaderless.Core.Games.RecorderManager.IsFileOpenByExternalProcess(inboxPath, fakeProcDir, selfPid: 100),
                "Shaderless's own PID must be ignored.");

            Directory.CreateDirectory(Path.Combine(fakeProcDir, "200", "fd"));
            File.CreateSymbolicLink(
                Path.Combine(fakeProcDir, "200", "fd", "7"),
                Path.Combine(symlinkGameDir, "shaderless.db"));
            Assert.IsTrue(
                Shaderless.Core.Games.RecorderManager.IsFileOpenByExternalProcess(inboxPath, fakeProcDir, selfPid: 100),
                "External process holding shaderless.db open (even via symlink path) must be detected.");

            // 3. Game exited -> Import merges cleanly and rotates inbox to 0 bytes
            int importedAfterExit = Shaderless.Core.Games.RecorderManager.Import(game, isGameRunning: _ => false);
            Assert.AreEqual(1, importedAfterExit);
            Assert.AreEqual(0L, new FileInfo(inboxPath).Length);
            Assert.AreEqual(1, Shaderless.Core.Games.RecorderManager.RecordedPsos(game));
        }
        finally
        {
            Shaderless.Core.Games.RecorderManager.TestDataDirOverride = prevDataOverride;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task GameExitMonitor_AutoCompiles_EvenWhenUiScanAlreadyImportedInbox_AndStaticWarmedKeysOutnumberRecorded()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "shaderless-exitrace-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(tempDir, "data");
        string gameDir = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(gameDir);

        string settingsPath = Path.Combine(tempDir, "settings.json");
        string? prevSettingsOverride = Shaderless.Core.SettingsManager.TestSettingsPathOverride;
        string? prevDataOverride = Shaderless.Core.Games.RecorderManager.TestDataDirOverride;

        try
        {
            Shaderless.Core.SettingsManager.TestSettingsPathOverride = settingsPath;
            Shaderless.Core.Games.RecorderManager.TestDataDirOverride = dataDir;
            Shaderless.Core.SettingsManager.Save(new AppSettings(AutoRecordAllGames: false, AutoCompileOnExit: true));

            string exePath = Path.Combine(gameDir, "ValorMortis-Win64-Shipping.exe");
            File.WriteAllBytes(exePath, new byte[64]);
            var cachedGame = MakeGame("2828710_race", GameSource.Steam, gameDir, exePath) with
            {
                State = CacheState.Cached,
                CompiledShadersCount = 10
            };

            // Simulate 2 recorded PSOs already merged into recording.db by a background UI scan (shaderless.db already emptied -> newlyImported == 0):
            // psoOld is warmed, psoNew is NOT warmed yet, while warmed-keys.txt has 10 keys total (static UE5 + old recorded) > 2 total recorded!
            var psoOld = new Shaderless.Engine.Planning.PsoDb.Rec('C', new byte[] { 1, 1, 1, 1 });
            var psoNew = new Shaderless.Engine.Planning.PsoDb.Rec('C', new byte[] { 2, 2, 2, 2 });
            string inboxPath = Path.Combine(gameDir, "shaderless.db");
            using (var fs = File.Create(inboxPath))
            {
                Shaderless.Engine.Planning.PsoDb.Write(fs, psoOld.Tag, psoOld.Payload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, psoNew.Tag, psoNew.Payload);
            }
            Assert.AreEqual(2, Shaderless.Core.Games.RecorderManager.Import(cachedGame, isGameRunning: _ => false));
            Assert.AreEqual(0L, new FileInfo(inboxPath).Length);

            var warmedSet = new List<string> { psoOld.Key };
            for (int i = 0; i < 9; i++)
                warmedSet.Add($"{i:x40}");
            Shaderless.Core.Games.RecorderManager.SaveWarmedKeys(cachedGame, warmedSet, "615.78.08|proton");

            Assert.AreEqual(2, Shaderless.Core.Games.RecorderManager.RecordedPsos(cachedGame));
            Assert.AreEqual(10, Shaderless.Core.Games.RecorderManager.LoadWarmedKeys(cachedGame.Id).Count);
            Assert.AreEqual(1, Shaderless.Core.Games.RecorderManager.UnwarmedRecordedPsos(cachedGame));

            bool isRunning = true;
            var compileSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (var monitor = new Shaderless.Core.Games.GameExitMonitor(
                () => new[] { cachedGame },
                onGameReadyToCompile: (exited, _) =>
                {
                    compileSignal.TrySetResult(exited.Id);
                    return Task.CompletedTask;
                },
                isGameRunning: _ => Volatile.Read(ref isRunning)))
            {
                monitor.Start(pollInterval: TimeSpan.FromMilliseconds(35), settleDelay: TimeSpan.FromMilliseconds(35));
                await Task.Delay(110);
                Volatile.Write(ref isRunning, false);

                var completed = await Task.WhenAny(compileSignal.Task, Task.Delay(3000));
                Assert.AreSame(compileSignal.Task, completed, "AutoCompileOnExit must trigger when UnwarmedRecordedPsos > 0 even if newlyImported == 0 and warmedKeys.Count > totalRecorded.");
                Assert.AreEqual(cachedGame.Id, await compileSignal.Task);
            }

            // Now mark psoNew as warmed too -> UnwarmedRecordedPsos becomes 0 -> exiting again must NOT re-trigger compile
            Shaderless.Core.Games.RecorderManager.SaveWarmedKeys(cachedGame, new[] { psoNew.Key }, "615.78.08|proton");
            Assert.AreEqual(0, Shaderless.Core.Games.RecorderManager.UnwarmedRecordedPsos(cachedGame));
            Assert.IsFalse(Shaderless.Core.Games.GameExitMonitor.ShouldAutoCompileAfterExit(cachedGame, newlyImported: 0));
        }
        finally
        {
            Shaderless.Core.SettingsManager.TestSettingsPathOverride = prevSettingsOverride;
            Shaderless.Core.Games.RecorderManager.TestDataDirOverride = prevDataOverride;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void PhantomBladeZeroLayout_ResolvesMainPlatformSteamExe_AndSyncsBaseDirVkd3dCache()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-pbz-" + Guid.NewGuid().ToString("N"));
        string installDir = Path.Combine(root, "PBZ");
        string engineWin64 = Path.Combine(installDir, "Engine", "Binaries", "Win64");
        string projWin64 = Path.Combine(installDir, "ProjectPBZ", "Binaries", "Win64");
        string steamStubDir = Path.Combine(installDir, "steam");
        string platformSteamDir = Path.Combine(installDir, "platform", "steam");
        string targetCache = Path.Combine(root, "shadercache");
        string vkd3dCache = Path.Combine(targetCache, "VKD3D_shader_cache");
        string glCache = Path.Combine(targetCache, "nvidiav1", "GLCache");

        Directory.CreateDirectory(engineWin64);
        Directory.CreateDirectory(projWin64);
        Directory.CreateDirectory(steamStubDir);
        Directory.CreateDirectory(platformSteamDir);
        Directory.CreateDirectory(vkd3dCache);
        Directory.CreateDirectory(glCache);

        try
        {
            File.WriteAllBytes(Path.Combine(engineWin64, "EpicWebHelper.exe"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(projWin64, "8za_v2.exe"), new byte[5 * 1024 * 1024]);
            File.WriteAllBytes(Path.Combine(projWin64, "CheckEnv.exe"), new byte[64 * 1024]);
            File.WriteAllBytes(Path.Combine(projWin64, "crashpad_handler.exe"), new byte[2 * 1024 * 1024]);

            string stubExe = Path.Combine(steamStubDir, "PBZ.exe");
            File.WriteAllBytes(stubExe, new byte[1 * 1024 * 1024]);

            string mainExe = Path.Combine(platformSteamDir, "PBZ.exe");
            using (var fs = File.Create(mainExe))
            {
                fs.SetLength(64L * 1024 * 1024);
            }

            // 1. FindExecutable & ResolveRealGameExecutable must pick platform/steam/PBZ.exe over 8za_v2.exe and steam/PBZ.exe
            string? found = Shaderless.Core.Games.GameEngineDetector.FindExecutable(installDir);
            Assert.AreEqual(mainExe, found);
            Assert.AreEqual(mainExe, Shaderless.Core.Games.GameEngineDetector.ResolveRealGameExecutable(installDir, stubExe));
            Assert.AreEqual(mainExe, Shaderless.Core.Games.GameEngineDetector.ResolveRealGameExecutable(installDir, Path.Combine(projWin64, "8za_v2.exe")));
            Assert.AreEqual(installDir, Shaderless.Core.Games.GameEngineDetector.InferInstallRoot(mainExe));

            // 2. RecorderManager.ExeDir must resolve to platform/steam so d3d12.dll is installed beside the 64-bit game binary
            var pbzGame = MakeGame("4115450", GameSource.Steam, installDir, stubExe);
            Assert.AreEqual(platformSteamDir, Shaderless.Core.Games.RecorderManager.ExeDir(pbzGame));

            // 3. FinalizeDriverCaches must promote runtime .cache.write in ProjectPBZ/Binaries/Win64 and sync vkd3d-proton.cache to BOTH platform/steam and ProjectPBZ/Binaries/Win64
            byte[] header = Enumerable.Range(1, 48).Select(i => (byte)i).ToArray();
            byte[] warmerPayload = new byte[] { 10, 20, 30, 40, 50, 60 };
            File.WriteAllBytes(Path.Combine(vkd3dCache, "vkd3d-proton.PBZ.exe.cache"), header.Concat(warmerPayload).ToArray());

            ProtonRunner.FinalizeDriverCaches(targetCache, vkd3dCache, glCache, platformSteamDir, ProtonRunner.DefaultMaxNvidiaPartBytes, installDir);

            Assert.IsTrue(File.Exists(Path.Combine(platformSteamDir, "vkd3d-proton.cache")), "vkd3d-proton.cache must be synced next to platform/steam/PBZ.exe.");
            Assert.IsTrue(File.Exists(Path.Combine(projWin64, "vkd3d-proton.cache")), "vkd3d-proton.cache must also be synced into ProjectPBZ/Binaries/Win64 (-basedir CWD).");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void FullCompile_KeepsExistingCache_WhenAskedAndRuntimeUnchanged_ButStillResetsOnProtonChange()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-preserve-" + Guid.NewGuid().ToString("N"));
        string targetCache = Path.Combine(root, "shadercache", "4115450");
        string vkd3dCache = Path.Combine(targetCache, "VKD3D_shader_cache");
        string glCache = Path.Combine(targetCache, "nvidiav1", "GLCache");
        string glLeaf = Path.Combine(glCache, "ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        string workDir = Path.Combine(root, "work");
        string gameExeDir = Path.Combine(root, "game", "platform", "steam");
        Directory.CreateDirectory(vkd3dCache);
        Directory.CreateDirectory(glLeaf);
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(gameExeDir);

        try
        {
            File.WriteAllBytes(Path.Combine(workDir, "d3d12core.dll"), new byte[100]);
            ProtonRunner.PrepareDriverCachesForWarmer(targetCache, vkd3dCache, glCache, "/proton-11.0/proton", workDir, isIncrementalRun: false, gameExeDir);

            // The compile from the game files left its cache (merged + parts) and a vkd3d cache
            string mergedBin = Path.Combine(glLeaf, "steamapp_merged_shader_cache.bin");
            string part1Bin = Path.Combine(glLeaf, "shaderless_part1.bin");
            string vkd3dFile = Path.Combine(vkd3dCache, "vkd3d-proton.PBZ.exe.cache");
            File.WriteAllBytes(mergedBin, new byte[2048]);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_merged_shader_cache.toc"), new byte[128]);
            File.WriteAllBytes(part1Bin, new byte[1024]);
            File.WriteAllBytes(Path.Combine(glLeaf, "shaderless_part1.toc"), new byte[64]);
            File.WriteAllBytes(vkd3dFile, new byte[256]);

            // A full compile planned from a new recording, same driver + Proton, asked to keep the cache: nothing deleted
            ProtonRunner.PrepareDriverCachesForWarmer(targetCache, vkd3dCache, glCache, "/proton-11.0/proton", workDir,
                isIncrementalRun: false, gameExeDir, preserveExistingCache: true);
            Assert.IsTrue(File.Exists(mergedBin), "the compile from the game files must survive a full compile asked to keep it");
            Assert.IsTrue(File.Exists(part1Bin));
            Assert.IsTrue(File.Exists(vkd3dFile));

            // Not asked to keep it: the old behaviour (full compiles never stack)
            ProtonRunner.PrepareDriverCachesForWarmer(targetCache, vkd3dCache, glCache, "/proton-11.0/proton", workDir,
                isIncrementalRun: false, gameExeDir, preserveExistingCache: false);
            Assert.IsFalse(File.Exists(mergedBin));

            // Asked to keep it, but Proton changed: the old cache can't match anymore, so it is reset
            File.WriteAllBytes(mergedBin, new byte[2048]);
            File.WriteAllBytes(part1Bin, new byte[1024]);
            File.WriteAllBytes(vkd3dFile, new byte[256]);
            ProtonRunner.PrepareDriverCachesForWarmer(targetCache, vkd3dCache, glCache, "/proton-cachyos/proton", workDir,
                isIncrementalRun: false, gameExeDir, preserveExistingCache: true);
            Assert.IsFalse(File.Exists(mergedBin), "a Proton change must reset even when asked to keep the cache");
            Assert.IsFalse(File.Exists(part1Bin));
            Assert.IsFalse(File.Exists(vkd3dFile));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void ShouldPreserveExistingCache_OnlyForFullCompilesOnTheSameRuntime_UnderTheSizeCap()
    {
        const long GB = 1024L * 1024L * 1024L;
        // PBZ: 13 GB compiled from the game files, 25 GB cap, then a compile planned from the recording (no overlap)
        Assert.IsTrue(ShaderCompilerCoordinator.ShouldPreserveExistingCache(false, null, 13 * GB, 25 * GB));
        // incremental compiles keep their cache through their own path
        Assert.IsFalse(ShaderCompilerCoordinator.ShouldPreserveExistingCache(true, null, 13 * GB, 25 * GB));
        // driver / Proton change or a truncated legacy cache: start clean
        Assert.IsFalse(ShaderCompilerCoordinator.ShouldPreserveExistingCache(false, "driver updated", 13 * GB, 25 * GB));
        Assert.IsFalse(ShaderCompilerCoordinator.ShouldPreserveExistingCache(false, "Proton updated", 13 * GB, 25 * GB));
        Assert.IsFalse(ShaderCompilerCoordinator.ShouldPreserveExistingCache(false, "cache truncated", 13 * GB, 25 * GB));
        // nothing to keep
        Assert.IsFalse(ShaderCompilerCoordinator.ShouldPreserveExistingCache(false, null, 0, 25 * GB));
        // already over 3/4 of the cap: start clean so the new compile fits
        Assert.IsFalse(ShaderCompilerCoordinator.ShouldPreserveExistingCache(false, null, 20 * GB, 25 * GB));
    }

    [TestMethod]
    public void PartialCompileNote_FollowsUpstreamPlannerCheck()
    {
        var none = Shaderless.Engine.AntiCheat.None;
        // no recording + an engine whose root-signature rule no game confirmed (Phantom Blade Zero): partial
        string? untested = ShaderCompilerCoordinator.PartialCompileNote(
            new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.Ready, Shaderless.Engine.Planning.Planner.Untested), none);
        StringAssert.Contains(untested, "Partial");
        StringAssert.Contains(untested, "Record");
        // a state-dependent cache without a recording (AMD): partial
        Assert.IsNotNull(ShaderCompilerCoordinator.PartialCompileNote(
            new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.NeedsRecording, Shaderless.Engine.Planning.Planner.Record), none));
        // planned from a recording, a confirmed engine, or DirectX 11 only: nothing guessed, no note
        Assert.IsNull(ShaderCompilerCoordinator.PartialCompileNote(
            new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.Ready, "planned from a recording"), none));
        Assert.IsNull(ShaderCompilerCoordinator.PartialCompileNote(
            new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.Ready, Shaderless.Engine.Planning.Planner.NoRecording), none));
        Assert.IsNull(ShaderCompilerCoordinator.PartialCompileNote(
            new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.Ready, "compiles every DirectX 11 shader"), none));
        Assert.IsNull(ShaderCompilerCoordinator.PartialCompileNote(null, none));
        // anti-cheat: recording isn't possible, so the note doesn't tell the user to record
        string? ac = ShaderCompilerCoordinator.PartialCompileNote(
            new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.Ready, Shaderless.Engine.Planning.Planner.Untested), Shaderless.Engine.AntiCheat.EasyAntiCheat);
        StringAssert.Contains(ac, "anti-cheat");
        Assert.IsFalse(ac!.Contains("Turn on"));
    }

    [TestMethod]
    public void CompileNote_RoundTrips_AndIsRemovedWhenNull()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "shaderless-note-" + Guid.NewGuid().ToString("N"));
        Shaderless.Core.Games.RecorderManager.TestDataDirOverride = dataDir;
        try
        {
            Assert.IsNull(Shaderless.Core.Games.RecorderManager.LoadCompileNote("6"));
            Shaderless.Core.Games.RecorderManager.SaveCompileNote("6", "Partial: compiled from the game's files only.");
            Assert.AreEqual("Partial: compiled from the game's files only.", Shaderless.Core.Games.RecorderManager.LoadCompileNote("6"));
            Shaderless.Core.Games.RecorderManager.SaveCompileNote("6", null);   // a later compile from a recording clears it
            Assert.IsNull(Shaderless.Core.Games.RecorderManager.LoadCompileNote("6"));
            Assert.IsFalse(File.Exists(Shaderless.Core.Games.RecorderManager.CompileNotePath("6")));
        }
        finally
        {
            Shaderless.Core.Games.RecorderManager.TestDataDirOverride = null;
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true); } catch { }
        }
    }

    [TestMethod]
    public void LutrisProtonSwitch_DetectedImmediatelyFromYaml_AndIsolatesPerGameInSharedEpicPrefix()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-lutris-proton-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string xdgData = Path.Combine(home, ".local", "share");
        string xdgConfig = Path.Combine(home, ".config");

        string lutrisData = Path.Combine(xdgData, "lutris");
        string lutrisGames = Path.Combine(lutrisData, "games");
        string lutrisRunners = Path.Combine(lutrisData, "runners");
        string steamCompat = Path.Combine(xdgData, "Steam", "compatibilitytools.d");
        string sharedEpicPrefix = Path.Combine(root, "Games", "epic-games-store");

        Directory.CreateDirectory(lutrisGames);
        Directory.CreateDirectory(lutrisRunners);
        Directory.CreateDirectory(steamCompat);
        Directory.CreateDirectory(sharedEpicPrefix);

        try
        {
            string CreateFakeProton(string baseDir, string name)
            {
                string dir = Path.Combine(baseDir, name);
                Directory.CreateDirectory(dir);
                string bin = Path.Combine(dir, "proton");
                File.WriteAllText(bin, "#!/usr/bin/env python3\n");
                return bin;
            }

            string umu10 = CreateFakeProton(steamCompat, "UMU-Proton-10.0-4");
            string ge11_7 = CreateFakeProton(steamCompat, "GE-Proton11-7-x86_64");
            string ge11_11 = CreateFakeProton(steamCompat, "GE-Proton11-11-x86_64");
            string cachySlr = CreateFakeProton(steamCompat, "proton-cachyos-slr");
            string dwProton = CreateFakeProton(Path.Combine(lutrisRunners, "wine"), "dw-proton-x86_64");

            // Stale config_info inside the shared Epic prefix still points to GE-Proton11-7-x86_64
            File.WriteAllText(
                Path.Combine(sharedEpicPrefix, "config_info"),
                $"11.0\n{Path.GetDirectoryName(ge11_7)}/files/share/default_pfx/\n");

            // Create Lutris pga.db with 3 Epic games sharing the exact same Wine prefix
            string dbPath = Path.Combine(lutrisData, "pga.db");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE games (
                        id INTEGER PRIMARY KEY,
                        slug TEXT,
                        name TEXT,
                        runner TEXT,
                        directory TEXT,
                        installed INTEGER,
                        service TEXT,
                        service_id TEXT,
                        configpath TEXT
                    );
                    INSERT INTO games VALUES (2, 'end-of-abyss', 'End of Abyss', 'wine', $pfx, 1, 'egs', 'eoa', 'end-of-abyss-100');
                    INSERT INTO games VALUES (3, 'arc-raiders', 'ARC Raiders', 'wine', $pfx, 1, 'egs', 'arc', 'arc-raiders-200');
                    INSERT INTO games VALUES (6, 'phantom-blade-zero', 'Phantom Blade Zero', 'wine', $pfx, 1, 'egs', 'pbz', 'phantom-blade-zero-300');
                    """;
                cmd.Parameters.AddWithValue("$pfx", sharedEpicPrefix);
                cmd.ExecuteNonQuery();
            }

            // Global Lutris Wine runner default: UMU-Proton-10.0-4
            File.WriteAllText(Path.Combine(lutrisRunners, "wine.yml"), """
                system:
                  env:
                    PROTON_DXVK_LOWLATENCY: '1'
                wine:
                  version: UMU-Proton-10.0-4
                """);

            // Game 2 has NO per-game wine: block -> inherits UMU-Proton-10.0-4 from runners/wine.yml (ignores stale config_info!)
            File.WriteAllText(Path.Combine(lutrisGames, "end-of-abyss-100.yml"), """
                game:
                  prefix: /shared
                name: End of Abyss
                """);

            // Game 3 explicitly uses GE-Proton11-7-x86_64
            File.WriteAllText(Path.Combine(lutrisGames, "arc-raiders-200.yml"), """
                wine:
                  version: GE-Proton11-7-x86_64
                """);

            // Game 6 explicitly uses GE-Proton11-11-x86_64 and overrides PROTON_DXVK_LOWLATENCY to '' while setting PROTON_VKD3D_LOWLATENCY to '1'
            string pbzYml = Path.Combine(lutrisGames, "phantom-blade-zero-300.yml");
            File.WriteAllText(pbzYml, """
                system:
                  disable_runtime: true
                  env:
                    PROTON_DXVK_LOWLATENCY: ''
                    PROTON_VKD3D_LOWLATENCY: '1'
                wine:
                  version: GE-Proton11-11-x86_64
                """);

            var g2 = MakeGame("2", GameSource.Lutris, "/games/eoa", "/games/eoa/EOA.exe") with { PrefixPath = sharedEpicPrefix };
            var g3 = MakeGame("3", GameSource.Lutris, "/games/arc", "/games/arc/ARC.exe") with { PrefixPath = sharedEpicPrefix };
            var g6 = MakeGame("6", GameSource.Lutris, "/games/pbz", "/games/pbz/PBZ.exe") with { PrefixPath = sharedEpicPrefix };

            Assert.AreEqual(umu10, ProtonRunner.ResolveLutrisProtonForGame(g2, null, home, xdgData, xdgConfig, allowSystemCompatTools: false));
            Assert.AreEqual(ge11_7, ProtonRunner.ResolveLutrisProtonForGame(g3, null, home, xdgData, xdgConfig, allowSystemCompatTools: false));
            Assert.AreEqual(ge11_11, ProtonRunner.ResolveLutrisProtonForGame(g6, null, home, xdgData, xdgConfig, allowSystemCompatTools: false));

            // Verify Lutris system.env low-latency cascade (game-level '' clears runner-level '1', while game-level '1' enables VKD3D low-latency)
            Assert.IsTrue(ProtonRunner.IsLowLatencyConfigured(g2, "PROTON_DXVK_LOWLATENCY", home, xdgData, xdgConfig));
            Assert.IsFalse(ProtonRunner.IsLowLatencyConfigured(g6, "PROTON_DXVK_LOWLATENCY", home, xdgData, xdgConfig));
            Assert.IsTrue(ProtonRunner.IsLowLatencyConfigured(g6, "PROTON_VKD3D_LOWLATENCY", home, xdgData, xdgConfig));

            // Capture live GUI watcher signature BEFORE switching Proton in Lutris
            string sigBefore = ProtonRunner.GetLauncherProtonConfigSignature(home, xdgData, xdgConfig);

            // User switches Game 6 in Lutris UI to proton-cachyos-slr WITHOUT launching the game (shared config_info still has GE-Proton11-7!)
            File.WriteAllText(pbzYml, """
                wine:
                  version: proton-cachyos-slr
                """);

            string sigAfter = ProtonRunner.GetLauncherProtonConfigSignature(home, xdgData, xdgConfig);
            Assert.AreNotEqual(sigBefore, sigAfter, "Live GUI watcher signature must change immediately when a Lutris game YAML is saved.");
            Assert.AreEqual(cachySlr, ProtonRunner.ResolveLutrisProtonForGame(g6, null, home, xdgData, xdgConfig, allowSystemCompatTools: false));
            Assert.AreEqual(umu10, ProtonRunner.ResolveLutrisProtonForGame(g2, null, home, xdgData, xdgConfig, allowSystemCompatTools: false));
            Assert.AreEqual(ge11_7, ProtonRunner.ResolveLutrisProtonForGame(g3, null, home, xdgData, xdgConfig, allowSystemCompatTools: false));

            // Verify Lutris's own runners/wine/<version> (`dw-proton-x86_64`) and `ge-proton` natural sort (`11-11` > `11-7`)
            File.WriteAllText(pbzYml, "wine:\n  version: dw-proton-x86_64\n");
            Assert.AreEqual(dwProton, ProtonRunner.ResolveLutrisProtonForGame(g6, null, home, xdgData, xdgConfig, allowSystemCompatTools: false));

            File.WriteAllText(pbzYml, "wine:\n  version: ge-proton\n");
            Assert.AreEqual(ge11_11, ProtonRunner.ResolveLutrisProtonForGame(g6, null, home, xdgData, xdgConfig, allowSystemCompatTools: false));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static (ulong K0, ulong K1, byte[] Payload) MakeNvdictRecord(byte driverTag, uint dictId, int payloadBytes = 256)
    {
        byte[] k0Bytes = new byte[8];
        k0Bytes[0] = driverTag;
        ulong k0 = BitConverter.ToUInt64(k0Bytes);
        ulong k1 = BitConverter.ToUInt64("_NVDICT_"u8);

        byte[] payload = new byte[Math.Max(payloadBytes, 64)];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), 0xEC30A437u); // ZSTD_MAGIC_DICTIONARY
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), dictId);
        for (int i = 8; i < payload.Length; i++)
            payload[i] = (byte)((dictId + i) & 0xFF);

        return (k0, k1, payload);
    }

    private static (ulong K0, ulong K1, byte[] Payload) MakeZstdShaderRecord(
        ulong k0,
        ulong k1,
        uint dictId,
        bool singleSegment = false,
        int payloadBytes = 192)
    {
        byte[] payload = new byte[Math.Max(payloadBytes, 64)];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), 4096u); // Uncompressed shader size
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), 0xFD2FB528u); // ZSTD_MAGICNUMBER
        if (singleSegment)
        {
            payload[8] = 0x23; // Single_Segment_Flag=1, Dictionary_ID_Flag=3 (4B)
            BitConverter.TryWriteBytes(payload.AsSpan(9, 4), dictId);
        }
        else
        {
            payload[8] = 0x03; // Single_Segment_Flag=0, Dictionary_ID_Flag=3 (4B)
            payload[9] = 0x58; // Window_Descriptor
            BitConverter.TryWriteBytes(payload.AsSpan(10, 4), dictId);
        }
        return (k0, k1, payload);
    }

    [TestMethod]
    public void NvidiaNvdictCollision_ShaderlessPriorityAndDictFilter_InCSharpAndPythonUserSettingsV6()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-nvdict-" + Guid.NewGuid().ToString("N"));
        string prefixDir = Path.Combine(root, "Games", "epic-games-store");
        string sharedGlRoot = Path.Combine(prefixDir, "GLCache");
        string oldDriverLeaf = Path.Combine(sharedGlRoot, "8d0a936d3f370e8143e56d2fcd10392e", "ca212eac8b1e670f");
        string curDriverLeaf = Path.Combine(sharedGlRoot, "ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        string compatShaderRoot = Path.Combine(prefixDir, "shadercache");
        string compatGlLeaf = Path.Combine(compatShaderRoot, "nvidiav1", "GLCache", "ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");

        Directory.CreateDirectory(oldDriverLeaf);
        Directory.CreateDirectory(curDriverLeaf);
        Directory.CreateDirectory(compatGlLeaf);

        try
        {
            const uint shaderlessDict = 0x45f88685u;
            const uint staleEpicDict = 0x1bc15c92u;
            const uint oldDriverDict = 0xdeadbeefu;

            // Older driver folder (must NOT poison active dictionary selection!)
            var (oldBin, oldToc) = BuildSyntheticCdvnCache(0x11, MakeNvdictRecord(0x11, oldDriverDict));
            string oldBinPath = Path.Combine(oldDriverLeaf, "steamapp_merged_shader_cache.bin");
            string oldTocPath = Path.Combine(oldDriverLeaf, "steamapp_merged_shader_cache.toc");
            File.WriteAllBytes(oldBinPath, oldBin);
            File.WriteAllBytes(oldTocPath, oldToc);
            File.SetLastWriteTimeUtc(oldBinPath, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(oldTocPath, DateTime.UtcNow.AddDays(-10));

            // Exact Phantom Blade Zero live GLCache layout:
            // - steamapp_merged_shader_cache: _NVDICT_ = 0x45f88685
            // - shaderless_part1 & part2: spill parts without _NVDICT_, Zstd shader frames = 0x45f88685
            // - shaderless_part5: orphaned runtime merge without _NVDICT_, Zstd shader frames = 0x1bc15c92
            // - steam_shader_cache: older launcher cache with _NVDICT_ = 0x1bc15c92
            var (mergedBin, mergedToc) = BuildSyntheticCdvnCache(
                0x59,
                MakeNvdictRecord(0x59, shaderlessDict),
                MakeZstdShaderRecord(10, 100, shaderlessDict, singleSegment: false));
            var (p1Bin, p1Toc) = BuildSyntheticCdvnCache(
                0x59,
                MakeZstdShaderRecord(20, 200, shaderlessDict, singleSegment: false));
            var (p2Bin, p2Toc) = BuildSyntheticCdvnCache(
                0x59,
                MakeZstdShaderRecord(30, 300, shaderlessDict, singleSegment: true));
            var (p5OrphanBin, p5OrphanToc) = BuildSyntheticCdvnCache(
                0x59,
                MakeZstdShaderRecord(50, 500, staleEpicDict, singleSegment: false));
            var (steamRoBin, steamRoToc) = BuildSyntheticCdvnCache(
                0x59,
                MakeNvdictRecord(0x59, staleEpicDict),
                MakeZstdShaderRecord(60, 600, staleEpicDict, singleSegment: false));

            File.WriteAllBytes(Path.Combine(curDriverLeaf, "steamapp_merged_shader_cache.bin"), mergedBin);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "steamapp_merged_shader_cache.toc"), mergedToc);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "shaderless_part1.bin"), p1Bin);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "shaderless_part1.toc"), p1Toc);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "shaderless_part2.bin"), p2Bin);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "shaderless_part2.toc"), p2Toc);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "shaderless_part5.bin"), p5OrphanBin);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "shaderless_part5.toc"), p5OrphanToc);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "steam_shader_cache.bin"), steamRoBin);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "steam_shader_cache.toc"), steamRoToc);

            // Verify C# dictionary extraction on both _NVDICT_ and Zstd frame headers (singleSegment false & true)
            Assert.AreEqual(shaderlessDict, ProtonRunner.TryGetNvidiaCacheDictId(
                Path.Combine(curDriverLeaf, "steamapp_merged_shader_cache.bin"),
                Path.Combine(curDriverLeaf, "steamapp_merged_shader_cache.toc")));
            Assert.AreEqual(shaderlessDict, ProtonRunner.TryGetNvidiaCacheDictId(
                Path.Combine(curDriverLeaf, "shaderless_part1.bin"),
                Path.Combine(curDriverLeaf, "shaderless_part1.toc")));
            Assert.AreEqual(shaderlessDict, ProtonRunner.TryGetNvidiaCacheDictId(
                Path.Combine(curDriverLeaf, "shaderless_part2.bin"),
                Path.Combine(curDriverLeaf, "shaderless_part2.toc")));
            Assert.AreEqual(staleEpicDict, ProtonRunner.TryGetNvidiaCacheDictId(
                Path.Combine(curDriverLeaf, "shaderless_part5.bin"),
                Path.Combine(curDriverLeaf, "shaderless_part5.toc")));
            Assert.AreEqual(staleEpicDict, ProtonRunner.TryGetNvidiaCacheDictId(
                Path.Combine(curDriverLeaf, "steam_shader_cache.bin"),
                Path.Combine(curDriverLeaf, "steam_shader_cache.toc")));

            // Verify C# BuildNvidiaReadOnlyAppNames puts Shaderless first and excludes BOTH steam_shader_cache AND orphaned shaderless_part5!
            string csRo = ProtonRunner.BuildNvidiaReadOnlyAppNames(sharedGlRoot, "fossilize;steam_shader_cache;steamapp_merged_shader_cache");
            Assert.AreEqual("steamapp_merged_shader_cache;shaderless_part1;shaderless_part2;fossilize", csRo);

            // Also drop a mismatched runtime bucket0 (0x1bc15c92) + a compatible runtime bucket1 (0x45f88685)
            // and run REAL python3 user_settings.py v6 under Lutris environment variables!
            var (badWbBin, badWbToc) = BuildSyntheticCdvnCache(0x59, MakeZstdShaderRecord(70, 700, staleEpicDict));
            var (goodWbBin, goodWbToc) = BuildSyntheticCdvnCache(0x59, MakeZstdShaderRecord(80, 800, shaderlessDict));
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "steamapp_shader_cache0.bin"), badWbBin);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "steamapp_shader_cache0.toc"), badWbToc);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "steamapp_shader_cache1.bin"), goodWbBin);
            File.WriteAllBytes(Path.Combine(curDriverLeaf, "steamapp_shader_cache1.toc"), goodWbToc);

            string protonDir = Path.Combine(root, "GE-Proton11-11-x86_64");
            Directory.CreateDirectory(protonDir);
            File.WriteAllText(Path.Combine(protonDir, "user_settings.py"), ProtonRunner.BuildProtonUserSettingsScript(new AppSettings(MaxCacheSizeGb: 25)));

            string harnessPy = Path.Combine(root, "run_lutris_proton.py");
            File.WriteAllText(harnessPy, """
import os, sys, json

class FakeSession:
    def __init__(self):
        self.env = dict(os.environ)

g_session = FakeSession()
sys.path.insert(0, sys.argv[1])
import user_settings as us
for key, value in us.user_settings.items():
    if key not in g_session.env:
        g_session.env[key] = value

print(json.dumps({
    "gl_path": g_session.env.get("__GL_SHADER_DISK_CACHE_PATH"),
    "ro_names": g_session.env.get("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"),
}))
""");

            var psi = new System.Diagnostics.ProcessStartInfo("python3")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(harnessPy);
            psi.ArgumentList.Add(protonDir);
            psi.Environment["STEAM_COMPAT_DATA_PATH"] = prefixDir;
            psi.Environment["__GL_SHADER_DISK_CACHE_PATH"] = prefixDir;
            psi.Environment["GAME_NAME"] = "Phantom Blade Zero";

            using var proc = System.Diagnostics.Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd().Trim();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            Assert.AreEqual(0, proc.ExitCode, $"python3 harness failed: {stderr}");

            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            Assert.AreEqual(prefixDir, doc.RootElement.GetProperty("gl_path").GetString());
            Assert.AreEqual(
                "steamapp_merged_shader_cache;shaderless_part1;shaderless_part2",
                doc.RootElement.GetProperty("ro_names").GetString());

            // Orphaned mismatched shaderless_part5 AND mismatched bucket0 must be removed, while compatible key (80,800) merged cleanly!
            Assert.IsFalse(File.Exists(Path.Combine(curDriverLeaf, "shaderless_part5.bin")), "Mismatched-dict shaderless_part5 must be purged during merge.");
            Assert.IsFalse(File.Exists(Path.Combine(curDriverLeaf, "steamapp_shader_cache0.bin")));
            Assert.IsFalse(File.Exists(Path.Combine(curDriverLeaf, "steamapp_shader_cache1.bin")));

            var mergedKeys = ReadTocKeys(Path.Combine(curDriverLeaf, "steamapp_merged_shader_cache.toc"));
            CollectionAssert.Contains(mergedKeys, (80UL, 800UL), "Compatible runtime shader key (80,800) must merge into active 0x45f88685 cache.");
            CollectionAssert.DoesNotContain(mergedKeys, (70UL, 700UL), "Incompatible 0x1bc15c92 runtime shader key (70,700) must NEVER merge into 0x45f88685 cache.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void NvidiaNvdictSeed_ExtractsDominantDictionaryBeforeFullReset_AndCleansUnusedSeed()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-nvseed-" + Guid.NewGuid().ToString("N"));
        string targetCache = Path.Combine(root, "shadercache", "4115450");
        string vkd3dCache = Path.Combine(targetCache, "VKD3D_shader_cache");
        string glCache = Path.Combine(targetCache, "nvidiav1", "GLCache");
        string relLeaf = Path.Combine("ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        string glLeaf = Path.Combine(glCache, relLeaf);
        string workDir = Path.Combine(root, "work");
        Directory.CreateDirectory(vkd3dCache);
        Directory.CreateDirectory(glLeaf);
        Directory.CreateDirectory(workDir);

        try
        {
            const uint dominantDict = 0x45f88685u;
            const uint smallerDict = 0x1bc15c92u;

            // Smaller steam_shader_cache (smallerDict) + larger steamapp_merged_shader_cache + part1 (dominantDict)
            var (steamBin, steamToc) = BuildSyntheticCdvnCache(0x59, MakeNvdictRecord(0x59, smallerDict, 128));
            var (mergedBin, mergedToc) = BuildSyntheticCdvnCache(
                0x59,
                MakeNvdictRecord(0x59, dominantDict, 256),
                MakeZstdShaderRecord(1, 10, dominantDict, payloadBytes: 512));
            var (p1Bin, p1Toc) = BuildSyntheticCdvnCache(
                0x59,
                MakeZstdShaderRecord(2, 20, dominantDict, payloadBytes: 1024));

            File.WriteAllBytes(Path.Combine(glLeaf, "steam_shader_cache.bin"), steamBin);
            File.WriteAllBytes(Path.Combine(glLeaf, "steam_shader_cache.toc"), steamToc);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_merged_shader_cache.bin"), mergedBin);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_merged_shader_cache.toc"), mergedToc);
            File.WriteAllBytes(Path.Combine(glLeaf, "shaderless_part1.bin"), p1Bin);
            File.WriteAllBytes(Path.Combine(glLeaf, "shaderless_part1.toc"), p1Toc);
            File.WriteAllBytes(Path.Combine(workDir, "d3d12core.dll"), new byte[120]);

            // 1. Capture dominant dictionary BEFORE full reset wipes glCache
            var seed = ProtonRunner.CaptureNvidiaDictionarySeed(new[] { glCache });
            Assert.IsNotNull(seed);
            Assert.AreEqual(dominantDict, seed!.DictId);

            ProtonRunner.PrepareDriverCachesForWarmer(targetCache, vkd3dCache, glCache, "/proton-11/proton", workDir, isIncrementalRun: false);
            Assert.IsFalse(File.Exists(Path.Combine(glLeaf, "steamapp_merged_shader_cache.bin")));

            // 2. Apply seed -> writes 1-entry (56-byte .toc) _NVDICT_ seed into steamapp_merged_shader_cache
            Assert.IsTrue(ProtonRunner.ApplyNvidiaDictionarySeed(glCache, seed));
            string seededBin = Path.Combine(glLeaf, "steamapp_merged_shader_cache.bin");
            string seededToc = Path.Combine(glLeaf, "steamapp_merged_shader_cache.toc");
            Assert.AreEqual(56L, new FileInfo(seededToc).Length);
            Assert.AreEqual(dominantDict, ProtonRunner.TryGetNvidiaCacheDictId(seededBin, seededToc));

            // 3. Warmer writes new shaders into steamapp_shader_cache0 (without re-emitting _NVDICT_) -> FinalizeDriverCaches packs them right after _NVDICT_!
            var (wb0Bin, wb0Toc) = BuildSyntheticCdvnCache(0x59, MakeZstdShaderRecord(99, 990, dominantDict));
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache0.bin"), wb0Bin);
            File.WriteAllBytes(Path.Combine(glLeaf, "steamapp_shader_cache0.toc"), wb0Toc);

            ProtonRunner.FinalizeDriverCaches(targetCache, vkd3dCache, glCache);
            Assert.AreEqual(80L, new FileInfo(seededToc).Length, "Expected _NVDICT_ + 1 compiled shader entry (32 + 2*24 = 80 bytes).");
            Assert.AreEqual(dominantDict, ProtonRunner.TryGetNvidiaCacheDictId(seededBin, seededToc));

            // 4. If a driver upgrade caused 0 shaders to be compiled into an old leaf folder, FinalizeDriverCaches cleans the unused 1-entry seed!
            string staleLeafRoot = Path.Combine(root, "unused-seed-cache");
            string staleGl = Path.Combine(staleLeafRoot, "nvidiav1", "GLCache");
            string staleVkd3d = Path.Combine(staleLeafRoot, "VKD3D_shader_cache");
            Directory.CreateDirectory(staleVkd3d);
            Assert.IsTrue(ProtonRunner.ApplyNvidiaDictionarySeed(staleGl, seed));
            Assert.IsTrue(File.Exists(Path.Combine(staleGl, relLeaf, "steamapp_merged_shader_cache.toc")));

            ProtonRunner.FinalizeDriverCaches(staleLeafRoot, staleVkd3d, staleGl);
            Assert.IsFalse(File.Exists(Path.Combine(staleGl, relLeaf, "steamapp_merged_shader_cache.toc")), "Unused 1-entry _NVDICT_ seed must be cleaned on finalize.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void SharedNonSteamPrefix_InstallsPerGameNvidiaParts_ResolvesLutrisGameName_AndClearsIsolated()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-shared-gl-" + Guid.NewGuid().ToString("N"));
        string sharedPrefix = Path.Combine(root, "Games", "epic-games-store");
        string relLeaf = Path.Combine("ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        Directory.CreateDirectory(sharedPrefix);

        try
        {
            const uint pbzDict = 0x45f88685u;
            const uint arcDict = 0x778899aau;

            var g6 = MakeGame("6", GameSource.Lutris, "/games/pbz", "/games/pbz/PBZ.exe") with
            {
                Name = "Phantom Blade Zero",
                PrefixPath = sharedPrefix,
                GlCachePath = sharedPrefix
            };
            var g3 = MakeGame("3", GameSource.Lutris, "/games/arc", "/games/arc/ARC.exe") with
            {
                Name = "ARC Raiders",
                PrefixPath = sharedPrefix,
                GlCachePath = sharedPrefix
            };

            // Populate Game 6 private cache (merged + part1 -> should install as shaderless_g6_part1 & shaderless_g6_part2)
            string g6Own = ProtonRunner.GetNonSteamShaderCachePath("6", sharedPrefix);
            string g6Vkd3d = Path.Combine(g6Own, "VKD3D_shader_cache");
            string g6GlLeaf = Path.Combine(g6Own, "nvidiav1", "GLCache", relLeaf);
            Directory.CreateDirectory(g6Vkd3d);
            Directory.CreateDirectory(g6GlLeaf);
            var (g6MergedBin, g6MergedToc) = BuildSyntheticCdvnCache(0x59, MakeNvdictRecord(0x59, pbzDict, 512), MakeZstdShaderRecord(1, 1, pbzDict, payloadBytes: 512));
            var (g6P1Bin, g6P1Toc) = BuildSyntheticCdvnCache(0x59, MakeZstdShaderRecord(2, 2, pbzDict, payloadBytes: 512));
            File.WriteAllBytes(Path.Combine(g6GlLeaf, "steamapp_merged_shader_cache.bin"), g6MergedBin);
            File.WriteAllBytes(Path.Combine(g6GlLeaf, "steamapp_merged_shader_cache.toc"), g6MergedToc);
            File.WriteAllBytes(Path.Combine(g6GlLeaf, "shaderless_part1.bin"), g6P1Bin);
            File.WriteAllBytes(Path.Combine(g6GlLeaf, "shaderless_part1.toc"), g6P1Toc);

            ProtonRunner.InstallCachesWhereGameReads(g6, g6Vkd3d, Path.Combine(g6Own, "nvidiav1", "GLCache"));

            // Populate Game 3 private cache in the EXACT SAME shared Epic prefix -> should install as shaderless_g3_part1 WITHOUT touching g6!
            string g3Own = ProtonRunner.GetNonSteamShaderCachePath("3", sharedPrefix);
            string g3Vkd3d = Path.Combine(g3Own, "VKD3D_shader_cache");
            string g3GlLeaf = Path.Combine(g3Own, "nvidiav1", "GLCache", relLeaf);
            Directory.CreateDirectory(g3Vkd3d);
            Directory.CreateDirectory(g3GlLeaf);
            var (g3MergedBin, g3MergedToc) = BuildSyntheticCdvnCache(0x59, MakeNvdictRecord(0x59, arcDict, 256), MakeZstdShaderRecord(3, 3, arcDict, payloadBytes: 256));
            File.WriteAllBytes(Path.Combine(g3GlLeaf, "steamapp_merged_shader_cache.bin"), g3MergedBin);
            File.WriteAllBytes(Path.Combine(g3GlLeaf, "steamapp_merged_shader_cache.toc"), g3MergedToc);

            ProtonRunner.InstallCachesWhereGameReads(g3, g3Vkd3d, Path.Combine(g3Own, "nvidiav1", "GLCache"));

            string sharedLeaf = Path.Combine(sharedPrefix, "GLCache", relLeaf);
            Assert.IsTrue(File.Exists(Path.Combine(sharedLeaf, "shaderless_g6_part1.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(sharedLeaf, "shaderless_g6_part2.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(sharedLeaf, "shaderless_g3_part1.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(sharedPrefix, "GLCache", "shaderless_games.tsv")));

            // Verify Python user_settings.py v6 resolves Lutris's GAME_NAME env var to select each game's exact cache parts & dictionary!
            string protonDir = Path.Combine(root, "proton");
            Directory.CreateDirectory(protonDir);
            File.WriteAllText(Path.Combine(protonDir, "user_settings.py"), ProtonRunner.BuildProtonUserSettingsScript(new AppSettings(MaxCacheSizeGb: 25)));
            string harnessPy = Path.Combine(root, "check_ro.py");
            File.WriteAllText(harnessPy, """
import sys
sys.path.insert(0, sys.argv[1])
import user_settings as us
print(us.user_settings.get("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME", ""))
""");

            string RunForGameName(string gameName)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("python3")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(harnessPy);
                psi.ArgumentList.Add(protonDir);
                psi.Environment["STEAM_COMPAT_DATA_PATH"] = sharedPrefix;
                psi.Environment["__GL_SHADER_DISK_CACHE_PATH"] = sharedPrefix;
                psi.Environment["GAME_NAME"] = gameName;
                using var p = System.Diagnostics.Process.Start(psi)!;
                string outStr = p.StandardOutput.ReadToEnd().Trim();
                string errStr = p.StandardError.ReadToEnd();
                p.WaitForExit(10_000);
                Assert.AreEqual(0, p.ExitCode, errStr);
                return outStr;
            }

            Assert.AreEqual(
                "shaderless_g3_part1;steamapp_merged_shader_cache;steam_shader_cache",
                RunForGameName("ARC Raiders"));
            Assert.AreEqual(
                "shaderless_g6_part1;shaderless_g6_part2;steamapp_merged_shader_cache;steam_shader_cache",
                RunForGameName("Phantom Blade Zero"));

            // Clearing Game 3's cache removes ONLY g3 while leaving Game 6's shared-prefix GLCache 100% intact!
            ProtonRunner.RemoveInstalledCaches(sharedPrefix, "3");
            Assert.IsFalse(File.Exists(Path.Combine(sharedLeaf, "shaderless_g3_part1.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(sharedLeaf, "shaderless_g6_part1.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(sharedLeaf, "shaderless_g6_part2.bin")));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ZdictParams
    {
        public int CompressionLevel;
        public uint NotificationLevel;
        public uint DictId;
    }

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern nuint ZDICT_finalizeDictionary(
        byte[] dstDictBuffer,
        nuint maxDictSize,
        byte[] dictContent,
        nuint dictContentSize,
        byte[] samplesBuffer,
        nuint[] samplesSizes,
        uint nbSamples,
        ZdictParams parameters);

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern IntPtr ZSTD_createCDict(byte[] dictBuffer, nuint dictSize, int compressionLevel);

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern nuint ZSTD_freeCDict(IntPtr cdict);

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern IntPtr ZSTD_createCCtx();

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern nuint ZSTD_freeCCtx(IntPtr cctx);

    [System.Runtime.InteropServices.DllImport("libzstd.so.1", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern nuint ZSTD_compress_usingCDict(
        IntPtr cctx,
        byte[] dst,
        nuint dstCapacity,
        byte[] src,
        nuint srcSize,
        IntPtr cdict);

    private static (byte[] DictBytes, IntPtr CDict) CreateRealFormattedZstdDictionary(uint dictId)
    {
        string sampleTemplate =
            "NVDANVVMNVuc\n  900ea0d4e6e1c7b6: 8\n  03105bcbd882737e: 127\n  0ca619f0d20992cd: 243\n  3b1207eb8332a4d4: 17\n  4b51426e5b24e765: true\n";
        byte[] sampleSingle = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(sampleTemplate, 8)));
        const int sampleCount = 8;
        byte[] samplesBuf = new byte[sampleSingle.Length * sampleCount];
        nuint[] sampleSizes = new nuint[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            Buffer.BlockCopy(sampleSingle, 0, samplesBuf, i * sampleSingle.Length, sampleSingle.Length);
            sampleSizes[i] = (nuint)sampleSingle.Length;
        }

        byte[] rawContent = new byte[512];
        Buffer.BlockCopy(sampleSingle, 0, rawContent, 0, Math.Min(rawContent.Length, sampleSingle.Length));

        byte[] dstDict = new byte[4096];
        nuint dictLen = ZDICT_finalizeDictionary(
            dstDict,
            (nuint)dstDict.Length,
            rawContent,
            (nuint)rawContent.Length,
            samplesBuf,
            sampleSizes,
            sampleCount,
            new ZdictParams { CompressionLevel = 3, NotificationLevel = 0, DictId = dictId });

        Assert.IsTrue(dictLen > 32 && dictLen <= (nuint)dstDict.Length, $"ZDICT_finalizeDictionary failed: {dictLen}");
        byte[] finalDict = dstDict[..(int)dictLen];
        Assert.AreEqual(0xEC30A437u, BitConverter.ToUInt32(finalDict, 0));
        Assert.AreEqual(dictId, BitConverter.ToUInt32(finalDict, 4));

        IntPtr cdict = ZSTD_createCDict(finalDict, (nuint)finalDict.Length, 3);
        Assert.AreNotEqual(IntPtr.Zero, cdict);
        return (finalDict, cdict);
    }

    private static (ulong K0, ulong K1, byte[] Payload) MakeRealCompressedNvdcShaderRecord(
        ulong k0,
        ulong k1,
        IntPtr cctx,
        IntPtr cdict,
        int promoValue,
        bool isD3d12DescriptorBuffer = true)
    {
        string text =
            $"NVDANVVMNVuc shader={k0:x16}\n" +
            $"  03105bcbd882737e: {promoValue}\n" +
            $"  0ca619f0d20992cd: 243\n" +
            $"  4b51426e5b24e765: {(isD3d12DescriptorBuffer ? "true" : "false")}\n";
        byte[] uncompressed = System.Text.Encoding.ASCII.GetBytes(text);
        byte[] compBuf = new byte[uncompressed.Length + 512];
        nuint cLen = ZSTD_compress_usingCDict(cctx, compBuf, (nuint)compBuf.Length, uncompressed, (nuint)uncompressed.Length, cdict);
        Assert.IsTrue(cLen > 8 && cLen < (nuint)compBuf.Length);

        byte[] payload = new byte[4 + (int)cLen];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), (uint)uncompressed.Length);
        Buffer.BlockCopy(compBuf, 0, payload, 4, (int)cLen);
        Assert.AreEqual(0xFD2FB528u, BitConverter.ToUInt32(payload, 4));
        return (k0, k1, payload);
    }

    [TestMethod]
    public void NvidiaDescriptorPromotion_DetectsRealD3d12ZeroVs127_IgnoresStaleShaderlessAndDx11Launchers()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Path.Combine(Path.GetTempPath(), "shaderless-nvpromo-" + Guid.NewGuid().ToString("N"));
        string egsPrefix = Path.Combine(root, "Games", "epic-games-store");
        string relLeaf = Path.Combine("ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        string egsLeaf = Path.Combine(egsPrefix, "GLCache", relLeaf);
        string pbzOwn = ProtonRunner.GetNonSteamShaderCachePath("6", egsPrefix);
        string pbzOwnLeaf = Path.Combine(pbzOwn, "nvidiav1", "GLCache", relLeaf);

        string steamRoot = Path.Combine(root, "Steam");
        string witcherLeaf = Path.Combine(steamRoot, "steamapps", "shadercache", "292030", "nvidiav1", "GLCache", relLeaf);
        string witcherCompatPfx = Path.Combine(steamRoot, "steamapps", "compatdata", "292030", "pfx");

        Directory.CreateDirectory(egsLeaf);
        Directory.CreateDirectory(pbzOwnLeaf);
        Directory.CreateDirectory(witcherLeaf);
        Directory.CreateDirectory(witcherCompatPfx);

        const uint dictId = 0x55fa2189u;
        var (dictBytes, cdict) = CreateRealFormattedZstdDictionary(dictId);
        IntPtr cctx = ZSTD_createCCtx();

        try
        {
            byte[] dictK0 = new byte[8];
            dictK0[0] = 0x59;
            var dictRecord = (BitConverter.ToUInt64(dictK0), BitConverter.ToUInt64("_NVDICT_"u8), dictBytes);

            // 1. Phantom Blade Zero scenario:
            //    - Stale Shaderless shaderless_g6_part1 has 20 D3D12 shaders with promo=127 (MUST BE IGNORED!)
            //    - Real game runtime bucket steamapp_shader_cache5 has 12 D3D12 shaders with promo=0 (without repeating _NVDICT_!)
            var staleWarmerRecords = new List<(ulong, ulong, byte[])> { dictRecord };
            for (ulong i = 1; i <= 20; i++)
                staleWarmerRecords.Add(MakeRealCompressedNvdcShaderRecord(i, 0x5c360000UL + i, cctx, cdict, promoValue: 127, isD3d12DescriptorBuffer: true));
            var (staleBin, staleToc) = BuildSyntheticCdvnCache(0x59, staleWarmerRecords.ToArray());
            File.WriteAllBytes(Path.Combine(egsLeaf, "shaderless_g6_part1.bin"), staleBin);
            File.WriteAllBytes(Path.Combine(egsLeaf, "shaderless_g6_part1.toc"), staleToc);
            File.WriteAllBytes(Path.Combine(pbzOwnLeaf, "steamapp_merged_shader_cache.bin"), staleBin);
            File.WriteAllBytes(Path.Combine(pbzOwnLeaf, "steamapp_merged_shader_cache.toc"), staleToc);

            var pbzRuntimeRecords = new List<(ulong, ulong, byte[])>();
            for (ulong i = 1; i <= 12; i++)
                pbzRuntimeRecords.Add(MakeRealCompressedNvdcShaderRecord(i, 0xc1650000UL + i, cctx, cdict, promoValue: 0, isD3d12DescriptorBuffer: true));
            var (pbzBin, pbzToc) = BuildSyntheticCdvnCache(0x59, pbzRuntimeRecords.ToArray());
            File.WriteAllBytes(Path.Combine(egsLeaf, "steamapp_shader_cache5.bin"), pbzBin);
            File.WriteAllBytes(Path.Combine(egsLeaf, "steamapp_shader_cache5.toc"), pbzToc);

            var pbzGame = MakeGame(
                "6",
                GameSource.Lutris,
                Path.Combine(egsPrefix, "drive_c", "Program Files", "Epic Games", "PBZ"),
                Path.Combine(egsPrefix, "drive_c", "Program Files", "Epic Games", "PBZ", "PBZ.exe")) with
            {
                Name = "Phantom Blade Zero",
                PrefixPath = egsPrefix,
                GlCachePath = egsPrefix
            };

            Assert.IsTrue(
                ProtonRunner.ShouldDisableNvidiaDescriptorPromotion(
                    pbzGame,
                    Path.Combine(pbzOwn, "nvidiav1", "GLCache"),
                    new[] { Path.Combine(pbzOwn, "nvidiav1", "GLCache"), Path.Combine(egsPrefix, "GLCache") },
                    egsPrefix),
                "Layer 1 must detect enableDescriptorPromotion=0 from PBZ runtime cache while ignoring stale shaderless_g6_part1 (127).");

            // 2. Clear Cache + Static Layer 2 fallback scenario:
            //    Also verify RemoveInstalledCaches deletes steamapp_shader_cache5.{bin,toc} AND .cache.write while preserving shaderless_g3_part1!
            File.WriteAllBytes(Path.Combine(egsLeaf, "shaderless_g3_part1.bin"), staleBin);
            File.WriteAllBytes(Path.Combine(egsLeaf, "shaderless_g3_part1.toc"), staleToc);
            string sharedVkd3d = Path.Combine(egsPrefix, "shadercache", "VKD3D_shader_cache");
            Directory.CreateDirectory(sharedVkd3d);
            string pbzVkd3dCache = Path.Combine(sharedVkd3d, "vkd3d-proton.PBZ.exe.cache");
            File.WriteAllBytes(pbzVkd3dCache, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(pbzVkd3dCache + ".write", new byte[] { 5, 6, 7, 8 });
            File.WriteAllLines(Path.Combine(pbzOwn, "installed-files.txt"), new[]
            {
                pbzVkd3dCache,
                Path.Combine(egsLeaf, "shaderless_g6_part1.bin"),
                Path.Combine(egsLeaf, "shaderless_g6_part1.toc")
            });

            ProtonRunner.RemoveInstalledCaches(egsPrefix, "6");
            Assert.IsFalse(File.Exists(Path.Combine(egsLeaf, "steamapp_shader_cache5.bin")), "Clear Cache must remove unmerged steamapp_shader_cache5.bin");
            Assert.IsFalse(File.Exists(Path.Combine(egsLeaf, "shaderless_g6_part1.bin")), "Clear Cache must remove shaderless_g6_part1.bin");
            Assert.IsFalse(File.Exists(pbzVkd3dCache + ".write"), "Clear Cache must remove .cache.write companion");
            Assert.IsTrue(File.Exists(Path.Combine(egsLeaf, "shaderless_g3_part1.bin")), "Clear Cache must preserve other games' shaderless_g3_part1.bin");

            // Even with ZERO real-game donor GLCaches left on disk, Layer 2 detects EOSOverlayVkLayer in system.reg!
            File.WriteAllText(Path.Combine(egsPrefix, "system.reg"), """
WINE REGISTRY Version 2

[Software\\Khronos\\Vulkan\\ImplicitLayers] 1791753463
"C:\\Program Files (x86)\\Epic Games\\Epic Online Services\\managedArtifacts\\EOSOverlayVkLayer-Win64.json"=dword:00000000
""");
            Assert.IsTrue(
                ProtonRunner.ShouldDisableNvidiaDescriptorPromotion(
                    pbzGame,
                    Path.Combine(pbzOwn, "nvidiav1", "GLCache"),
                    new[] { Path.Combine(egsPrefix, "GLCache") },
                    egsPrefix),
                "Layer 2 must detect enabled EOSOverlayVkLayer in system.reg when no donor GLCache remains.");

            // 3. The Witcher 3 scenario (mixed DX11 REDlauncher with promo=0 + D3D12 witcher3.exe with promo=127):
            var witcherRecords = new List<(ulong, ulong, byte[])> { dictRecord };
            for (ulong i = 1; i <= 25; i++)
                witcherRecords.Add(MakeRealCompressedNvdcShaderRecord(100 + i, 0x990000UL + i, cctx, cdict, promoValue: 0, isD3d12DescriptorBuffer: false));
            for (ulong i = 1; i <= 15; i++)
                witcherRecords.Add(MakeRealCompressedNvdcShaderRecord(200 + i, 0xaa0000UL + i, cctx, cdict, promoValue: 127, isD3d12DescriptorBuffer: true));
            var (wBin, wToc) = BuildSyntheticCdvnCache(0x59, witcherRecords.ToArray());
            File.WriteAllBytes(Path.Combine(witcherLeaf, "steamapp_merged_shader_cache.bin"), wBin);
            File.WriteAllBytes(Path.Combine(witcherLeaf, "steamapp_merged_shader_cache.toc"), wToc);
            File.WriteAllText(Path.Combine(witcherCompatPfx, "system.reg"), "WINE REGISTRY Version 2\n");

            var witcherGame = MakeGame("292030", GameSource.Steam, "/games/witcher3", "/games/witcher3/witcher3.exe") with
            {
                Name = "The Witcher 3: Wild Hunt",
                PrefixPath = witcherCompatPfx
            };
            Assert.IsFalse(
                ProtonRunner.ShouldDisableNvidiaDescriptorPromotion(
                    witcherGame,
                    Path.Combine(steamRoot, "steamapps", "shadercache", "292030", "nvidiav1", "GLCache"),
                    new[] { Path.Combine(steamRoot, "steamapps", "shadercache", "292030", "nvidiav1", "GLCache") },
                    Path.Combine(steamRoot, "steamapps", "compatdata", "292030")),
                "D3D12 promo=127 games must keep default 127 even when DX11 launcher shaders in the same cache have promo=0.");

            // 4. Runtime stamp invalidation when nvpromo=0 is enabled:
            string workDir = Path.Combine(root, "work");
            Directory.CreateDirectory(workDir);
            File.WriteAllBytes(Path.Combine(workDir, "d3d12core.dll"), new byte[128]);
            string stampDir = Path.Combine(root, "stamp_test");
            string stampVkd3d = Path.Combine(stampDir, "VKD3D_shader_cache");
            string stampGl = Path.Combine(stampDir, "nvidiav1", "GLCache");
            string stampGlLeaf = Path.Combine(stampGl, relLeaf);
            Directory.CreateDirectory(stampVkd3d);
            Directory.CreateDirectory(stampGlLeaf);

            ProtonRunner.PrepareDriverCachesForWarmer(
                stampDir, stampVkd3d, stampGl, "/fake/proton", workDir,
                isIncrementalRun: false, preserveExistingCache: false, disableNvidiaDescriptorPromotion: false);
            File.WriteAllBytes(Path.Combine(stampGlLeaf, "steamapp_merged_shader_cache.bin"), staleBin);
            File.WriteAllBytes(Path.Combine(stampGlLeaf, "steamapp_merged_shader_cache.toc"), staleToc);

            // Re-running with preserveExistingCache=true BUT disableNvidiaDescriptorPromotion=true must detect stamp change and reset stale 127 cache!
            ProtonRunner.PrepareDriverCachesForWarmer(
                stampDir, stampVkd3d, stampGl, "/fake/proton", workDir,
                isIncrementalRun: true, preserveExistingCache: true, disableNvidiaDescriptorPromotion: true);
            Assert.IsFalse(File.Exists(Path.Combine(stampGlLeaf, "steamapp_merged_shader_cache.bin")),
                "Switching to nvpromo=0 must invalidate stale 127 cache via shaderless_runtime.stamp.");
            StringAssert.EndsWith(File.ReadAllText(Path.Combine(stampDir, "shaderless_runtime.stamp")).Trim(), "|nvpromo=0");
        }
        finally
        {
            if (cctx != IntPtr.Zero) ZSTD_freeCCtx(cctx);
            if (cdict != IntPtr.Zero) ZSTD_freeCDict(cdict);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task LutrisUbisoftConnect_DiscoversContainerPrefixGames_DeduplicatesServiceRows_AndResolvesParentProtonConfig()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-lutris-ubi-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string xdgData = Path.Combine(home, ".local", "share");
        string xdgConfig = Path.Combine(home, ".config");
        string lutrisData = Path.Combine(xdgData, "lutris");
        string lutrisGamesCfg = Path.Combine(lutrisData, "games");
        string ubiPrefix = Path.Combine(home, "Games", "ubisoft-connect");
        string emptyEaPrefix = Path.Combine(home, "Games", "ea-app");

        Directory.CreateDirectory(lutrisGamesCfg);
        Directory.CreateDirectory(Path.Combine(lutrisData, "coverart"));
        Directory.CreateDirectory(Path.Combine(ubiPrefix, "drive_c"));
        Directory.CreateDirectory(Path.Combine(emptyEaPrefix, "drive_c"));

        try
        {
            // 1. Populate Assassin's Creed Shadows (Uplay ID 8006) + Far Cry 6 (Uplay ID 1234) inside Ubisoft Connect prefix
            string acsDir = Path.Combine(ubiPrefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher", "games", "Assassin's Creed Shadows");
            Directory.CreateDirectory(Path.Combine(acsDir, "D3D12"));
            string acsPlainExe = Path.Combine(acsDir, "ACShadows.exe");
            string acsPlusExe = Path.Combine(acsDir, "ACShadows_Plus.exe");
            File.WriteAllBytes(acsPlainExe, new byte[40_000]);
            File.WriteAllBytes(acsPlusExe, new byte[50_000]); // Ubisoft+ binary is larger than owned binary!
            File.WriteAllBytes(Path.Combine(acsDir, "D3D12", "D3D12Core.dll"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(acsDir, "DataPC_boot_dx12.forge"), new byte[2048]);
            File.WriteAllText(Path.Combine(acsDir, "uplay_install.state"), "installed");

            string fc6Dir = Path.Combine(ubiPrefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher", "games", "Far Cry 6");
            Directory.CreateDirectory(Path.Combine(fc6Dir, "bin"));
            Directory.CreateDirectory(Path.Combine(fc6Dir, "D3D12"));
            string fc6Exe = Path.Combine(fc6Dir, "bin", "FarCry6.exe");
            File.WriteAllBytes(fc6Exe, new byte[32_000]);
            File.WriteAllBytes(Path.Combine(fc6Dir, "D3D12", "D3D12Core.dll"), new byte[1024]);

            string acsBanner = Path.Combine(lutrisData, "coverart", "assassins-creed-shadows.jpg");
            File.WriteAllBytes(acsBanner, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

            File.WriteAllText(Path.Combine(ubiPrefix, "system.reg"), """
WINE REGISTRY Version 2

[Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\8006] 1793871116
"InstallDir"="C:/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/games/Assassin's Creed Shadows/"
"Language"="en-US"

[Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\1234] 1793871117
"InstallDir"="C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\games\\Far Cry 6\\"

[Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\9999] 1793871118
"InstallDir"="C:/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/games/UninstalledGhost/"
""");

            File.WriteAllText(Path.Combine(lutrisGamesCfg, "ubisoft-connect-1792353012.yml"), $"""
game:
  exe: {ubiPrefix}/drive_c/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe
  prefix: {ubiPrefix}
wine:
  version: GE-Proton11-7-x86_64
system:
  env:
    PROTON_VKD3D_LOWLATENCY: '1'
""");

            string dbPath = Path.Combine(lutrisData, "pga.db");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE games (
                        id INTEGER PRIMARY KEY,
                        slug TEXT,
                        name TEXT,
                        runner TEXT,
                        directory TEXT,
                        installed INTEGER,
                        service TEXT,
                        service_id TEXT,
                        configpath TEXT
                    );
                    """;
                cmd.ExecuteNonQuery();

                void InsertRow(long id, string slug, string name, string dir, string? service, string? serviceId, string? cfgPath)
                {
                    using var ins = conn.CreateCommand();
                    ins.CommandText = "INSERT INTO games (id, slug, name, runner, directory, installed, service, service_id, configpath) VALUES ($id, $slug, $name, 'wine', $dir, 1, $svc, $sid, $cfg)";
                    ins.Parameters.AddWithValue("$id", id);
                    ins.Parameters.AddWithValue("$slug", slug);
                    ins.Parameters.AddWithValue("$name", name);
                    ins.Parameters.AddWithValue("$dir", dir);
                    ins.Parameters.AddWithValue("$svc", (object?)service ?? DBNull.Value);
                    ins.Parameters.AddWithValue("$sid", (object?)serviceId ?? DBNull.Value);
                    ins.Parameters.AddWithValue("$cfg", (object?)cfgPath ?? DBNull.Value);
                    ins.ExecuteNonQuery();
                }

                // Bare Ubisoft Connect container + explicit service="ubisoft" row for 1234 + empty EA container
                InsertRow(5, "ubisoft-connect", "Ubisoft Connect", ubiPrefix, null, null, "ubisoft-connect-1792353012");
                InsertRow(7, "far-cry-6", "Far Cry 6", ubiPrefix, "ubisoft", "1234", "far-cry-6-1792353999");
                InsertRow(8, "ea-app", "EA App", emptyEaPrefix, null, null, null);
            }

            var source = new Shaderless.Core.Games.LutrisLinuxSource(home, xdgData, xdgConfig);
            var diag = await source.DiagnoseAsync();

            // Must discover Far Cry 6 (id=7) AND Assassin's Creed Shadows (id=5_ubisoft_8006) without duplicating Far Cry 6!
            Assert.HasCount(2, diag.DiscoveredGames);
            var fc6 = diag.DiscoveredGames.Single(g => g.Id == "7");
            Assert.AreEqual(fc6Dir, fc6.InstallPath);
            Assert.AreEqual(fc6Exe, fc6.ExecutablePath);

            var acs = diag.DiscoveredGames.Single(g => g.Id == "5_ubisoft_8006");
            Assert.AreEqual("Assassin's Creed Shadows", acs.Name);
            Assert.AreEqual(acsDir, acs.InstallPath);
            Assert.AreEqual(acsPlainExe, acs.ExecutablePath, "Must default to ACShadows.exe instead of larger ACShadows_Plus.exe.");
            Assert.AreEqual(ubiPrefix, acs.PrefixPath);
            Assert.AreEqual(ubiPrefix, acs.GlCachePath);
            Assert.AreEqual(acsBanner, acs.BannerPath);
            Assert.AreEqual(GameEngine.DirectX12, acs.Engine);

            // Empty EA container must still be reported in SkippedItems
            Assert.IsTrue(diag.SkippedItems.Any(s => s.NameOrId.Contains("EA App", StringComparison.Ordinal) &&
                                                     s.Reason.Contains("Bare Wine prefix", StringComparison.Ordinal)));
            Assert.IsFalse(diag.SkippedItems.Any(s => s.NameOrId.Contains("Ubisoft Connect", StringComparison.Ordinal)));

            // Composite child ID 5_ubisoft_8006 must inherit parent Lutris row 5's Proton version & env overrides
            var runnerCfg = Shaderless.Core.Games.LutrisLinuxSource.TryReadGameRunnerConfig(
                "5_ubisoft_8006", isFlatpak: false, home, xdgData, xdgConfig);
            Assert.IsNotNull(runnerCfg);
            Assert.AreEqual("GE-Proton11-7-x86_64", runnerCfg!.Version);
            Assert.IsTrue(ProtonRunner.IsLowLatencyConfigured(acs, "PROTON_VKD3D_LOWLATENCY", home, xdgData, xdgConfig));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void UbisoftPlusAndAnvilForge_ResolvesLoggedBinary_TracksForgeUpdates_MirrorsVkd3dCache_AndResolvesContainerTagInPython()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-ubi-plus-" + Guid.NewGuid().ToString("N"));
        string ubiPrefix = Path.Combine(root, "Games", "ubisoft-connect");
        string acsDir = Path.Combine(ubiPrefix, "drive_c", "games", "Assassin's Creed Shadows");
        string relLeaf = Path.Combine("ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        Directory.CreateDirectory(Path.Combine(acsDir, "D3D12"));

        try
        {
            string plainExe = Path.Combine(acsDir, "ACShadows.exe");
            string plusExe = Path.Combine(acsDir, "ACShadows_Plus.exe");
            string forgeBoot = Path.Combine(acsDir, "DataPC_boot_dx12.forge");
            File.WriteAllBytes(plainExe, new byte[44_000]);
            File.WriteAllBytes(plusExe, new byte[50_000]);
            File.WriteAllBytes(forgeBoot, new byte[4096]);

            // 1. Default executable resolution -> ACShadows.exe; switching via shaderless.log -> ACShadows_Plus.exe (and back)
            Assert.AreEqual(plainExe, Shaderless.Core.Games.GameEngineDetector.FindExecutable(acsDir));
            Assert.AreEqual(plainExe, Shaderless.Core.Games.GameEngineDetector.ResolveRealGameExecutable(acsDir, plusExe));

            var acsGame = MakeGame("5_ubisoft_8006", GameSource.Lutris, acsDir, plainExe) with
            {
                Name = "Assassin's Creed Shadows",
                PrefixPath = ubiPrefix,
                GlCachePath = ubiPrefix,
                Engine = GameEngine.DirectX12
            };
            string stampBeforeSession = ProtonRunner.GetGameBuildStamp(acsGame);

            File.WriteAllText(Path.Combine(acsDir, "shaderless.log"),
                "[      0.1s] loaded into C:\\Program Files (x86)\\Ubisoft\\games\\Assassin's Creed Shadows\\ACShadows_Plus.exe (Linux/Proton recorder)\n");
            Assert.AreEqual(plusExe, Shaderless.Core.Games.GameEngineDetector.ResolveRealGameExecutable(acsDir, plainExe));
            Assert.AreEqual(stampBeforeSession, ProtonRunner.GetGameBuildStamp(acsGame),
                "Switching between ACShadows.exe and ACShadows_Plus.exe must NOT falsely change GameBuildStamp.");

            // Adding or patching a .forge archive MUST update GameBuildStamp
            File.WriteAllBytes(Path.Combine(acsDir, "DataPC_patch_01.forge"), new byte[8192]);
            Assert.AreNotEqual(stampBeforeSession, ProtonRunner.GetGameBuildStamp(acsGame),
                "New or updated Ubisoft Anvil .forge archive must change GameBuildStamp.");

            // 2. VKD3D-Proton cache mirroring across ACShadows & ACShadows_Plus + isolated NVIDIA g5ubisoft8006 parts
            string ownCache = ProtonRunner.GetNonSteamShaderCachePath(acsGame.Id, ubiPrefix);
            string ownVkd3d = Path.Combine(ownCache, "VKD3D_shader_cache");
            string ownGlLeaf = Path.Combine(ownCache, "nvidiav1", "GLCache", relLeaf);
            Directory.CreateDirectory(ownVkd3d);
            Directory.CreateDirectory(ownGlLeaf);

            byte[] fakeVkd3d = new byte[128];
            "VKD3D_PROTON_CACHE"u8.CopyTo(fakeVkd3d);
            File.WriteAllBytes(Path.Combine(ownVkd3d, "ACShadows.cache"), fakeVkd3d);
            File.WriteAllBytes(Path.Combine(ownVkd3d, "vkd3d-proton.ACShadows.exe.cache"), fakeVkd3d);

            const uint acsDict = 0x33445566u;
            var (acsBin, acsToc) = BuildSyntheticCdvnCache(
                0x59,
                MakeNvdictRecord(0x59, acsDict, 256),
                MakeZstdShaderRecord(11, 110, acsDict, payloadBytes: 512));
            File.WriteAllBytes(Path.Combine(ownGlLeaf, "steamapp_merged_shader_cache.bin"), acsBin);
            File.WriteAllBytes(Path.Combine(ownGlLeaf, "steamapp_merged_shader_cache.toc"), acsToc);

            ProtonRunner.InstallCachesWhereGameReads(acsGame, ownVkd3d, Path.Combine(ownCache, "nvidiav1", "GLCache"));
            ProtonRunner.EnsureProtonLocalShaderCacheSetting(ubiPrefix);

            string installedVkd3d = Path.Combine(ubiPrefix, "shadercache", "VKD3D_shader_cache");
            Assert.IsTrue(File.Exists(Path.Combine(installedVkd3d, "ACShadows.cache")));
            Assert.IsTrue(File.Exists(Path.Combine(installedVkd3d, "ACShadows_Plus.cache")));
            Assert.IsTrue(File.Exists(Path.Combine(installedVkd3d, "vkd3d-proton.ACShadows.exe.cache")));
            Assert.IsTrue(File.Exists(Path.Combine(installedVkd3d, "vkd3d-proton.ACShadows_Plus.exe.cache")));

            string sharedLeaf = Path.Combine(ubiPrefix, "GLCache", relLeaf);
            Assert.IsTrue(File.Exists(Path.Combine(sharedLeaf, "shaderless_g5ubisoft8006_part1.bin")));
            Assert.IsTrue(ProtonRunner.VerifyCacheInstalled(acsGame, Path.Combine(root, "Steam")).Verified);

            // 3. Verify Python user_settings.py v7 resolves GAME_NAME="Ubisoft Connect" to single tag "5ubisoft8006"
            //    and merges runtime misses into shaderless_g5ubisoft8006_part1, while ignoring unrelated game names!
            var (wbBin, wbToc) = BuildSyntheticCdvnCache(0x59, MakeZstdShaderRecord(22, 220, acsDict, payloadBytes: 256));
            File.WriteAllBytes(Path.Combine(sharedLeaf, "steamapp_shader_cache0.bin"), wbBin);
            File.WriteAllBytes(Path.Combine(sharedLeaf, "steamapp_shader_cache0.toc"), wbToc);

            string protonDir = Path.Combine(root, "proton");
            Directory.CreateDirectory(protonDir);
            File.WriteAllText(Path.Combine(protonDir, "user_settings.py"), ProtonRunner.BuildProtonUserSettingsScript(new AppSettings(MaxCacheSizeGb: 25)));
            string harnessPy = Path.Combine(root, "check_ubi_tag.py");
            File.WriteAllText(harnessPy, """
import os, sys, json
sys.path.insert(0, sys.argv[1])
import user_settings as us
print(json.dumps({
    "tag": us._game_tag,
    "ro": us.user_settings.get("__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME", "")
}))
""");

            (string Tag, string Ro) RunWithGameName(string gameName)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("python3")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(harnessPy);
                psi.ArgumentList.Add(protonDir);
                psi.Environment["STEAM_COMPAT_DATA_PATH"] = ubiPrefix;
                psi.Environment["__GL_SHADER_DISK_CACHE_PATH"] = ubiPrefix;
                psi.Environment["GAME_NAME"] = gameName;
                using var p = System.Diagnostics.Process.Start(psi)!;
                string outStr = p.StandardOutput.ReadToEnd().Trim();
                string errStr = p.StandardError.ReadToEnd();
                p.WaitForExit(10_000);
                Assert.AreEqual(0, p.ExitCode, errStr);
                using var doc = System.Text.Json.JsonDocument.Parse(outStr);
                return (doc.RootElement.GetProperty("tag").GetString()!, doc.RootElement.GetProperty("ro").GetString()!);
            }

            // Unrelated game name must NOT resolve to 5ubisoft8006
            var unrelated = RunWithGameName("Some Uncompiled Game");
            Assert.AreEqual("", unrelated.Tag);

            // Re-drop runtime bucket and run as Lutris container GAME_NAME="Ubisoft Connect" -> resolves "5ubisoft8006" and merges key (22,220)!
            File.WriteAllBytes(Path.Combine(sharedLeaf, "steamapp_shader_cache0.bin"), wbBin);
            File.WriteAllBytes(Path.Combine(sharedLeaf, "steamapp_shader_cache0.toc"), wbToc);
            var containerRun = RunWithGameName("Ubisoft Connect");
            Assert.AreEqual("5ubisoft8006", containerRun.Tag);
            Assert.AreEqual("shaderless_g5ubisoft8006_part1;steamapp_merged_shader_cache;steam_shader_cache", containerRun.Ro);
            CollectionAssert.Contains(
                ReadTocKeys(Path.Combine(sharedLeaf, "shaderless_g5ubisoft8006_part1.toc")),
                (22UL, 220UL));

            // 4. Clear Cache removes BOTH ACShadows AND ACShadows_Plus VKD3D caches + g5ubisoft8006 GLCache
            File.WriteAllBytes(Path.Combine(installedVkd3d, "ACShadows_Plus.cache.write"), new byte[] { 1, 2, 3 });
            ProtonRunner.RemoveInstalledCaches(ubiPrefix, acsGame.Id);
            Assert.IsFalse(File.Exists(Path.Combine(installedVkd3d, "ACShadows.cache")));
            Assert.IsFalse(File.Exists(Path.Combine(installedVkd3d, "ACShadows_Plus.cache")));
            Assert.IsFalse(File.Exists(Path.Combine(installedVkd3d, "ACShadows_Plus.cache.write")));
            Assert.IsFalse(File.Exists(Path.Combine(sharedLeaf, "shaderless_g5ubisoft8006_part1.bin")));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class ConfigurableCacheDriverBackend(long bytesPerGame) : IDriverBackend
    {
        public GpuVendor Vendor => GpuVendor.Nvidia;
        public Task<DriverInfo> GetDriverInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DriverInfo(
                GpuVendor.Nvidia, "Mock GPU", "NVIDIA", "615.00", "/tmp", 0, 0, 0, 0, 0, DriverCacheStatus.Healthy, null));

        public Task<(long Bytes, int Files, string Path)> GetGameCacheStatsAsync(string gameId, GameSource source, string? prefixPath = null) =>
            Task.FromResult((bytesPerGame, bytesPerGame > 0 ? 2 : 0, "/tmp/shadercache/" + gameId));

        public Task ClearCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearGameCacheAsync(string gameId, GameSource source, string? prefixPath = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAllCachesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string GetRecommendedEnvironmentVariables() => "";
    }

    private static byte[] MakeMinimalRawDxbcContainer()
    {
        byte[] container = new byte[128];
        "DXBC"u8.CopyTo(container.AsSpan(0, 4));
        BitConverter.TryWriteBytes(container.AsSpan(24, 4), 128u);   // totalSize
        BitConverter.TryWriteBytes(container.AsSpan(28, 2), (ushort)2); // partCount
        return container;
    }

    [TestMethod]
    public void HasCompressedCompanionShaderArchives_DetectsHybridCarvedGame_AndIgnoresRawOrUnreal()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-hybrid-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            // Raw global shader bank (like AC Shadows' ShaderDataDX12.bin)
            byte[] rawSbin = new byte[128 * 1024];
            "SBIN"u8.CopyTo(rawSbin.AsSpan(0, 4));
            MakeMinimalRawDxbcContainer().CopyTo(rawSbin.AsSpan(64));
            File.WriteAllBytes(Path.Combine(root, "ShaderDataDX12.bin"), rawSbin);

            // Oodle-compressed companion archive (like DataPC_boot_dx12.forge with 'scimitar\0' header and 0 raw DXBC)
            byte[] forgeBytes = new byte[128 * 1024];
            "scimitar\0"u8.CopyTo(forgeBytes.AsSpan(0, 9));
            for (int i = 16; i < forgeBytes.Length; i++)
                forgeBytes[i] = (byte)((i * 31 + 7) & 0xFF);
            string forgePath = Path.Combine(root, "DataPC_boot_dx12.forge");
            File.WriteAllBytes(forgePath, forgeBytes);

            var carvedEngine = new Shaderless.Engine.EngineInfo(
                Shaderless.Engine.Carved.CarvedReader.Family,
                "DXIL+RTS0",
                null,
                "D3D12",
                false,
                null);
            var unrealEngine = new Shaderless.Engine.EngineInfo(
                "Unreal",
                "5.4",
                null,
                "D3D12",
                false,
                null);

            Assert.IsTrue(ShaderCompilerCoordinator.HasCompressedCompanionShaderArchives(root, carvedEngine));
            Assert.IsFalse(ShaderCompilerCoordinator.HasCompressedCompanionShaderArchives(root, unrealEngine));

            // If the companion archive actually contains uncompressed raw DXBC containers, it is already carved -> false
            MakeMinimalRawDxbcContainer().CopyTo(forgeBytes.AsSpan(256));
            File.WriteAllBytes(forgePath, forgeBytes);
            Assert.IsFalse(ShaderCompilerCoordinator.HasCompressedCompanionShaderArchives(root, carvedEngine));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void PartialCompileNote_HandlesHybridArchives_Vkd3dStateDependent_Recorded_AndPureDx11()
    {
        var none = Shaderless.Engine.AntiCheat.None;
        var readyNoRec = new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.Ready, Shaderless.Engine.Planning.Planner.NoRecording);

        // 1. File-only DX12 with embedded RTS0 / confirmed rule on Linux vkd3d-proton -> still partial without recording!
        var fileOnlyDx12Stats = new Shaderless.Engine.PlanStats(
            Recorded: 0,
            Generated: 22593,
            SynthesizedTemplates: 22593,
            RootSignatures: 69,
            RootSigRuleVerified: true,
            ExactUnits: 3194,
            GuessedUnits: 2661,
            StageSets: 22593);
        string? vkd3dNote = ShaderCompilerCoordinator.PartialCompileNote(
            readyNoRec,
            none,
            fileOnlyDx12Stats,
            hasCompressedCompanionArchives: false,
            linuxVkd3dStateDependent: true);
        StringAssert.Contains(vkd3dNote, "Partial:");
        StringAssert.Contains(vkd3dNote, "Turn on ● Record");

        // 2. Hybrid carved game with compressed companion archives (AC Shadows) -> explains global vs compressed archives!
        string? hybridNote = ShaderCompilerCoordinator.PartialCompileNote(
            readyNoRec,
            none,
            fileOnlyDx12Stats,
            hasCompressedCompanionArchives: true,
            linuxVkd3dStateDependent: true);
        StringAssert.Contains(hybridNote, "global shaders");
        StringAssert.Contains(hybridNote, "compressed shader archives");
        StringAssert.Contains(hybridNote, "Turn on ● Record");

        string? hybridAcNote = ShaderCompilerCoordinator.PartialCompileNote(
            readyNoRec,
            Shaderless.Engine.AntiCheat.EasyAntiCheat,
            fileOnlyDx12Stats,
            hasCompressedCompanionArchives: true,
            linuxVkd3dStateDependent: true);
        StringAssert.Contains(hybridAcNote, "anti-cheat");
        Assert.IsFalse(hybridAcNote!.Contains("Turn on"));

        // 3. Compiled WITH a recording (e.g. Phantom Blade Zero / Valor Mortis / AC Shadows after recording) -> null!
        var recordedStats = fileOnlyDx12Stats with { Recorded = 14500 };
        Assert.IsNull(ShaderCompilerCoordinator.PartialCompileNote(
            new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.Ready, "planned from a recording"),
            none,
            recordedStats,
            hasCompressedCompanionArchives: true,
            linuxVkd3dStateDependent: true));

        // 4. Pure DirectX 11 compile (e.g. A Short Hike) -> null even on Linux!
        var pureDx11Stats = new Shaderless.Engine.PlanStats(
            Recorded: 0,
            Generated: 0,
            SynthesizedTemplates: 0,
            RootSignatures: 0,
            RootSigRuleVerified: false,
            D3D11Shaders: 2402);
        Assert.IsNull(ShaderCompilerCoordinator.PartialCompileNote(
            new Shaderless.Engine.PlanCheck(Shaderless.Engine.Readiness.Ready, "compiles every DirectX 11 shader"),
            none,
            pureDx11Stats,
            hasCompressedCompanionArchives: false,
            linuxVkd3dStateDependent: true));
    }

    [TestMethod]
    public async Task RetroactiveCompileNote_InfersPartialFromPlanBin_AndClearsOnceRecordedAndWarmed()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-retro-plan-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(root, "data");
        string installDir = Path.Combine(root, "ACShadows");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(installDir);

        Shaderless.Core.Games.RecorderManager.TestDataDirOverride = dataDir;
        try
        {
            string gameId = "5_ubisoft_8006";
            string exePath = Path.Combine(installDir, "ACShadows.exe");
            File.WriteAllBytes(exePath, new byte[70 * 1024]);

            // Write compressed DataPC_boot_dx12.forge + ShaderDataDX12.bin with raw DXBC
            byte[] rawSbin = new byte[128 * 1024];
            MakeMinimalRawDxbcContainer().CopyTo(rawSbin.AsSpan(64));
            File.WriteAllBytes(Path.Combine(installDir, "ShaderDataDX12.bin"), rawSbin);

            byte[] forgeBytes = new byte[128 * 1024];
            "scimitar\0"u8.CopyTo(forgeBytes.AsSpan(0, 9));
            File.WriteAllBytes(Path.Combine(installDir, "DataPC_boot_dx12.forge"), forgeBytes);

            var game = new GameInfo(
                Id: gameId,
                Name: "Assassin's Creed Shadows",
                Source: GameSource.Lutris,
                InstallPath: installDir,
                ExecutablePath: exePath,
                PrefixPath: null,
                BannerPath: null,
                Engine: GameEngine.DirectX12,
                State: CacheState.NeedsCompilation,
                TotalShadersEstimated: 22593,
                CompiledShadersCount: 0,
                LastCompiledAt: null);

            // Simulate an already-compiled game on disk WITHOUT compile-note.txt, with work/<id>/plan.bin from before
            string protonStamp = ProtonRunner.GetProtonStamp(game);
            string warmedPath = Shaderless.Core.Games.RecorderManager.WarmedKeysPath(gameId);
            Directory.CreateDirectory(Path.GetDirectoryName(warmedPath)!);
            string sampleKey = new string('a', 40);
            File.WriteAllText(warmedPath, $"#STAMP:615.00|{protonStamp}\n{sampleKey}\n");

            string planBinPath = Path.Combine(dataDir, "work", gameId, "plan.bin");
            var plan = new Shaderless.Engine.Plan(
                GameId: "ubisoft_8006",
                IndexContentHash: "deadbeef",
                Platform: "D3D12",
                VendorProfile: "nvidia-1",
                Stats: new Shaderless.Engine.PlanStats(
                    Recorded: 0,
                    Generated: 22593,
                    SynthesizedTemplates: 22593,
                    RootSignatures: 69,
                    RootSigRuleVerified: true,
                    ExactUnits: 3194,
                    GuessedUnits: 2661,
                    StageSets: 22593),
                FilePath: planBinPath);
            Shaderless.Engine.Planning.PlanFile.Write(plan, Array.Empty<Shaderless.Engine.Planning.PsoDb.Rec>());

            var headerBack = ShaderCompilerCoordinator.TryReadPlanHeader(planBinPath);
            Assert.IsNotNull(headerBack);
            Assert.AreEqual(22593L, headerBack!.Stats.Generated);

            var gm = new Shaderless.Core.Games.GameManager(new Shaderless.Core.Games.IGameSource[] { new StaticGameSource(game) });
            var scanned = await gm.ScanAllGamesAsync(new ConfigurableCacheDriverBackend(50L * 1024 * 1024));
            var acs = scanned.Single();

            Assert.AreEqual(CacheState.Cached, acs.State);
            Assert.IsNotNull(acs.CompileNote);
            StringAssert.Contains(acs.CompileNote, "Partial:");

            // Now simulate a subsequent compile after recording where plan.bin has Recorded > 0 -> CompileNote disappears!
            var recordedPlan = plan with { Stats = plan.Stats with { Recorded = 8500 } };
            Shaderless.Engine.Planning.PlanFile.Write(recordedPlan, Array.Empty<Shaderless.Engine.Planning.PsoDb.Rec>());

            var rescanned = await gm.ScanAllGamesAsync(new ConfigurableCacheDriverBackend(75L * 1024 * 1024));
            Assert.IsNull(rescanned.Single().CompileNote);
        }
        finally
        {
            Shaderless.Core.Games.RecorderManager.TestDataDirOverride = null;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task SmartReadiness_FlagsPackedD3d12GameAsNeedsRecordingFirst_AndUnlocksAfterRecording()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-smart-ready-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(root, "data");
        string installDir = Path.Combine(root, "PackedDx12Game");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(installDir);

        Shaderless.Core.Games.RecorderManager.TestDataDirOverride = dataDir;
        try
        {
            string gameId = "packed_dx12_" + Guid.NewGuid().ToString("N");
            string exePath = Path.Combine(installDir, "PackedGame.exe");
            File.WriteAllBytes(exePath, new byte[70 * 1024]);
            File.WriteAllBytes(Path.Combine(installDir, "D3D12Core.dll"), new byte[1024]);

            // Packed archive with 0 raw DXBC shaders
            byte[] packedArchive = new byte[128 * 1024];
            for (int i = 0; i < packedArchive.Length; i++)
                packedArchive[i] = (byte)((i * 17 + 3) & 0xFF);
            File.WriteAllBytes(Path.Combine(installDir, "Data.pak"), packedArchive);

            var game = new GameInfo(
                Id: gameId,
                Name: "Packed DX12 Game",
                Source: GameSource.Steam,
                InstallPath: installDir,
                ExecutablePath: exePath,
                PrefixPath: null,
                BannerPath: null,
                Engine: GameEngine.DirectX12,
                State: CacheState.NeedsCompilation,
                TotalShadersEstimated: 6000,
                CompiledShadersCount: 0,
                LastCompiledAt: null);

            var gm = new Shaderless.Core.Games.GameManager(new Shaderless.Core.Games.IGameSource[] { new StaticGameSource(game) });

            // 1. Uncompiled & unrecorded (0 RecordedPsos): State stays NeedsCompilation, NeedsRecordingFirst == true!
            var beforeRec = (await gm.ScanAllGamesAsync(new ConfigurableCacheDriverBackend(0))).Single();
            Assert.AreEqual(CacheState.NeedsCompilation, beforeRec.State);
            Assert.IsTrue(beforeRec.NeedsRecordingFirst);
            StringAssert.Contains(beforeRec.ReadinessNote, "Needs recording first");
            StringAssert.Contains(beforeRec.ReadinessNote, "● Record");

            // 2. Simulate user recording a compute PSO into games/<id>/recording.db -> NeedsRecordingFirst flips to false!
            string recDb = Shaderless.Core.Games.RecorderManager.RecordingPath(gameId);
            Directory.CreateDirectory(Path.GetDirectoryName(recDb)!);
            using (var fs = File.Create(recDb))
            {
                byte[] fakeComputePayload = new byte[40]; // 20B rootSig sha1 + 20B CS sha1
                for (int i = 0; i < fakeComputePayload.Length; i++) fakeComputePayload[i] = (byte)(i + 1);
                Shaderless.Engine.Planning.PsoDb.Write(fs, 'C', fakeComputePayload);
            }

            var afterRec = (await gm.ScanAllGamesAsync(new ConfigurableCacheDriverBackend(0))).Single();
            Assert.AreEqual(CacheState.NeedsCompilation, afterRec.State);
            Assert.AreEqual(1, afterRec.RecordedPsos);
            Assert.IsFalse(afterRec.NeedsRecordingFirst, "Once PSOs are recorded, Kill Stutter should unlock automatically.");
            Assert.IsNull(afterRec.ReadinessNote);
        }
        finally
        {
            Shaderless.Core.Games.RecorderManager.TestDataDirOverride = null;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public async Task LutrisUbisoftUuidCard_MatchesDisplayNameAndSlug_MigratesContainerState_AndDetectsProtonSwitchImmediately()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-ubi-uuid-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string xdgData = Path.Combine(home, ".local", "share");
        string xdgConfig = Path.Combine(home, ".config");
        string lutrisData = Path.Combine(xdgData, "lutris");
        string lutrisGamesCfg = Path.Combine(lutrisData, "games");
        string ubiPrefix = Path.Combine(home, "Games", "ubisoft-connect");
        string wildlandsDir = Path.Combine(ubiPrefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher", "games", "Tom Clancy's Ghost Recon Wildlands");
        string compatTools = Path.Combine(xdgData, "Steam", "compatibilitytools.d");
        string proton117 = Path.Combine(compatTools, "GE-Proton11-7-x86_64", "proton");
        string proton1112 = Path.Combine(compatTools, "GE-Proton11-12-x86_64", "proton");
        string dataDir = Path.Combine(root, "shaderless-data");
        string relLeaf = Path.Combine("ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");

        Directory.CreateDirectory(lutrisGamesCfg);
        Directory.CreateDirectory(wildlandsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(proton117)!);
        Directory.CreateDirectory(Path.GetDirectoryName(proton1112)!);
        Directory.CreateDirectory(dataDir);

        Shaderless.Core.Games.RecorderManager.TestDataDirOverride = dataDir;
        try
        {
            File.WriteAllText(proton117, "#!/bin/sh\n");
            File.WriteAllText(proton1112, "#!/bin/sh\n");

            string wildlandsExe = Path.Combine(wildlandsDir, "GRW.exe");
            File.WriteAllBytes(wildlandsExe, new byte[48_000]);
            File.WriteAllBytes(Path.Combine(wildlandsDir, "DataPC_patch_01.forge"), new byte[4096]);

            // Wine system.reg stores numeric Uplay ID 9010 + Official DisplayName with trademark symbol (®), NOT Ubisoft's GraphQL UUID
            File.WriteAllText(Path.Combine(ubiPrefix, "system.reg"), """
                WINE REGISTRY Version 2

                [Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\9010] 1793871116
                "InstallDir"="C:/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/games/Tom Clancy's Ghost Recon Wildlands/"

                [Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Uplay Install 9010] 1793873580
                "DisplayName"="Ghost Recon® Wildlands"
                """);

            File.WriteAllText(Path.Combine(lutrisGamesCfg, "ubisoft-connect-100.yml"), """
                game:
                  exe: drive_c/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe
                  prefix: /unused
                wine:
                  version: GE-Proton11-7-x86_64
                """);

            string dbPath = Path.Combine(lutrisData, "pga.db");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE games (
                        id INTEGER PRIMARY KEY,
                        slug TEXT,
                        name TEXT,
                        runner TEXT,
                        directory TEXT,
                        installed INTEGER,
                        service TEXT,
                        service_id TEXT,
                        configpath TEXT
                    );
                    INSERT INTO games VALUES (5, 'ubisoft-connect', 'Ubisoft Connect', 'wine', $dir, 1, NULL, NULL, 'ubisoft-connect-100');
                    """;
                cmd.Parameters.AddWithValue("$dir", ubiPrefix);
                cmd.ExecuteNonQuery();
            }

            // Phase 1: Only bare Ubisoft Connect container exists -> discovered as 5_ubisoft_9010 using GE-Proton11-7
            var lutrisSource = new Shaderless.Core.Games.LutrisLinuxSource(home, xdgData, xdgConfig);
            var phase1Games = await lutrisSource.DiscoverGamesAsync();
            Assert.HasCount(1, phase1Games);
            var containerGame = phase1Games[0];
            Assert.AreEqual("5_ubisoft_9010", containerGame.Id);
            Assert.AreEqual("Ghost Recon® Wildlands", containerGame.Name);
            Assert.AreEqual(proton117, ProtonRunner.ResolveLutrisProtonForGame(containerGame, null, home, xdgData, xdgConfig, allowSystemCompatTools: false));

            // Simulate user recording 1 PSO + compiling cache on GE-Proton11-7 under fallback ID 5_ubisoft_9010
            string oldRecDb = Shaderless.Core.Games.RecorderManager.RecordingPath("5_ubisoft_9010");
            Directory.CreateDirectory(Path.GetDirectoryName(oldRecDb)!);
            using (var fs = File.Create(oldRecDb))
            {
                byte[] fakeCompute = new byte[40];
                for (int i = 0; i < fakeCompute.Length; i++) fakeCompute[i] = (byte)(i + 1);
                Shaderless.Engine.Planning.PsoDb.Write(fs, 'C', fakeCompute);
            }

            string oldOwnSc = ProtonRunner.GetNonSteamShaderCachePath("5_ubisoft_9010", ubiPrefix);
            string oldOwnVkd3d = Path.Combine(oldOwnSc, "VKD3D_shader_cache");
            string oldOwnGlLeaf = Path.Combine(oldOwnSc, "nvidiav1", "GLCache", relLeaf);
            Directory.CreateDirectory(oldOwnVkd3d);
            Directory.CreateDirectory(oldOwnGlLeaf);

            byte[] fakeVkd3d = new byte[128];
            "VKD3D_PROTON_CACHE"u8.CopyTo(fakeVkd3d);
            File.WriteAllBytes(Path.Combine(oldOwnVkd3d, "vkd3d-proton.GRW.exe.cache"), fakeVkd3d);
            var (nvBin, nvToc) = BuildSyntheticCdvnCache(0x59, MakeNvdictRecord(0x59, 0x11223344u, 256), MakeZstdShaderRecord(7, 77, 0x11223344u, payloadBytes: 256));
            File.WriteAllBytes(Path.Combine(oldOwnGlLeaf, "steamapp_merged_shader_cache.bin"), nvBin);
            File.WriteAllBytes(Path.Combine(oldOwnGlLeaf, "steamapp_merged_shader_cache.toc"), nvToc);
            File.WriteAllText(Path.Combine(oldOwnSc, "shaderless_runtime.stamp"), $"{proton117}|default|615.00");

            ProtonRunner.InstallCachesWhereGameReads(containerGame, oldOwnVkd3d, Path.Combine(oldOwnSc, "nvidiav1", "GLCache"));
            Assert.IsTrue(File.Exists(Path.Combine(ubiPrefix, "GLCache", relLeaf, "shaderless_g5ubisoft9010_part1.bin")));

            var gm = new Shaderless.Core.Games.GameManager(new Shaderless.Core.Games.IGameSource[] { lutrisSource });
            string stamp117 = $"{proton117}|default";
            gm.SaveGameCompiled("5_ubisoft_9010", 4200, "615.00", stamp117, ProtonRunner.GetGameBuildStamp(containerGame));

            // Phase 2: User connects Lutris Ubisoft sidebar -> inserts row id=7 with GraphQL UUID service_id & switches card 7 to GE-Proton11-12!
            File.WriteAllText(Path.Combine(lutrisGamesCfg, "ghost-recon-wildlands-200.yml"), """
                game:
                  args: uplay://launch/429a8e76-f469-4b2e-9d74-c1b35ee0ddad
                  exe: drive_c/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe
                  prefix: /unused
                service: ubisoft
                service_id: 429a8e76-f469-4b2e-9d74-c1b35ee0ddad
                slug: ghost-recon-wildlands-ubisoft
                wine:
                  version: GE-Proton11-12-x86_64
                """);

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO games VALUES (
                        7,
                        'ghost-recon-wildlands',
                        'Ghost Recon® Wildlands',
                        'wine',
                        $dir,
                        1,
                        'ubisoft',
                        '429a8e76-f469-4b2e-9d74-c1b35ee0ddad',
                        'ghost-recon-wildlands-200'
                    );
                    """;
                cmd.Parameters.AddWithValue("$dir", ubiPrefix);
                cmd.ExecuteNonQuery();
            }

            var diag2 = await lutrisSource.DiagnoseAsync();
            Assert.HasCount(1, diag2.DiscoveredGames, "Must deduplicate container child once explicit Ubisoft service card exists.");
            Assert.IsFalse(diag2.SkippedItems.Any(s => s.NameOrId.Contains("Ghost Recon", StringComparison.Ordinal)),
                "Explicit Lutris Ubisoft UUID card must match installed game via DisplayName/slug and never be skipped.");

            var explicitGame = diag2.DiscoveredGames[0];
            Assert.AreEqual("7", explicitGame.Id);
            Assert.AreEqual(proton1112, ProtonRunner.ResolveLutrisProtonForGame(explicitGame, null, home, xdgData, xdgConfig, allowSystemCompatTools: false),
                "Explicit Ubisoft card ID 7 must read GE-Proton11-12 from its own YAML even while Ubisoft Connect card 5 stays on GE-Proton11-7.");

            // Verify recording, shadercache, NVIDIA shards, and state file were seamlessly migrated from 5_ubisoft_9010 -> 7
            Assert.IsFalse(Directory.Exists(Shaderless.Core.Games.RecorderManager.GameDir("5_ubisoft_9010")));
            Assert.AreEqual(1, Shaderless.Core.Games.RecorderManager.RecordedPsos(explicitGame), "Recorded PSOs must survive container-to-card ID migration.");
            Assert.IsFalse(File.Exists(Path.Combine(ubiPrefix, "GLCache", relLeaf, "shaderless_g5ubisoft9010_part1.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(ubiPrefix, "GLCache", relLeaf, "shaderless_g7_part1.bin")));
            Assert.IsTrue(Directory.Exists(ProtonRunner.GetNonSteamShaderCachePath("7", ubiPrefix)));

            // Verify GameManager detects Proton mismatch against migrated stamp when Proton changed from 11-7 to 11-12
            string currentStamp1112 = $"{proton1112}|default";
            Assert.AreEqual(
                "Proton updated",
                Shaderless.Core.Games.GameManager.EvaluateStaleReason("615.00", stamp117, null, "615.00", currentStamp1112, null));
        }
        finally
        {
            Shaderless.Core.Games.RecorderManager.TestDataDirOverride = null;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void LinuxCaseSensitiveVkd3dCache_MirrorsRuntimeExeCasing_LowercaseUbisoftPlus_AndPromotesWriteFilesForAnyGame()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-case-vkd3d-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Case A: Generic non-Ubisoft game ("StarlightOdyssey.exe" on disk, launched as "starlightodyssey.exe" in log
            //         and leaving "vkd3d-proton.STARLIGHTODYSSEY.exe.cache.write" in VKD3D_shader_cache)
            string prefixA = Path.Combine(root, "prefixA");
            string installA = Path.Combine(prefixA, "drive_c", "Games", "Starlight Odyssey");
            Directory.CreateDirectory(installA);
            string exeA = Path.Combine(installA, "StarlightOdyssey.exe");
            File.WriteAllBytes(exeA, new byte[40_000]);
            File.WriteAllText(Path.Combine(installA, "shaderless.log"),
                "[ 1.2s] loaded into C:\\Games\\Starlight Odyssey\\starlightodyssey.exe (Linux/Proton recorder)\n");

            var gameA = MakeGame("101", GameSource.Lutris, installA, exeA) with
            {
                Name = "Starlight Odyssey",
                PrefixPath = prefixA,
                GlCachePath = prefixA
            };

            string ownScA = ProtonRunner.GetNonSteamShaderCachePath("101", prefixA);
            string ownVkd3dA = Path.Combine(ownScA, "VKD3D_shader_cache");
            string sharedVkd3dA = Path.Combine(prefixA, "shadercache", "VKD3D_shader_cache");
            Directory.CreateDirectory(ownVkd3dA);
            Directory.CreateDirectory(sharedVkd3dA);

            byte[] validVkd3d = new byte[128];
            "VKD3D_PROTON_CACHE"u8.CopyTo(validVkd3d);
            File.WriteAllBytes(Path.Combine(ownVkd3dA, "vkd3d-proton.StarlightOdyssey.exe.cache"), validVkd3d);
            File.WriteAllBytes(Path.Combine(sharedVkd3dA, "vkd3d-proton.STARLIGHTODYSSEY.exe.cache.write"), validVkd3d);

            ProtonRunner.InstallCachesWhereGameReads(gameA, ownVkd3dA, Path.Combine(ownScA, "nvidiav1", "GLCache"));

            Assert.IsFalse(File.Exists(Path.Combine(sharedVkd3dA, "vkd3d-proton.STARLIGHTODYSSEY.exe.cache.write")),
                "Runtime .cache.write must be promoted and removed.");
            Assert.IsTrue(File.Exists(Path.Combine(sharedVkd3dA, "vkd3d-proton.StarlightOdyssey.exe.cache")));
            Assert.IsTrue(File.Exists(Path.Combine(sharedVkd3dA, "vkd3d-proton.starlightodyssey.exe.cache")),
                "Must mirror exact lowercase runtime exe casing from shaderless.log.");
            Assert.IsTrue(File.Exists(Path.Combine(sharedVkd3dA, "vkd3d-proton.STARLIGHTODYSSEY.exe.cache")),
                "Must mirror exact uppercase runtime exe casing observed in VKD3D_shader_cache.");

            // Case-mismatched .write created later must also be cleaned by RemoveInstalledCaches
            File.WriteAllBytes(Path.Combine(sharedVkd3dA, "vkd3d-proton.starlightodyssey.exe.cache.write"), new byte[] { 1, 2, 3 });
            ProtonRunner.RemoveInstalledCaches(prefixA, "101");
            Assert.IsFalse(File.Exists(Path.Combine(sharedVkd3dA, "vkd3d-proton.StarlightOdyssey.exe.cache")));
            Assert.IsFalse(File.Exists(Path.Combine(sharedVkd3dA, "vkd3d-proton.starlightodyssey.exe.cache")));
            Assert.IsFalse(File.Exists(Path.Combine(sharedVkd3dA, "vkd3d-proton.STARLIGHTODYSSEY.exe.cache")));
            Assert.IsFalse(File.Exists(Path.Combine(sharedVkd3dA, "vkd3d-proton.starlightodyssey.exe.cache.write")));

            // Case B: Paired <Stem>.exe + <Stem>_Plus.exe game -> mirrors <Stem>, <Stem>_Plus, AND lowercase <Stem>_plus automatically
            string prefixB = Path.Combine(root, "prefixB");
            string installB = Path.Combine(prefixB, "drive_c", "Games", "Mirage Quest");
            Directory.CreateDirectory(installB);
            string plainB = Path.Combine(installB, "MirageQuest.exe");
            string plusB = Path.Combine(installB, "MirageQuest_Plus.exe");
            File.WriteAllBytes(plainB, new byte[44_000]);
            File.WriteAllBytes(plusB, new byte[49_000]);

            var gameB = MakeGame("202", GameSource.Lutris, installB, plainB) with
            {
                Name = "Mirage Quest",
                PrefixPath = prefixB,
                GlCachePath = prefixB
            };

            string ownScB = ProtonRunner.GetNonSteamShaderCachePath("202", prefixB);
            string ownVkd3dB = Path.Combine(ownScB, "VKD3D_shader_cache");
            string sharedVkd3dB = Path.Combine(prefixB, "shadercache", "VKD3D_shader_cache");
            Directory.CreateDirectory(ownVkd3dB);
            File.Create(Path.Combine(ownVkd3dB, "vkd3d-proton.MirageQuest.exe.cache")).Dispose();
            File.WriteAllBytes(Path.Combine(ownVkd3dB, "vkd3d-proton.MirageQuest.exe.cache"), validVkd3d);

            ProtonRunner.InstallCachesWhereGameReads(gameB, ownVkd3dB, Path.Combine(ownScB, "nvidiav1", "GLCache"));
            Assert.IsTrue(File.Exists(Path.Combine(sharedVkd3dB, "vkd3d-proton.MirageQuest.exe.cache")));
            Assert.IsTrue(File.Exists(Path.Combine(sharedVkd3dB, "vkd3d-proton.MirageQuest_Plus.exe.cache")));
            Assert.IsTrue(File.Exists(Path.Combine(sharedVkd3dB, "vkd3d-proton.MirageQuest_plus.exe.cache")),
                "Must mirror lowercase _plus.exe even before the first launch.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void ReleaseVersion_Is030Beta()
    {
        Assert.AreEqual("0.3.0-beta", Shaderless.Core.AppPaths.Version);
    }

    [TestMethod]
    public void LegacyProxyAndFiles_AreRecognized_UpgradedInPlace_AndImported()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-upgrade-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(root, "data");
        string gameA = Path.Combine(root, "GameOldV01");
        string gameB = Path.Combine(root, "GameV023");
        string gameMod = Path.Combine(root, "GameOptiScaler");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(gameA);
        Directory.CreateDirectory(gameB);
        Directory.CreateDirectory(gameMod);

        Shaderless.Core.Games.RecorderManager.TestDataDirOverride = dataDir;
        try
        {
            // Helper: build a fake old proxy containing only the legacy SCSKiller_StartWarm export marker
            byte[] legacyProxyBytes = System.Text.Encoding.ASCII.GetBytes("MZ_FAKE_PE_HEADER_SCSKiller_StartWarm_END");
            byte[] thirdPartyModBytes = System.Text.Encoding.ASCII.GetBytes("MZ_FAKE_OPTISCALER_D3D12_WRAPPER");

            // Case 1: Old v0.1/v0.2.0 game folder (legacy d3d12.dll + scskiller.ini + scskiller.armed + scskiller.db)
            string exeA = Path.Combine(gameA, "OldGame.exe");
            File.WriteAllBytes(exeA, new byte[64_000]);
            string dllA = Path.Combine(gameA, "d3d12.dll");
            File.WriteAllBytes(dllA, legacyProxyBytes);
            File.WriteAllText(Path.Combine(gameA, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
            File.WriteAllText(Path.Combine(gameA, "scskiller.armed"), "[scskiller]\r\narmed=1\r\n");

            // Write 1 valid Compute PSO record ('C', 44 bytes: rootSig[20] + csSha1[20] + flags[4]) + its blobs into scskiller.db
            using (var ms = new MemoryStream())
            {
                byte[] rsBlob = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                byte[] csBlob = new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 };
                byte[] rsSha = System.Security.Cryptography.SHA1.HashData(rsBlob);
                byte[] csSha = System.Security.Cryptography.SHA1.HashData(csBlob);

                static void WriteRec(Stream s, char tag, byte[] payload)
                {
                    s.WriteByte((byte)tag);
                    Span<byte> len = stackalloc byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)payload.Length);
                    s.Write(len);
                    s.Write(payload);
                }

                WriteRec(ms, 'B', rsSha.Concat(rsBlob).ToArray());
                WriteRec(ms, 'B', csSha.Concat(csBlob).ToArray());
                WriteRec(ms, 'C', rsSha.Concat(csSha).Concat(new byte[4]).ToArray());
                File.WriteAllBytes(Path.Combine(gameA, "scskiller.db"), ms.ToArray());
            }

            var infoA = MakeGame("old_1", GameSource.Steam, gameA, exeA);
            Assert.IsTrue(Shaderless.Engine.App.ShaderEngine.IsOurProxy(dllA), "Legacy d3d12.dll must still be recognized as ours.");
            Assert.IsTrue(Shaderless.Engine.App.ShaderEngine.IsLegacyProxy(dllA), "Legacy d3d12.dll must be flagged for automatic upgrade.");
            Assert.IsTrue(Shaderless.Core.Games.RecorderManager.IsInstalled(infoA), "Record mode must stay ON across upgrade.");

            int importedA = Shaderless.Core.Games.RecorderManager.Import(infoA, isGameRunning: _ => false);
            Assert.AreEqual(1, importedA, "Legacy scskiller.db must migrate to shaderless.db and import cleanly.");
            Assert.AreEqual(1, Shaderless.Core.Games.RecorderManager.RecordedPsos(infoA));
            Assert.IsFalse(File.Exists(Path.Combine(gameA, "scskiller.ini")));
            Assert.IsFalse(File.Exists(Path.Combine(gameA, "scskiller.armed")));
            Assert.IsTrue(File.Exists(Path.Combine(gameA, "shaderless.ini")));
            Assert.IsTrue(File.Exists(Path.Combine(gameA, "shaderless.armed")));
            Assert.IsTrue(Shaderless.Engine.App.ShaderEngine.IsOurProxy(dllA));
            Assert.IsFalse(Shaderless.Engine.App.ShaderEngine.IsLegacyProxy(dllA), "Legacy d3d12.dll must be upgraded in-place to Shaderless_StartWarm.");

            // Case 2: v0.2.3-beta game folder (shaderless.ini + shaderless.armed ALREADY exist, but d3d12.dll still has SCSKiller_StartWarm)
            string exeB = Path.Combine(gameB, "Borderlands4.exe");
            File.WriteAllBytes(exeB, new byte[64_000]);
            string dllB = Path.Combine(gameB, "d3d12.dll");
            File.WriteAllBytes(dllB, legacyProxyBytes);
            File.WriteAllText(Path.Combine(gameB, "shaderless.ini"), Shaderless.Core.Games.RecorderManager.IniText);
            File.WriteAllText(Path.Combine(gameB, "shaderless.armed"), "[shaderless]\r\narmed=1\r\n");

            var infoB = MakeGame("v023_bl4", GameSource.Heroic, gameB, exeB);
            Assert.IsTrue(Shaderless.Core.Games.RecorderManager.IsInstalled(infoB));
            _ = Shaderless.Core.Games.RecorderManager.RecordedPsos(infoB); // triggers MigrateLegacyFiles on scan!
            Assert.IsTrue(Shaderless.Engine.App.ShaderEngine.IsOurProxy(dllB));
            Assert.IsFalse(Shaderless.Engine.App.ShaderEngine.IsLegacyProxy(dllB), "v0.2.3 d3d12.dll must also auto-upgrade on scan even when shaderless.ini already exists.");

            // Case 3: Third-party d3d12.dll (OptiScaler / ReShade) must NEVER be treated as ours or overwritten
            string exeMod = Path.Combine(gameMod, "ModdedGame.exe");
            File.WriteAllBytes(exeMod, new byte[64_000]);
            string dllMod = Path.Combine(gameMod, "d3d12.dll");
            File.WriteAllBytes(dllMod, thirdPartyModBytes);
            var infoMod = MakeGame("mod_1", GameSource.Steam, gameMod, exeMod);
            Assert.IsFalse(Shaderless.Core.Games.RecorderManager.IsInstalled(infoMod));
            Shaderless.Core.Games.RecorderManager.MigrateLegacyFiles(gameMod);
            CollectionAssert.AreEqual(thirdPartyModBytes, File.ReadAllBytes(dllMod));
        }
        finally
        {
            Shaderless.Core.Games.RecorderManager.TestDataDirOverride = null;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [TestMethod]
    public void RecorderUpgrade_UsesAtomicRename_AndDefersWhenMemoryMappedInProcMaps()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-atomic-upg-" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(root, "Borderlands 4", "OakGame", "Binaries", "Win64");
        string procRoot = Path.Combine(root, "fake_proc");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(procRoot);

        try
        {
            byte[] legacyBytes = System.Text.Encoding.ASCII.GetBytes("MZ_LEGACY_PROXY_SCSKiller_StartWarm_ORIGINAL_BYTES_1234567890");
            byte[] newBytes = System.Text.Encoding.ASCII.GetBytes("MZ_NEW_PROXY_Shaderless_StartWarm_UPGRADED_BYTES_ABCDEFGHIJ");
            string srcDll = Path.Combine(root, "new_d3d12.dll");
            string dstDll = Path.Combine(gameDir, "d3d12.dll");
            File.WriteAllBytes(srcDll, newBytes);
            File.WriteAllBytes(dstDll, legacyBytes);

            bool hasBeforeId = ProtonRunner.TryGetLinuxFileIdentity(dstDll, out var beforeId, out _);

            // 1. Hold old d3d12.dll open (simulating an active mapping / open descriptor on the old inode),
            //    replace atomically, and verify the old handle STILL reads the uncorrupted original bytes on a distinct inode!
            using (var openOldInode = new FileStream(dstDll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Shaderless.Core.Games.RecorderManager.ReplaceFileAtomically(srcDll, dstDll);

                if (OperatingSystem.IsLinux() && hasBeforeId && ProtonRunner.TryGetLinuxFileIdentity(dstDll, out var afterId, out _))
                {
                    Assert.AreNotEqual(beforeId.Ino, afterId.Ino, "Atomic replacement must allocate a fresh Linux inode so active mmap pages are never truncated in-place.");
                }

                byte[] readFromOldHandle = new byte[legacyBytes.Length];
                int n = openOldInode.Read(readFromOldHandle, 0, readFromOldHandle.Length);
                Assert.AreEqual(legacyBytes.Length, n);
                CollectionAssert.AreEqual(legacyBytes, readFromOldHandle, "Process holding previous inode must still read original uncorrupted bytes.");
            }

            CollectionAssert.AreEqual(newBytes, File.ReadAllBytes(dstDll));
            Assert.AreEqual(0, Directory.GetFiles(gameDir, "*.tmp").Length, "No temporary files should remain after atomic replacement.");

            // 2. Verify /proc/<pid>/maps detection handles spaces in game path, container inode matches, and ignores selfPid
            Assert.IsFalse(Shaderless.Core.Games.RecorderManager.IsFileMappedByExternalProcess(dstDll, procRoot, selfPid: 1000));

            // Self PID mapping must be ignored
            Directory.CreateDirectory(Path.Combine(procRoot, "1000"));
            File.WriteAllText(
                Path.Combine(procRoot, "1000", "maps"),
                $"7f0000000000-7f0000010000 r-xp 00000000 103:02 999999                   {Path.GetFullPath(dstDll)}\n");
            Assert.IsFalse(Shaderless.Core.Games.RecorderManager.IsFileMappedByExternalProcess(dstDll, procRoot, selfPid: 1000));

            // External Wine/Proton PID mapping exact path (with space in "Borderlands 4") -> true
            Directory.CreateDirectory(Path.Combine(procRoot, "4242"));
            File.WriteAllText(
                Path.Combine(procRoot, "4242", "maps"),
                $"7f0000000000-7f0000010000 r-xp 00000000 103:02 12345                    {Path.GetFullPath(dstDll)}\n");
            Assert.IsTrue(Shaderless.Core.Games.RecorderManager.IsFileMappedByExternalProcess(dstDll, procRoot, selfPid: 1000));

            // Suffix collision ("/not_Borderlands 4/.../d3d12.dll" with wrong inode) -> false
            File.WriteAllText(
                Path.Combine(procRoot, "4242", "maps"),
                $"7f0000000000-7f0000010000 r-xp 00000000 103:02 1                        /prefix{Path.GetFullPath(dstDll)}\n");
            Assert.IsFalse(Shaderless.Core.Games.RecorderManager.IsFileMappedByExternalProcess(dstDll, procRoot, selfPid: 1000));

            // Container bind-mount path ("/run/host/game/d3d12.dll") with matching real Linux inode -> true
            if (OperatingSystem.IsLinux() && ProtonRunner.TryGetLinuxFileIdentity(dstDll, out var curId, out _))
            {
                File.WriteAllText(
                    Path.Combine(procRoot, "4242", "maps"),
                    $"7f0000000000-7f0000010000 r-xp 00000000 103:02 {curId.Ino}              /run/host/mnt/d3d12.dll\n");
                Assert.IsTrue(Shaderless.Core.Games.RecorderManager.IsFileMappedByExternalProcess(dstDll, procRoot, selfPid: 1000));
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static (Dictionary<string, byte[]> BlobsByHash, List<Shaderless.Engine.Planning.PsoDb.Rec> Psos) BuildSyntheticD3D12ComputePsos(int count)
    {
        // Valid 88-byte D3D12SerializeRootSignature v1.0 blob: 1 UAV root descriptor at register(u0, space0)
        byte[] rootSigDxbc =
        [
            0x44, 0x58, 0x42, 0x43, 0x61, 0xAF, 0x00, 0x7C, 0x04, 0x8B, 0xBD, 0xED, 0xBF, 0xEF, 0x29, 0xA3,
            0x4A, 0xE0, 0xF4, 0xEE, 0x01, 0x00, 0x00, 0x00, 0x58, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x24, 0x00, 0x00, 0x00, 0x52, 0x54, 0x53, 0x30, 0x2C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];

        // Valid 288-byte stripped D3DCompile cs_5_0 DXBC template:
        //   RWStructuredBuffer<uint> OutBuf : register(u0);
        //   [numthreads(8, 1, 1)] void main(uint3 tid : SV_DispatchThreadID) {
        //       uint x = tid.x ^ 0x13579BDFu; x = (x * 0x27D4EB2Du) + (x >> 15); OutBuf[tid.x] = x;
        //   }
        // Immediate 0x13579BDFu sits at offset 160; immediate 0x27D4EB2Du sits at offset 192.
        byte[] csDxbcTemplate =
        [
            0x44, 0x58, 0x42, 0x43, 0x72, 0x43, 0x2E, 0xBB, 0xDE, 0x5B, 0x0C, 0xD2, 0xA2, 0xBA, 0x9E, 0x11,
            0xEC, 0xD0, 0xD2, 0xF4, 0x01, 0x00, 0x00, 0x00, 0x20, 0x01, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
            0x2C, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x00, 0x00, 0x4C, 0x00, 0x00, 0x00, 0x49, 0x53, 0x47, 0x4E,
            0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x4F, 0x53, 0x47, 0x4E,
            0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x53, 0x48, 0x44, 0x52,
            0xCC, 0x00, 0x00, 0x00, 0x50, 0x00, 0x05, 0x00, 0x33, 0x00, 0x00, 0x00, 0x9B, 0x00, 0x00, 0x04,
            0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x68, 0x00, 0x00, 0x02,
            0x02, 0x00, 0x00, 0x00, 0x9E, 0x00, 0x00, 0x04, 0x01, 0xE0, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x5F, 0x00, 0x00, 0x02, 0x72, 0x00, 0x02, 0x00, 0x57, 0x00, 0x00, 0x06,
            0x12, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x02, 0x00, 0x01, 0x40, 0x00, 0x00,
            0xDF, 0x9B, 0x57, 0x13, 0x26, 0x00, 0x00, 0x08, 0x00, 0xD0, 0x00, 0x00, 0x12, 0x00, 0x10, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00,
            0x2D, 0xEB, 0xD4, 0x27, 0x55, 0x00, 0x00, 0x07, 0x12, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x06, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00,
            0x1E, 0x00, 0x00, 0x07, 0x12, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x10, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0xA8, 0x00, 0x00, 0x08,
            0x12, 0xE0, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x02, 0x00, 0x01, 0x40, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3E, 0x00, 0x00, 0x01
        ];

        var blobsByHash = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var psos = new List<Shaderless.Engine.Planning.PsoDb.Rec>(count);

        byte[] rsSha = System.Security.Cryptography.SHA1.HashData(rootSigDxbc);
        string rsHex = Shaderless.Engine.Planning.PsoDb.Hex(rsSha);
        blobsByHash[rsHex] = rootSigDxbc.PrependRange(rsSha);

        for (int i = 0; i < count; i++)
        {
            byte[] cs = (byte[])csDxbcTemplate.Clone();
            if (i == 0)
            {
                byte[] origChk = cs[4..20];
                UpdateDxbcChecksum(cs);
                CollectionAssert.AreEqual(origChk, cs[4..20], "DXBC MD5 checksum helper must match D3DCompile byte-for-byte!");
            }
            else
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cs.AsSpan(160, 4), 0x13579BDFu + (uint)i * 0x01010103u);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cs.AsSpan(192, 4), 0x27D4EB2Du + (uint)i * 0x9E3779B1u);
                UpdateDxbcChecksum(cs);
            }

            byte[] csSha = System.Security.Cryptography.SHA1.HashData(cs);
            string csHex = Shaderless.Engine.Planning.PsoDb.Hex(csSha);
            blobsByHash[csHex] = cs.PrependRange(csSha);

            // 'C' (Compute PSO) canonical 48-byte payload: rsSha[20] + csSha[20] + NodeMask(u32=0) + Flags(u32=0)
            byte[] cPayload = new byte[48];
            rsSha.CopyTo(cPayload, 0);
            csSha.CopyTo(cPayload, 20);
            psos.Add(new Shaderless.Engine.Planning.PsoDb.Rec('C', cPayload));
        }

        return (blobsByHash, psos);
    }

    private static void UpdateDxbcChecksum(byte[] dxbc)
    {
        ReadOnlySpan<byte> data = dxbc.AsSpan(20);
        uint a = 0x67452301u, b = 0xefcdab89u, c = 0x98badcfeu, d = 0x10325476u;
        uint bitLen = (uint)(data.Length << 3);
        Span<byte> blk = stackalloc byte[64];

        int off = 0;
        while (data.Length - off >= 64)
        {
            Md5TransformDxbc(ref a, ref b, ref c, ref d, data.Slice(off, 64));
            off += 64;
        }

        int rem = data.Length - off;
        blk.Clear();
        data.Slice(off, rem).CopyTo(blk);
        blk[rem] = 0x80;
        int count = rem + 1;
        int padding = 64 - count;

        if (padding < 8)
        {
            Md5TransformDxbc(ref a, ref b, ref c, ref d, blk);
            blk.Clear();
        }
        else
        {
            blk.Slice(0, count).CopyTo(blk.Slice(4, count));
            blk.Slice(0, 4).Clear();
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(blk.Slice(0, 4), bitLen);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(blk.Slice(60, 4), (bitLen >> 2) | 1u);
        Md5TransformDxbc(ref a, ref b, ref c, ref d, blk);

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dxbc.AsSpan(4, 4), a);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dxbc.AsSpan(8, 4), b);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dxbc.AsSpan(12, 4), c);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dxbc.AsSpan(16, 4), d);
    }

    private static void Md5TransformDxbc(ref uint stateA, ref uint stateB, ref uint stateC, ref uint stateD, ReadOnlySpan<byte> block)
    {
        Span<uint> x = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
            x[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));

        uint a = stateA, b = stateB, c = stateC, d = stateD;
        static uint Rol(uint v, int s) => (v << s) | (v >> (32 - s));
        static void F1(ref uint w, uint x, uint y, uint z, uint data, int s) => w = Rol(w + (z ^ (x & (y ^ z))) + data, s) + x;
        static void F2(ref uint w, uint x, uint y, uint z, uint data, int s) => w = Rol(w + (y ^ (z & (x ^ y))) + data, s) + x;
        static void F3(ref uint w, uint x, uint y, uint z, uint data, int s) => w = Rol(w + (x ^ y ^ z) + data, s) + x;
        static void F4(ref uint w, uint x, uint y, uint z, uint data, int s) => w = Rol(w + (y ^ (x | ~z)) + data, s) + x;

        F1(ref a, b, c, d, x[0] + 0xd76aa478u, 7);  F1(ref d, a, b, c, x[1] + 0xe8c7b756u, 12); F1(ref c, d, a, b, x[2] + 0x242070dbu, 17); F1(ref b, c, d, a, x[3] + 0xc1bdceeeu, 22);
        F1(ref a, b, c, d, x[4] + 0xf57c0fafu, 7);  F1(ref d, a, b, c, x[5] + 0x4787c62au, 12); F1(ref c, d, a, b, x[6] + 0xa8304613u, 17); F1(ref b, c, d, a, x[7] + 0xfd469501u, 22);
        F1(ref a, b, c, d, x[8] + 0x698098d8u, 7);  F1(ref d, a, b, c, x[9] + 0x8b44f7afu, 12); F1(ref c, d, a, b, x[10] + 0xffff5bb1u, 17); F1(ref b, c, d, a, x[11] + 0x895cd7beu, 22);
        F1(ref a, b, c, d, x[12] + 0x6b901122u, 7); F1(ref d, a, b, c, x[13] + 0xfd987193u, 12); F1(ref c, d, a, b, x[14] + 0xa679438eu, 17); F1(ref b, c, d, a, x[15] + 0x49b40821u, 22);

        F2(ref a, b, c, d, x[1] + 0xf61e2562u, 5);  F2(ref d, a, b, c, x[6] + 0xc040b340u, 9);  F2(ref c, d, a, b, x[11] + 0x265e5a51u, 14); F2(ref b, c, d, a, x[0] + 0xe9b6c7aau, 20);
        F2(ref a, b, c, d, x[5] + 0xd62f105du, 5);  F2(ref d, a, b, c, x[10] + 0x02441453u, 9); F2(ref c, d, a, b, x[15] + 0xd8a1e681u, 14); F2(ref b, c, d, a, x[4] + 0xe7d3fbc8u, 20);
        F2(ref a, b, c, d, x[9] + 0x21e1cde6u, 5);  F2(ref d, a, b, c, x[14] + 0xc33707d6u, 9); F2(ref c, d, a, b, x[3] + 0xf4d50d87u, 14);  F2(ref b, c, d, a, x[8] + 0x455a14edu, 20);
        F2(ref a, b, c, d, x[13] + 0xa9e3e905u, 5); F2(ref d, a, b, c, x[2] + 0xfcefa3f8u, 9);  F2(ref c, d, a, b, x[7] + 0x676f02d9u, 14);  F2(ref b, c, d, a, x[12] + 0x8d2a4c8au, 20);

        F3(ref a, b, c, d, x[5] + 0xfffa3942u, 4);  F3(ref d, a, b, c, x[8] + 0x8771f681u, 11); F3(ref c, d, a, b, x[11] + 0x6d9d6122u, 16); F3(ref b, c, d, a, x[14] + 0xfde5380cu, 23);
        F3(ref a, b, c, d, x[1] + 0xa4beea44u, 4);  F3(ref d, a, b, c, x[4] + 0x4bdecfa9u, 11); F3(ref c, d, a, b, x[7] + 0xf6bb4b60u, 16);  F3(ref b, c, d, a, x[10] + 0xbebfbc70u, 23);
        F3(ref a, b, c, d, x[13] + 0x289b7ec6u, 4); F3(ref d, a, b, c, x[0] + 0xeaa127fau, 11); F3(ref c, d, a, b, x[3] + 0xd4ef3085u, 16);  F3(ref b, c, d, a, x[6] + 0x04881d05u, 23);
        F3(ref a, b, c, d, x[9] + 0xd9d4d039u, 4);  F3(ref d, a, b, c, x[12] + 0xe6db99e5u, 11); F3(ref c, d, a, b, x[15] + 0x1fa27cf8u, 16); F3(ref b, c, d, a, x[2] + 0xc4ac5665u, 23);

        F4(ref a, b, c, d, x[0] + 0xf4292244u, 6);  F4(ref d, a, b, c, x[7] + 0x432aff97u, 10); F4(ref c, d, a, b, x[14] + 0xab9423a7u, 15); F4(ref b, c, d, a, x[5] + 0xfc93a039u, 21);
        F4(ref a, b, c, d, x[12] + 0x655b59c3u, 6); F4(ref d, a, b, c, x[3] + 0x8f0ccc92u, 10); F4(ref c, d, a, b, x[10] + 0xffeff47du, 15); F4(ref b, c, d, a, x[1] + 0x85845dd1u, 21);
        F4(ref a, b, c, d, x[8] + 0x6fa87e4fu, 6);  F4(ref d, a, b, c, x[15] + 0xfe2ce6e0u, 10); F4(ref c, d, a, b, x[6] + 0xa3014314u, 15);  F4(ref b, c, d, a, x[13] + 0x4e0811a1u, 21);
        F4(ref a, b, c, d, x[4] + 0xf7537e82u, 6);  F4(ref d, a, b, c, x[11] + 0xbd3af235u, 10); F4(ref c, d, a, b, x[2] + 0x2ad7d2bbu, 15);  F4(ref b, c, d, a, x[9] + 0xeb86d391u, 21);

        stateA += a; stateB += b; stateC += c; stateD += d;
    }

    [TestMethod]
    public void GameLauncher_BuildLaunchSpec_Steam_NativeAndFlatpak_UsesCorrectProtocolAndFallbacks()
    {
        var nativeSteam = MakeGame("2101960", GameSource.Steam, "/games/Cronos", "/games/Cronos/Cronos-Win64-Shipping.exe");
        var specXdg = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(
            nativeSteam,
            cmd => cmd == "xdg-open");
        Assert.AreEqual("xdg-open", specXdg.FileName);
        CollectionAssert.AreEqual(new[] { "steam://rungameid/2101960" }, specXdg.Arguments.ToArray());

        var specSteamFallback = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(
            nativeSteam,
            cmd => cmd == "steam");
        Assert.AreEqual("steam", specSteamFallback.FileName);
        CollectionAssert.AreEqual(new[] { "steam://rungameid/2101960" }, specSteamFallback.Arguments.ToArray());

        var flatpakSteam = nativeSteam with { Id = "flatpak_2101960", IsFlatpak = true };
        var specFlatpak = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(
            flatpakSteam,
            cmd => cmd == "flatpak");
        Assert.AreEqual("flatpak", specFlatpak.FileName);
        CollectionAssert.AreEqual(
            new[] { "run", "com.valvesoftware.Steam", "steam://rungameid/2101960" },
            specFlatpak.Arguments.ToArray());
    }

    [TestMethod]
    public async Task GameLauncher_BuildLaunchSpec_Heroic_NativeAndFlatpak_UsesRunnerAndAppName()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-heroic-launch-" + Guid.NewGuid().ToString("N"));
        try
        {
            string heroicCfg = Path.Combine(root, ".config", "heroic");
            string legDir = Path.Combine(heroicCfg, "legendaryConfig", "legendary");
            string sideDir = Path.Combine(heroicCfg, "sideload_apps");
            string gogDir = Path.Combine(heroicCfg, "gog_store");
            string g1 = Path.Combine(root, "games", "EpicGame");
            string g2 = Path.Combine(root, "games", "SideGame");
            string g3 = Path.Combine(root, "games", "GogGame");
            Directory.CreateDirectory(legDir);
            Directory.CreateDirectory(sideDir);
            Directory.CreateDirectory(gogDir);
            Directory.CreateDirectory(g1);
            Directory.CreateDirectory(g2);
            Directory.CreateDirectory(g3);
            File.WriteAllText(Path.Combine(g1, "EpicGame.exe"), "x");
            File.WriteAllText(Path.Combine(g2, "SideGame.exe"), "x");
            File.WriteAllText(Path.Combine(g3, "GogGame.exe"), "x");

            File.WriteAllText(Path.Combine(legDir, "installed.json"), $$"""
            {
              "Sugar": { "title": "Rocket League", "install_path": "{{g1.Replace("\\", "\\\\")}}", "executable": "EpicGame.exe", "is_dlc": false }
            }
            """);
            File.WriteAllText(Path.Combine(sideDir, "library.json"), $$"""
            {
              "games": [
                { "app_name": "side_123", "title": "Side App", "is_installed": true, "folder_name": "{{g2.Replace("\\", "\\\\")}}", "install": { "executable": "{{Path.Combine(g2, "SideGame.exe").Replace("\\", "\\\\")}}", "is_dlc": false } }
              ]
            }
            """);
            File.WriteAllText(Path.Combine(gogDir, "installed.json"), $$"""
            {
              "installed": [
                { "appName": "1207658924", "install_path": "{{g3.Replace("\\", "\\\\")}}", "is_dlc": false }
              ]
            }
            """);

            var source = new Shaderless.Core.Games.HeroicLinuxSource(homeDir: root, xdgConfigHome: Path.Combine(root, ".config"));
            var discovered = await source.DiscoverGamesAsync();
            Assert.AreEqual(3, discovered.Count);

            var epic = discovered.Single(d => d.Id == "Sugar");
            var side = discovered.Single(d => d.Id == "side_123");
            var gog = discovered.Single(d => d.Id == "1207658924");
            Assert.AreEqual("legendary", epic.LauncherRunner);
            Assert.AreEqual("sideload", side.LauncherRunner);
            Assert.AreEqual("gog", gog.LauncherRunner);

            var epicSpec = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(epic, cmd => cmd == "xdg-open");
            Assert.AreEqual("xdg-open", epicSpec.FileName);
            CollectionAssert.AreEqual(new[] { "heroic://launch/legendary/Sugar" }, epicSpec.Arguments.ToArray());

            var sideFallbackSpec = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(side, cmd => cmd == "heroic");
            Assert.AreEqual("heroic", sideFallbackSpec.FileName);
            CollectionAssert.AreEqual(new[] { "heroic://launch/sideload/side_123" }, sideFallbackSpec.Arguments.ToArray());

            var gogFlatpakSpec = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(
                gog with { Id = "flatpak_1207658924", IsFlatpak = true },
                cmd => cmd == "flatpak");
            Assert.AreEqual("flatpak", gogFlatpakSpec.FileName);
            CollectionAssert.AreEqual(
                new[] { "run", "com.heroicgameslauncher.hgl", "heroic://launch/gog/1207658924" },
                gogFlatpakSpec.Arguments.ToArray());

            var noRunnerSpec = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(
                epic with { LauncherRunner = null },
                cmd => cmd == "xdg-open");
            CollectionAssert.AreEqual(new[] { "heroic://launch/Sugar" }, noRunnerSpec.Arguments.ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void GameLauncher_BuildLaunchSpec_Lutris_HandlesNumericId_Flatpak_UbisoftContainerChild_AndSlugFallback()
    {
        var regularLutris = MakeGame("42", GameSource.Lutris, "/games/PBZ", "/games/PBZ/PBZ.exe") with
        {
            LauncherRunner = "phantom-blade-zero"
        };
        var specRegular = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(regularLutris, cmd => cmd == "xdg-open");
        Assert.AreEqual("xdg-open", specRegular.FileName);
        CollectionAssert.AreEqual(new[] { "lutris:rungameid/42" }, specRegular.Arguments.ToArray());

        // Ubisoft Connect container child ID (<lutrisId>_ubisoft_<uplayId>) resolves parent Lutris container ID cleanly
        var ubiContainerChild = MakeGame("flatpak_12_ubisoft_8006", GameSource.Lutris, "/games/ACShadows", "/games/ACShadows/ACShadows.exe") with
        {
            IsFlatpak = true
        };
        var specUbiFlatpak = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(ubiContainerChild, cmd => cmd == "flatpak");
        Assert.AreEqual("flatpak", specUbiFlatpak.FileName);
        CollectionAssert.AreEqual(
            new[] { "run", "net.lutris.Lutris", "lutris:rungameid/12" },
            specUbiFlatpak.Arguments.ToArray());

        // Non-numeric slug fallback when xdg-open is absent
        var slugLutris = MakeGame("cyberpunk-2077", GameSource.Lutris, "/games/CP77", "/games/CP77/Cyberpunk2077.exe");
        var specSlug = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(slugLutris, cmd => cmd == "lutris");
        Assert.AreEqual("lutris", specSlug.FileName);
        CollectionAssert.AreEqual(new[] { "lutris:rungame/cyberpunk-2077" }, specSlug.Arguments.ToArray());
    }

    [TestMethod]
    public void GameLauncher_BuildLaunchSpec_Manual_WiresProtonOrCustomCommand_CacheEnvVars_AndRecorderDllOverride()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-manual-launch-" + Guid.NewGuid().ToString("N"));
        try
        {
            string gameDir = Path.Combine(root, "StandaloneGame");
            string exePath = Path.Combine(gameDir, "StandaloneGame.exe");
            string prefixDir = Path.Combine(root, "prefix", "pfx");
            string protonBin = Path.Combine(root, "Proton", "proton");
            string glCacheLeaf = Path.Combine(prefixDir, "shadercache", "nvidiav1", "GLCache", "deadbeef");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(prefixDir);
            Directory.CreateDirectory(Path.GetDirectoryName(protonBin)!);
            Directory.CreateDirectory(glCacheLeaf);
            File.WriteAllText(exePath, "x");
            File.WriteAllText(protonBin, "#!/usr/bin/env python3\n");
            File.WriteAllBytes(Path.Combine(glCacheLeaf, "shaderless_gmanual_1_part1.toc"), new byte[] { 1, 2, 3, 4 });

            var manualProton = MakeGame("manual_1", GameSource.Manual, gameDir, exePath, prefixDir) with
            {
                RecorderInstalled = true,
                LauncherRunner = protonBin
            };

            var specProton = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(manualProton, cmd => cmd == "python3");
            Assert.AreEqual("python3", specProton.FileName);
            CollectionAssert.AreEqual(new[] { protonBin, "waitforexitandrun", exePath }, specProton.Arguments.ToArray());
            Assert.IsNotNull(specProton.EnvironmentVariables);
            Assert.AreEqual(Path.Combine(root, "prefix"), specProton.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"]);
            Assert.AreEqual(prefixDir, specProton.EnvironmentVariables["WINEPREFIX"]);
            Assert.AreEqual(Path.Combine(prefixDir, "shadercache", "VKD3D_shader_cache"), specProton.EnvironmentVariables["VKD3D_SHADER_CACHE_PATH"]);
            StringAssert.Contains(specProton.EnvironmentVariables["WINEDLLOVERRIDES"], "d3d12=n,b");
            Assert.AreEqual("shaderless_gmanual_1_part1", specProton.EnvironmentVariables["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"]);

            var manualCustom = manualProton with
            {
                RecorderInstalled = false,
                LaunchCommand = "gamemoderun mangohud ./StandaloneGame.exe -dx12"
            };
            var specCustom = Shaderless.Core.Games.GameLauncher.BuildLaunchSpec(manualCustom, _ => true);
            Assert.AreEqual("/bin/sh", specCustom.FileName);
            CollectionAssert.AreEqual(new[] { "-c", "gamemoderun mangohud ./StandaloneGame.exe -dx12" }, specCustom.Arguments.ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void GameLauncher_Launch_BlocksWhenCompiling_OrAlreadyRunning_AndGameExitMonitor_DetectsStartedGames()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-launch-guard-" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(root, "game");
        string procDir = Path.Combine(root, "proc");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(procDir);

        try
        {
            string exePath = Path.Combine(gameDir, "Cronos-Win64-Shipping.exe");
            File.WriteAllText(exePath, "x");
            var game = MakeGame("2101960", GameSource.Steam, gameDir, exePath);

            int startCalls = 0;

            // 1. Blocked while compiling
            var rCompiling = Shaderless.Core.Games.GameLauncher.Launch(
                game,
                isCompiling: true,
                procRoot: procDir,
                commandExists: _ => true,
                processStarter: _ => { startCalls++; return (true, null); });
            Assert.IsFalse(rCompiling.Ok);
            StringAssert.Contains(rCompiling.Message, "compilation is in progress");
            Assert.AreEqual(0, startCalls);

            // 2. Blocked when game process is already running in /proc
            Directory.CreateDirectory(Path.Combine(procDir, "4242"));
            File.WriteAllText(Path.Combine(procDir, "4242", "cmdline"), $"Z:\\games\\Cronos-Win64-Shipping.exe\0-dx12\0");

            var rRunning = Shaderless.Core.Games.GameLauncher.Launch(
                game,
                isCompiling: false,
                procRoot: procDir,
                commandExists: _ => true,
                processStarter: _ => { startCalls++; return (true, null); });
            Assert.IsFalse(rRunning.Ok);
            StringAssert.Contains(rRunning.Message, "already running");
            Assert.AreEqual(0, startCalls);

            // 3. GameExitMonitor.DetectGameTransitions tracks started -> still running -> exited cleanly
            var runningSet = new HashSet<string>(StringComparer.Ordinal);
            var t1 = Shaderless.Core.Games.GameExitMonitor.DetectGameTransitions(
                new[] { game },
                runningSet,
                g => ProtonRunner.IsGameRunning(g, procDir));
            Assert.AreEqual(1, t1.Started.Count);
            Assert.AreEqual("2101960", t1.Started[0].Id);
            Assert.AreEqual(0, t1.Exited.Count);

            var t2 = Shaderless.Core.Games.GameExitMonitor.DetectGameTransitions(
                new[] { game },
                runningSet,
                g => ProtonRunner.IsGameRunning(g, procDir));
            Assert.AreEqual(0, t2.Started.Count);
            Assert.AreEqual(0, t2.Exited.Count);

            // Remove fake /proc entry -> game exited, and Launch now succeeds
            Directory.Delete(Path.Combine(procDir, "4242"), true);
            var t3 = Shaderless.Core.Games.GameExitMonitor.DetectGameTransitions(
                new[] { game },
                runningSet,
                g => ProtonRunner.IsGameRunning(g, procDir));
            Assert.AreEqual(0, t3.Started.Count);
            Assert.AreEqual(1, t3.Exited.Count);

            var rOk = Shaderless.Core.Games.GameLauncher.Launch(
                game,
                isCompiling: false,
                procRoot: procDir,
                commandExists: cmd => cmd == "xdg-open",
                processStarter: _ => { startCalls++; return (true, null); });
            Assert.IsTrue(rOk.Ok);
            Assert.AreEqual(1, startCalls);
            Assert.IsNotNull(rOk.Spec);
            Assert.AreEqual("xdg-open", rOk.Spec.FileName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void RecorderManager_StreamlineFirst_InstallsAndUninstallsCompanionDxgiDll_RefusesForeignDxgi_AndCleansStaleGoverlayVars()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-streamline-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(root, "data");
        string gameDir = Path.Combine(root, "game");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(gameDir);

        string? prevDataOverride = Shaderless.Core.AppPaths.TestDataDirOverride;
        Shaderless.Core.AppPaths.TestDataDirOverride = dataDir;
        try
        {
            string exePath = Path.Combine(gameDir, "OnimushaWotS.exe");
            File.WriteAllText(exePath, "MZ-stub");

            var game = MakeGame("2638890", GameSource.Steam, gameDir, exePath);

            // Without sl.interposer.dll, companion dxgi.dll is not required
            Assert.IsFalse(Shaderless.Core.Games.RecorderManager.NeedsStreamlineCompanion(game));
            Shaderless.Core.Games.RecorderManager.Install(game);
            Assert.IsTrue(Shaderless.Core.Games.RecorderManager.IsInstalled(game));
            Assert.IsTrue(File.Exists(Path.Combine(gameDir, "d3d12.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(gameDir, "dxgi.dll")));
            Shaderless.Core.Games.RecorderManager.Uninstall(game);
            Assert.IsFalse(File.Exists(Path.Combine(gameDir, "d3d12.dll")));

            // Add sl.interposer.dll and a stale goverlay.vars (without OptiScaler.dll)
            File.WriteAllText(Path.Combine(gameDir, "sl.interposer.dll"), "MZ-streamline");
            File.WriteAllText(Path.Combine(gameDir, "goverlay.vars"), "optiscalerproxy=dxgi.dll\n");
            Assert.IsTrue(Shaderless.Core.Games.RecorderManager.NeedsStreamlineCompanion(game));

            // If only d3d12.dll is installed (legacy state), IsInstalled returns false until dxgi.dll is also installed
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Native", "d3d12.dll"), Path.Combine(gameDir, "d3d12.dll"));
            Assert.IsFalse(Shaderless.Core.Games.RecorderManager.IsInstalled(game));

            // Install installs both d3d12.dll and dxgi.dll and removes stale goverlay.vars
            Shaderless.Core.Games.RecorderManager.Install(game);
            Assert.IsTrue(Shaderless.Core.Games.RecorderManager.IsInstalled(game));
            Assert.IsTrue(Shaderless.Engine.App.ShaderEngine.IsOurProxy(Path.Combine(gameDir, "d3d12.dll")));
            Assert.IsTrue(Shaderless.Engine.App.ShaderEngine.IsOurProxy(Path.Combine(gameDir, "dxgi.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(gameDir, "goverlay.vars")));

            // Uninstall removes both d3d12.dll and dxgi.dll cleanly
            Shaderless.Core.Games.RecorderManager.Uninstall(game);
            Assert.IsFalse(Shaderless.Core.Games.RecorderManager.IsInstalled(game));
            Assert.IsFalse(File.Exists(Path.Combine(gameDir, "d3d12.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(gameDir, "dxgi.dll")));

            // Foreign dxgi.dll blocks Install and TryAutoInstall
            File.WriteAllText(Path.Combine(gameDir, "dxgi.dll"), "foreign-dxgi-mod");
            Assert.IsFalse(Shaderless.Core.Games.RecorderManager.TryAutoInstall(game));
            Assert.ThrowsExactly<InvalidOperationException>(() => Shaderless.Core.Games.RecorderManager.Install(game));
            Assert.AreEqual("foreign-dxgi-mod", File.ReadAllText(Path.Combine(gameDir, "dxgi.dll")));
        }
        finally
        {
            Shaderless.Core.AppPaths.TestDataDirOverride = prevDataOverride;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void GameExitMonitor_ShouldAutoCompileAfterExit_ReturnsFalseWhenNeedsRecordingFirstAndZeroRecordedPsos()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-exit-guard-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(root, "data");
        string gameDir = Path.Combine(root, "game");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(gameDir);

        string? prevDataOverride = Shaderless.Core.AppPaths.TestDataDirOverride;
        Shaderless.Core.AppPaths.TestDataDirOverride = dataDir;
        try
        {
            string exePath = Path.Combine(gameDir, "OnimushaWotS.exe");
            File.WriteAllText(exePath, "MZ");

            var recordingFirstGame = MakeGame("2638890", GameSource.Steam, gameDir, exePath) with
            {
                State = CacheState.NeedsCompilation,
                NeedsRecordingFirst = true,
                RecordedPsos = 0
            };

            // 0 recorded PSOs and 0 newly imported -> must NOT trigger doomed auto-compile
            Assert.IsFalse(Shaderless.Core.Games.GameExitMonitor.ShouldAutoCompileAfterExit(recordingFirstGame, newlyImported: 0));

            // Once PSOs are recorded/imported, auto-compile is allowed
            Assert.IsTrue(Shaderless.Core.Games.GameExitMonitor.ShouldAutoCompileAfterExit(recordingFirstGame, newlyImported: 5));
            Assert.IsTrue(Shaderless.Core.Games.GameExitMonitor.ShouldAutoCompileAfterExit(recordingFirstGame with { RecordedPsos = 12 }, newlyImported: 0));
        }
        finally
        {
            Shaderless.Core.AppPaths.TestDataDirOverride = prevDataOverride;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void RecorderManager_StreamlineCompanionProxy_RecordsD3D12PipelinesWhenSystem32D3D12LoadedAndWalkedLikeStreamline()
    {
        if (!Shaderless.Core.Games.GameLauncher.HasCommandInPath("x86_64-w64-mingw32-g++") ||
            !Shaderless.Core.Games.GameLauncher.HasCommandInPath("wine"))
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "shaderless-sl-wine-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(root, "data");
        string gameDir = Path.Combine(root, "game");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(gameDir);

        string? prevDataOverride = Shaderless.Core.AppPaths.TestDataDirOverride;
        Shaderless.Core.AppPaths.TestDataDirOverride = dataDir;
        try
        {
            // Build a small 64-bit PE executable that:
            // 1. Statically imports dxgi.dll (CreateDXGIFactory1) and does NOT statically import d3d12.dll.
            // 2. Loads system32\d3d12.dll via LoadLibraryExW(L"d3d12.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32).
            // 3. Walks the PE Export Address Table (AddressOfFunctions) of system32\d3d12.dll directly in memory
            //    (matching sl.interposer.dll's 0x18002c460 export resolver!) to resolve D3D12CreateDevice and D3D12GetInterface.
            // 4. Invokes the resolved D3D12CreateDevice with a fake/mocked or real call and verifies it routes through
            //    Proxy_D3D12CreateDevice in our <GameDir>\d3d12.dll (which logs "loaded into" in shaderless.log).
            string cppPath = Path.Combine(root, "sl_repro.cpp");
            string exePath = Path.Combine(gameDir, "sl_repro.exe");
            File.WriteAllText(cppPath, @"
#define NOMINMAX
#include <windows.h>
#include <dxgi.h>
#include <d3d12.h>
#include <cstdio>
#include <cstdint>
#include <cstring>

static void* walk_pe_export(HMODULE mod, const char* target_name) {
    if (!mod) return nullptr;
    auto* base = (uint8_t*)mod;
    auto* dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    auto* nt = (IMAGE_NT_HEADERS64*)(base + dos->e_lfanew);
    auto dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (!dir.VirtualAddress) return nullptr;
    auto* exp = (IMAGE_EXPORT_DIRECTORY*)(base + dir.VirtualAddress);
    auto* names = (DWORD*)(base + exp->AddressOfNames);
    auto* ords = (WORD*)(base + exp->AddressOfNameOrdinals);
    auto* funcs = (DWORD*)(base + exp->AddressOfFunctions);
    for (DWORD i = 0; i < exp->NumberOfNames; ++i) {
        if (!strcmp((const char*)(base + names[i]), target_name)) {
            return base + funcs[ords[i]];
        }
    }
    return nullptr;
}

int main() {
    // Touch static import from dxgi.dll so linker keeps dxgi.dll in the PE import table
    IDXGIFactory1* fac = nullptr;
    CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&fac);
    if (fac) fac->Release();

    // Simulate Streamline/NGX loading System32\d3d12.dll with LOAD_LIBRARY_SEARCH_SYSTEM32
    HMODULE sys_d3d12 = LoadLibraryExW(L""d3d12.dll"", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!sys_d3d12) return 2;

    // Simulate sl.interposer.dll walking the PE Export Directory of d3d12.dll in memory
    using PFN_D3D12CreateDevice = HRESULT(WINAPI*)(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);
    auto fn = (PFN_D3D12CreateDevice)walk_pe_export(sys_d3d12, ""D3D12CreateDevice"");
    if (!fn) return 3;

    // Also verify our recorder d3d12.dll was preloaded by dxgi.dll
    HMODULE our_d3d12 = GetModuleHandleW(L""d3d12.dll"");
    if (!our_d3d12 || !GetProcAddress(our_d3d12, ""Shaderless_StartWarm"")) return 4;
    void* proxy_fn = (void*)GetProcAddress(our_d3d12, ""D3D12CreateDevice"");

    // Case A: LoadLibraryExW(L""d3d12.dll"", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32) returns our preloaded d3d12.dll
    if ((void*)fn != proxy_fn) {
        auto* code = (const uint8_t*)fn;
        if (code[0] != 0xff || code[1] != 0x25) return 5;
        uint64_t target = 0;
        memcpy(&target, code + 6, sizeof(target));
        if ((void*)(uintptr_t)target != proxy_fn) return 6;
    }

    // Case B: Full-path LoadLibraryW(C:\windows\system32\d3d12.dll) returns system32\d3d12.dll,
    // whose PE Export Address Table was patched by hook_system_d3d12_exports(g_real) to jump to proxy_fn!
    wchar_t sys_path[MAX_PATH];
    GetSystemDirectoryW(sys_path, MAX_PATH);
    wcscat(sys_path, L""\\d3d12.dll"");
    HMODULE full_sys_d3d12 = LoadLibraryW(sys_path);
    if (!full_sys_d3d12 || full_sys_d3d12 == our_d3d12) return 7;
    auto* sys_fn = (const uint8_t*)walk_pe_export(full_sys_d3d12, ""D3D12CreateDevice"");
    if (!sys_fn) return 8;
    if (sys_fn[0] != 0xff || sys_fn[1] != 0x25) return 9;
    uint64_t sys_target = 0;
    memcpy(&sys_target, sys_fn + 6, sizeof(sys_target));
    if ((void*)(uintptr_t)sys_target != proxy_fn) return 10;

    return 0;
}
");
            var compilePsi = new System.Diagnostics.ProcessStartInfo("x86_64-w64-mingw32-g++")
            {
                UseShellExecute = false,
                RedirectStandardError = true
            };
            compilePsi.ArgumentList.Add("-O2");
            compilePsi.ArgumentList.Add("-std=c++20");
            compilePsi.ArgumentList.Add("-static");
            compilePsi.ArgumentList.Add("-o");
            compilePsi.ArgumentList.Add(exePath);
            compilePsi.ArgumentList.Add(cppPath);
            compilePsi.ArgumentList.Add("-ldxgi");
            compilePsi.ArgumentList.Add("-luuid");
            using (var cp = System.Diagnostics.Process.Start(compilePsi)!)
            {
                string err = cp.StandardError.ReadToEnd();
                cp.WaitForExit();
                Assert.AreEqual(0, cp.ExitCode, $"mingw compile failed: {err}");
            }

            File.WriteAllText(Path.Combine(gameDir, "sl.interposer.dll"), "MZ-streamline");

            // Verify GameFiles.StreamlineFirst detects sl_repro.exe as StreamlineFirst == true on Linux!
            Assert.AreEqual(true, Shaderless.Engine.Games.GameFiles.StreamlineFirst(exePath));

            var game = MakeGame("2638890", GameSource.Steam, gameDir, exePath);
            Assert.IsTrue(Shaderless.Core.Games.RecorderManager.NeedsStreamlineCompanion(game));

            Shaderless.Core.Games.RecorderManager.Install(game);
            Assert.IsTrue(File.Exists(Path.Combine(gameDir, "d3d12.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(gameDir, "dxgi.dll")));

            var winePsi = new System.Diagnostics.ProcessStartInfo("wine")
            {
                WorkingDirectory = gameDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            winePsi.ArgumentList.Add(exePath);
            winePsi.Environment["WINEDLLOVERRIDES"] = "dxgi=n,b;d3d12=n,b;mscoree=;mshtml=";
            using (var wp = System.Diagnostics.Process.Start(winePsi)!)
            {
                bool exited = wp.WaitForExit(30000);
                if (!exited)
                {
                    try { wp.Kill(true); } catch { }
                    Assert.Fail("wine sl_repro.exe timed out");
                }
                Assert.AreEqual(0, wp.ExitCode, $"sl_repro.exe exited with code {wp.ExitCode}");
            }
        }
        finally
        {
            Shaderless.Core.AppPaths.TestDataDirOverride = prevDataOverride;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

file static class ByteArrayTestExtensions
{
    public static byte[] PrependRange(this byte[] tail, byte[] head)
    {
        byte[] combined = new byte[head.Length + tail.Length];
        Buffer.BlockCopy(head, 0, combined, 0, head.Length);
        Buffer.BlockCopy(tail, 0, combined, head.Length, tail.Length);
        return combined;
    }
}
