using Microsoft.VisualStudio.TestTools.UnitTesting;
using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Models;
using SCSKiller.Linux.Core.Pipelines;

namespace SCSKiller.Linux.Tests;

[TestClass]
public class Part1SafetyTests
{
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

            var source = new SCSKiller.Linux.Core.Games.SteamLinuxSource(home, xdgData);
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

            var source = new SCSKiller.Linux.Core.Games.HeroicLinuxSource(home, xdgConfig);
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

    private sealed class ThrowingGameSource : SCSKiller.Linux.Core.Games.IGameSource
    {
        public GameSource SourceType => GameSource.Lutris;
        public Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated corrupted SQLite database");
    }

    private sealed class StaticGameSource : SCSKiller.Linux.Core.Games.IGameSource
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
        var manager = new SCSKiller.Linux.Core.Games.GameManager(new SCSKiller.Linux.Core.Games.IGameSource[]
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
            var initial = SCSKiller.Linux.Core.SettingsManager.Load(settingsPath);
            Assert.IsFalse(initial.AutoRecordAllGames);
            Assert.IsFalse(initial.AutoCompileOnExit);

            SCSKiller.Linux.Core.SettingsManager.Save(new AppSettings(AutoRecordAllGames: true, AutoCompileOnExit: false), settingsPath);
            var reloaded = SCSKiller.Linux.Core.SettingsManager.Load(settingsPath);
            Assert.IsTrue(reloaded.AutoRecordAllGames);
            Assert.IsFalse(reloaded.AutoCompileOnExit);

            var withOptOut = SCSKiller.Linux.Core.SettingsManager.SetGameAutoRecordOptOut("2828710", optOut: true, settingsPath);
            Assert.IsTrue(withOptOut.IsAutoRecordDisabledForGame("2828710"));

            var clearedOptOut = SCSKiller.Linux.Core.SettingsManager.SetGameAutoRecordOptOut("2828710", optOut: false, settingsPath);
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
            Assert.IsFalse(SCSKiller.Linux.Core.Games.RecorderManager.TryAutoInstall(acGame));
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
            var pso1 = new SCSKiller.Core.Planning.PsoDb.Rec('G', new byte[] { 1, 2, 3, 4 });
            var pso2 = new SCSKiller.Core.Planning.PsoDb.Rec('C', new byte[] { 5, 6, 7, 8 });
            var d11Item = new SCSKiller.Core.Planning.PsoDb.Rec('1', new byte[24]);

            using (var fs = File.Create(recDb))
            {
                SCSKiller.Core.Planning.PsoDb.Write(fs, 'B', blobPayload);
                SCSKiller.Core.Planning.PsoDb.Write(fs, pso1.Tag, pso1.Payload);
                SCSKiller.Core.Planning.PsoDb.Write(fs, pso2.Tag, pso2.Payload);
            }
            using (var fs = File.Create(genDb))
            {
                SCSKiller.Core.Planning.PsoDb.Write(fs, d11Item.Tag, d11Item.Payload);
            }

            // Mark pso1 and d11Item as already warmed; only pso2 is new
            var warmed = new HashSet<string>(StringComparer.Ordinal) { pso1.Key, d11Item.Key };
            var (allKeys, unwarmedCount, alreadyWarmedCount) =
                SCSKiller.Linux.Core.Games.RecorderManager.FilterDatabasesToUnwarmed(recDb, genDb, warmed);

            Assert.AreEqual(3, allKeys.Count);
            Assert.AreEqual(1, unwarmedCount);
            Assert.AreEqual(2, alreadyWarmedCount);

            var remainingRecs = SCSKiller.Core.Planning.PsoDb.Read(recDb).ToList();
            Assert.IsTrue(remainingRecs.Any(r => r.Tag == 'B'));
            Assert.IsFalse(remainingRecs.Any(r => r.Key == pso1.Key));
            Assert.IsTrue(remainingRecs.Any(r => r.Key == pso2.Key));

            var remainingGen = SCSKiller.Core.Planning.PsoDb.Read(genDb).ToList();
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
        var exited1 = SCSKiller.Linux.Core.Games.GameExitMonitor.DetectExitedGames(
            games, runningSet, g => g.Id == "1");
        Assert.AreEqual(0, exited1.Count);
        Assert.IsTrue(runningSet.Contains("1"));

        // Step 2: g1 exits, g2 is also running -> should suppress auto-compile while g2 is open
        var exited2 = SCSKiller.Linux.Core.Games.GameExitMonitor.DetectExitedGames(
            games, runningSet, g => g.Id == "2");
        Assert.AreEqual(0, exited2.Count);
        Assert.IsFalse(runningSet.Contains("1"));
        Assert.IsTrue(runningSet.Contains("2"));

        // Step 3: g2 exits and no game is running -> returns g2
        var exited3 = SCSKiller.Linux.Core.Games.GameExitMonitor.DetectExitedGames(
            games, runningSet, _ => false);
        Assert.AreEqual(1, exited3.Count);
        Assert.AreEqual("2", exited3[0].Id);
    }

    [TestMethod]
    public void GameManager_EvaluateStaleReason_Detects_Driver_Proton_And_Game_Updates()
    {
        // Matching stamps -> not stale
        Assert.IsNull(SCSKiller.Linux.Core.Games.GameManager.EvaluateStaleReason(
            "595.58.03", "/proton|100|200", "1000:500|2:5000:900",
            "595.58.03", "/proton|100|200", "1000:500|2:5000:900"));

        // Null saved stamps (adopting legacy cache) -> not stale
        Assert.IsNull(SCSKiller.Linux.Core.Games.GameManager.EvaluateStaleReason(
            null, null, null,
            "595.58.03", "/proton|100|200", "1000:500|2:5000:900"));

        // Driver updated
        Assert.AreEqual("driver updated", SCSKiller.Linux.Core.Games.GameManager.EvaluateStaleReason(
            "590.00.00", "/proton|100|200", "1000:500|2:5000:900",
            "595.58.03", "/proton|100|200", "1000:500|2:5000:900"));

        // Proton updated
        Assert.AreEqual("Proton updated", SCSKiller.Linux.Core.Games.GameManager.EvaluateStaleReason(
            "595.58.03", "/proton-9|100|200", "1000:500|2:5000:900",
            "595.58.03", "/proton-10|110|210", "1000:500|2:5000:900"));

        // Game updated (patch changed exe or archive)
        Assert.AreEqual("game updated", SCSKiller.Linux.Core.Games.GameManager.EvaluateStaleReason(
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

            // Populate compiled caches after first full compile
            File.WriteAllBytes(mergedBin, new byte[2048]);
            File.WriteAllBytes(mergedToc, new byte[128]);
            File.WriteAllBytes(steamRoBin, new byte[512]);
            File.WriteAllBytes(steamRoToc, new byte[64]);
            File.WriteAllBytes(vkd3dFile, new byte[256]);
            File.WriteAllBytes(exeVkd3d, new byte[256]);

            // Case 1: Incremental run with UNCHANGED Proton/driver stamp -> preserves mergedBin, steamRoBin, vkd3d, and pre-seeds bucket0/bucket1
            ProtonRunner.PrepareDriverCachesForWarmer(
                targetCache, vkd3dCache, glCache, "/proton-10.0/proton", workDir, isIncrementalRun: true, gameExeDir);

            Assert.IsTrue(File.Exists(mergedBin));
            Assert.IsTrue(File.Exists(steamRoBin));
            Assert.IsTrue(File.Exists(vkd3dFile));
            Assert.IsTrue(File.Exists(exeVkd3d));
            Assert.IsTrue(File.Exists(Path.Combine(glLeaf, "steamapp_shader_cache0.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(glLeaf, "steamapp_shader_cache1.bin")));

            // Case 2: Proton updated (/proton-11.0/proton) even with isIncrementalRun: true -> resets stale caches
            ProtonRunner.PrepareDriverCachesForWarmer(
                targetCache, vkd3dCache, glCache, "/proton-11.0/proton", workDir, isIncrementalRun: true, gameExeDir);

            Assert.IsFalse(File.Exists(mergedBin));
            Assert.IsFalse(File.Exists(steamRoBin));
            Assert.IsFalse(File.Exists(vkd3dFile));
            Assert.IsFalse(File.Exists(exeVkd3d));

            // Re-populate caches under /proton-11.0/proton
            File.WriteAllBytes(mergedBin, new byte[2048]);
            File.WriteAllBytes(mergedToc, new byte[128]);
            File.WriteAllBytes(vkd3dFile, new byte[256]);
            File.WriteAllBytes(exeVkd3d, new byte[256]);

            // Case 3: Full re-compile (isIncrementalRun: false) with same Proton -> resets caches so full compiles never stack
            ProtonRunner.PrepareDriverCachesForWarmer(
                targetCache, vkd3dCache, glCache, "/proton-11.0/proton", workDir, isIncrementalRun: false, gameExeDir);

            Assert.IsFalse(File.Exists(mergedBin));
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
        string workDir = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "work", testGameId);
        Directory.CreateDirectory(workDir);

        try
        {
            string recDb = Path.Combine(workDir, "shaderless.db");
            string genDb = Path.Combine(workDir, "shaderless_gen.db");

            var pso1 = new SCSKiller.Core.Planning.PsoDb.Rec('G', new byte[] { 1, 2, 3, 4 });
            var pso2 = new SCSKiller.Core.Planning.PsoDb.Rec('C', new byte[] { 5, 6, 7, 8 });
            var pso3 = new SCSKiller.Core.Planning.PsoDb.Rec('S', new byte[] { 9, 10, 11, 12 });

            using (var fs = File.Create(recDb))
            {
                SCSKiller.Core.Planning.PsoDb.Write(fs, pso1.Tag, pso1.Payload);
                SCSKiller.Core.Planning.PsoDb.Write(fs, pso2.Tag, pso2.Payload);
            }
            using (var fs = File.Create(genDb))
            {
                SCSKiller.Core.Planning.PsoDb.Write(fs, pso3.Tag, pso3.Payload);
            }

            // Build snapshot BEFORE FilterDatabasesToUnwarmed strips pso1 & pso2 out of recDb
            byte[]? fullSnapshot = SCSKiller.Linux.Core.Games.RecorderManager.BuildWarmedSnapshot(new[] { recDb, genDb });
            Assert.IsNotNull(fullSnapshot);
            // 8-byte "SCSKWARM" + 4-byte count (3) + 3 * 20-byte hashes = 72 bytes
            Assert.AreEqual(8 + 4 + 3 * 20, fullSnapshot.Length);

            var alreadyWarmed = new HashSet<string>(StringComparer.Ordinal) { pso1.Key, pso2.Key };
            var (allKeys, unwarmedCount, alreadyWarmedCount) =
                SCSKiller.Linux.Core.Games.RecorderManager.FilterDatabasesToUnwarmed(recDb, genDb, alreadyWarmed);

            Assert.AreEqual(3, allKeys.Count);
            Assert.AreEqual(1, unwarmedCount);
            Assert.AreEqual(2, alreadyWarmedCount);

            SCSKiller.Linux.Core.Games.RecorderManager.SaveWarmedKeys(
                game, allKeys, "stamp1", new[] { recDb, genDb }, fullSnapshot);

            string savedWarmed = Path.Combine(SCSKiller.Linux.Core.Games.RecorderManager.GameDir(testGameId), "shaderless.warmed");
            Assert.IsTrue(File.Exists(savedWarmed));
            Assert.AreEqual(8 + 4 + 3 * 20, new FileInfo(savedWarmed).Length);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
            string recGameDir = SCSKiller.Linux.Core.Games.RecorderManager.GameDir(testGameId);
            try { if (Directory.Exists(recGameDir)) Directory.Delete(recGameDir, true); } catch { }
        }
    }
}
