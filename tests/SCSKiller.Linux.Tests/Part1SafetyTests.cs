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

    [TestMethod]
    public void LinuxPlatformGuards_KeyFiles_Middleware_GameFiles_And_ProcessMemory_DoNotRequireKernel32()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-mw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string dllPath = Path.Combine(root, "amd_fidelityfx_dx12.dll");
            File.WriteAllBytes(dllPath, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04 });

            string id = SCSKiller.Core.App.KeyFiles.Identity(dllPath);
            Assert.IsFalse(string.IsNullOrEmpty(id));
            Assert.AreEqual("", SCSKiller.Core.App.KeyFiles.Identity(Path.Combine(root, "missing.dll")));

            var detected = SCSKiller.Core.Planning.Middleware.Detect(root);
            Assert.HasCount(1, detected);
            Assert.AreEqual("amd", detected[0].Vendor);

            // Middleware.Scan calls internal KeyFiles.Stamp -> KeyFiles.Identity under the hood
            var scanned = SCSKiller.Core.Planning.Middleware.Scan(dllPath);
            Assert.AreEqual(8, scanned.Size);
            Assert.IsEmpty(scanned.Containers);

            string? finalDir = SCSKiller.Core.Games.GameFiles.FinalPath(root);
            Assert.IsNotNull(finalDir);
            Assert.IsNull(SCSKiller.Core.Games.GameFiles.FinalPath(Path.Combine(root, "does-not-exist")));

            var gfId = SCSKiller.Core.Games.GameFiles.Identity(dllPath);
            Assert.IsNotNull(gfId);
            Assert.IsNotNull(gfId.Value.Final);

            // Must be a clean no-op on Linux without P/Invoking kernel32.dll
            SCSKiller.Core.App.ProcessMemory.Trim();
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

            SCSKiller.Linux.Core.Games.RecorderManager.MigrateLegacyFiles(root);

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
        var games = new List<SCSKiller.Linux.Core.Models.GameInfo>
        {
            new(
                Id: "1285190",
                Name: "Borderlands 4",
                Source: SCSKiller.Linux.Core.Models.GameSource.Steam,
                InstallPath: "/games/bl4",
                ExecutablePath: "/games/bl4/bl4.exe",
                PrefixPath: null,
                BannerPath: null,
                Engine: SCSKiller.Linux.Core.Models.GameEngine.UnrealEngine5,
                State: SCSKiller.Linux.Core.Models.CacheState.NeedsCompilation,
                TotalShadersEstimated: 20000,
                CompiledShadersCount: 0,
                LastCompiledAt: null),
            new(
                Id: "flatpak_kcd2",
                Name: "Kingdom Come Deliverance 2",
                Source: SCSKiller.Linux.Core.Models.GameSource.Heroic,
                InstallPath: "/games/kcd2",
                ExecutablePath: "/games/kcd2/kcd2.exe",
                PrefixPath: null,
                BannerPath: null,
                Engine: SCSKiller.Linux.Core.Models.GameEngine.DirectX12,
                State: SCSKiller.Linux.Core.Models.CacheState.Cached,
                TotalShadersEstimated: 6000,
                CompiledShadersCount: 6000,
                LastCompiledAt: DateTime.UtcNow,
                IsFlatpak: true,
                NewRecordedPsos: 0),
            new(
                Id: "lutris_eoa",
                Name: "End of Abyss",
                Source: SCSKiller.Linux.Core.Models.GameSource.Lutris,
                InstallPath: "/games/eoa",
                ExecutablePath: "/games/eoa/eoa.exe",
                PrefixPath: null,
                BannerPath: null,
                Engine: SCSKiller.Linux.Core.Models.GameEngine.UnrealEngine5,
                State: SCSKiller.Linux.Core.Models.CacheState.Cached,
                TotalShadersEstimated: 8000,
                CompiledShadersCount: 8000,
                LastCompiledAt: DateTime.UtcNow,
                NewRecordedPsos: 42)
        };

        // 1. Launcher filter
        var steamOnly = SCSKiller.Linux.Core.Games.GameManager.FilterGames(games, SCSKiller.Linux.Core.Models.GameSource.Steam);
        Assert.HasCount(1, steamOnly);
        Assert.AreEqual("1285190", steamOnly[0].Id);

        var heroicOnly = SCSKiller.Linux.Core.Games.GameManager.FilterGames(games, SCSKiller.Linux.Core.Models.GameSource.Heroic);
        Assert.HasCount(1, heroicOnly);
        Assert.AreEqual("flatpak_kcd2", heroicOnly[0].Id);

        // 2. Needs Compile filter (includes NeedsCompilation, Outdated, AND Cached games with NewRecordedPsos > 0)
        var needsCompile = SCSKiller.Linux.Core.Games.GameManager.FilterGames(games, onlyNeedsCompile: true);
        Assert.HasCount(2, needsCompile);
        Assert.IsTrue(needsCompile.Any(g => g.Id == "1285190"));
        Assert.IsTrue(needsCompile.Any(g => g.Id == "lutris_eoa"));

        // 3. Search by name, engine, ID, or Flatpak tag
        Assert.HasCount(2, SCSKiller.Linux.Core.Games.GameManager.FilterGames(games, searchQuery: "UnrealEngine5"));
        Assert.HasCount(1, SCSKiller.Linux.Core.Games.GameManager.FilterGames(games, searchQuery: "kingdom"));
        Assert.HasCount(1, SCSKiller.Linux.Core.Games.GameManager.FilterGames(games, searchQuery: "flatpak"));
        Assert.IsEmpty(SCSKiller.Linux.Core.Games.GameManager.FilterGames(games, SCSKiller.Linux.Core.Models.GameSource.Steam, searchQuery: "kingdom"));
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

            Assert.IsTrue(SCSKiller.Linux.Core.Drivers.ProtonRunner.DoesPrefixMatchProton(compatDir, oldProtonBin));
            Assert.IsFalse(SCSKiller.Linux.Core.Drivers.ProtonRunner.DoesPrefixMatchProton(compatDir, newExpBin));

            string cfgDir = Path.Combine(root, "config");
            Directory.CreateDirectory(cfgDir);
            string cfgVdf = Path.Combine(cfgDir, "config.vdf");

            // 1. User switches Valor Mortis (2828710) in Steam dropdown to Proton - Experimental ("proton_experimental")
            File.WriteAllText(cfgVdf,
                "\"InstallConfigStore\"\n{\n  \"Software\"\n  {\n    \"Valve\"\n    {\n      \"Steam\"\n      {\n        \"CompatToolMapping\"\n        {\n          \"0\"\n          {\n            \"name\" \"proton_10\"\n            \"config\" \"\"\n            \"priority\" \"75\"\n          }\n          \"2828710\"\n          {\n            \"name\" \"proton_experimental\"\n            \"config\" \"\"\n            \"priority\" \"250\"\n          }\n        }\n      }\n    }\n  }\n}\n");

            string? resolved = SCSKiller.Linux.Core.Drivers.ProtonRunner.ResolveSteamProtonForApp(
                root, "2828710", Path.Combine(compatDir, "pfx"), allowSystemCompatTools: false);
            Assert.AreEqual(newExpBin, resolved);

            // 2. User switches dropdown to a custom compatibilitytool.vdf alias ("my-custom-cachyos-id")
            File.WriteAllText(cfgVdf,
                "\"InstallConfigStore\"\n{\n  \"CompatToolMapping\"\n  {\n    \"2828710\"\n    {\n      \"name\" \"my-custom-cachyos-id\"\n    }\n  }\n}\n");

            string? resolvedAlias = SCSKiller.Linux.Core.Drivers.ProtonRunner.ResolveSteamProtonForApp(
                root, "2828710", Path.Combine(compatDir, "pfx"), allowSystemCompatTools: false);
            Assert.AreEqual(customAliasBin, resolvedAlias);

            // 3. User unchecks "Force specific Steam Play compatibility tool" ("name" "") -> immediately falls back to "0" ("proton_10")
            File.WriteAllText(cfgVdf,
                "\"InstallConfigStore\"\n{\n  \"CompatToolMapping\"\n  {\n    \"0\"\n    {\n      \"name\" \"proton_10\"\n    }\n    \"2828710\"\n    {\n      \"name\" \"\"\n    }\n  }\n}\n");

            string? resolvedUnchecked = SCSKiller.Linux.Core.Drivers.ProtonRunner.ResolveSteamProtonForApp(
                root, "2828710", Path.Combine(compatDir, "pfx"), allowSystemCompatTools: false);
            Assert.AreEqual(proton10Bin, resolvedUnchecked);

            // 4. Game with no per-game entry in CompatToolMapping uses its prefix config_info (Proton-Old)
            File.WriteAllText(cfgVdf,
                "\"InstallConfigStore\"\n{\n  \"CompatToolMapping\"\n  {\n    \"0\"\n    {\n      \"name\" \"proton_10\"\n    }\n  }\n}\n");

            string? resolvedNoOverride = SCSKiller.Linux.Core.Drivers.ProtonRunner.ResolveSteamProtonForApp(
                root, "2828710", Path.Combine(compatDir, "pfx"), allowSystemCompatTools: false);
            Assert.AreEqual(oldProtonBin, resolvedNoOverride);

            // 5. Verify GetWarmedStampMismatchReason distinguishes Proton vs Driver updates
            string warmedPath = SCSKiller.Linux.Core.Games.RecorderManager.WarmedKeysPath(testGameId);
            Directory.CreateDirectory(Path.GetDirectoryName(warmedPath)!);
            File.WriteAllText(warmedPath, "#STAMP:615.78.08|/proton/a|6316032|7131136\n");

            Assert.IsNull(SCSKiller.Linux.Core.Games.RecorderManager.GetWarmedStampMismatchReason(
                testGameId, "615.78.08", "/proton/a|6316032|7131136"));
            Assert.AreEqual("Proton updated", SCSKiller.Linux.Core.Games.RecorderManager.GetWarmedStampMismatchReason(
                testGameId, "615.78.08", "/proton/b|6369280|7131136"));
            Assert.AreEqual("driver updated", SCSKiller.Linux.Core.Games.RecorderManager.GetWarmedStampMismatchReason(
                testGameId, "620.01.00", "/proton/a|6316032|7131136"));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            string recGameDir = SCSKiller.Linux.Core.Games.RecorderManager.GameDir(testGameId);
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

            var (wotEngine, _) = SCSKiller.Linux.Core.Games.GameEngineDetector.Detect(wotDir);
            Assert.AreNotEqual(GameEngine.UnrealEngine4, wotEngine);
            Assert.AreEqual(GameEngine.DirectX11, wotEngine);

            string? foundExe = SCSKiller.Linux.Core.Games.GameEngineDetector.FindExecutable(wotDir);
            Assert.AreEqual(realWotExe, foundExe);

            // 2. Simulated Gaijin Dagor Engine install (War Thunder / Enlisted)
            string dagorDir = Path.Combine(root, "War Thunder");
            Directory.CreateDirectory(Path.Combine(dagorDir, "compiledShaders"));
            Directory.CreateDirectory(Path.Combine(dagorDir, "win64"));
            string acesExe = Path.Combine(dagorDir, "win64", "aces.exe");
            File.WriteAllBytes(acesExe, new byte[8192]);

            Assert.AreEqual(dagorDir, SCSKiller.Linux.Core.Games.GameEngineDetector.InferInstallRoot(acesExe));
            var (dagorEngine, dagorEst) = SCSKiller.Linux.Core.Games.GameEngineDetector.Detect(dagorDir);
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

            var source = new SCSKiller.Linux.Core.Games.HeroicLinuxSource(home, xdgConfig);
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
                SCSKiller.Core.Planning.PsoDb.WriteBlob(fs, sha1Hex, fakeShaderBytes);
                SCSKiller.Core.Planning.PsoDb.Write(fs, '1', d11Payload);
            }

            string storeDb = Path.Combine(root, "recording.db");
            SCSKiller.Core.App.Recordings.Merge(storeDb, inboxDb, null);

            string materializedDb = Path.Combine(root, "work", "shaderless.db");
            var scsGame = new SCSKiller.Core.Game("kcd2_sideload", kcd2.Name, SCSKiller.Core.Store.Epic, kcd2.InstallPath, kcd2.ExecutablePath);
            int count = SCSKiller.Linux.Core.Games.RecorderManager.MaterializeStandaloneRecording(storeDb, scsGame, materializedDb);

            Assert.AreEqual(1, count);
            Assert.IsTrue(File.Exists(materializedDb));
            var outRecs = SCSKiller.Core.Planning.PsoDb.Read(materializedDb).ToList();
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
            string warmedPath = SCSKiller.Linux.Core.Games.RecorderManager.WarmedKeysPath(compiledId);
            Directory.CreateDirectory(Path.GetDirectoryName(warmedPath)!);
            string protonStamp = ProtonRunner.GetProtonStamp(compiledGame);
            File.WriteAllText(warmedPath, $"#STAMP:615.00|{protonStamp}\n");

            var manager = new SCSKiller.Linux.Core.Games.GameManager(new SCSKiller.Linux.Core.Games.IGameSource[]
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
            string dir1 = SCSKiller.Linux.Core.Games.RecorderManager.GameDir(uncompiledId);
            string dir2 = SCSKiller.Linux.Core.Games.RecorderManager.GameDir(compiledId);
            try { if (Directory.Exists(dir1)) Directory.Delete(dir1, true); } catch { }
            try { if (Directory.Exists(dir2)) Directory.Delete(dir2, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Issue2_RealProtonWarmer_UnsupportedArchiveGame_CompilesRecordedD3D12PipelinesEndToEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-kcd2-e2e-" + Guid.NewGuid().ToString("N"));
        string gameId = "flatpak_kcd2_e2e_" + Guid.NewGuid().ToString("N");
        string workDir = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "work", gameId);
        string recGameDir = SCSKiller.Linux.Core.Games.RecorderManager.GameDir(gameId);

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
            var gm = new SCSKiller.Linux.Core.Games.GameManager(new SCSKiller.Linux.Core.Games.IGameSource[] { new StaticGameSource(kcd2Game) });
            var coordinator = new SCSKiller.Linux.Core.Pipelines.ShaderCompilerCoordinator(gm, backend);

            // 1. Unrecorded unsupported-archive game must fail cleanly with actionable Record guidance (no crash, no fake Cached)
            var unrecordedSteps = new List<CompilationProgress>();
            await foreach (var step in coordinator.CompileGameShadersAsync(kcd2Game))
                unrecordedSteps.Add(step);

            var unrecordedFinal = unrecordedSteps.Last();
            Assert.IsTrue(unrecordedFinal.Failed);
            StringAssert.Contains(unrecordedFinal.ErrorMessage, "Record");
            Assert.IsFalse(ProtonRunner.HasShaderlessCompiledArtifacts(kcd2Game));

            // 2. If real recorded D3D12 PSOs and Proton + shaderless_warm.exe exist on this machine, run full GPU compilation end-to-end!
            string realSampleRecording = SCSKiller.Linux.Core.Games.RecorderManager.RecordingPath("2828710");
            if (!File.Exists(realSampleRecording) || ProtonRunner.FindWarmerBinary() == null || ProtonRunner.FindProtonBinaryForGame(kcd2Game) == null)
            {
                Console.WriteLine("[E2E] Skipping live Proton warmer run (no sample recording or Proton runtime present).");
                return;
            }

            var blobsByHash = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var chosenPsos = new List<SCSKiller.Core.Planning.PsoDb.Rec>();

            foreach (var r in SCSKiller.Core.Planning.PsoDb.Read(realSampleRecording))
            {
                if (r.Tag == 'B' && r.Payload.Length >= 20)
                {
                    blobsByHash[SCSKiller.Core.Planning.PsoDb.Hex(r.Payload.AsSpan(0, 20))] = r.Payload;
                }
            }

            foreach (var r in SCSKiller.Core.Planning.PsoDb.Read(realSampleRecording))
            {
                if (r.Tag is 'G' or 'C' or 'S')
                {
                    var refs = SCSKiller.Core.Planning.Rehydrate.References([r]);
                    if (refs.Count > 0 && refs.All(blobsByHash.ContainsKey))
                    {
                        chosenPsos.Add(r);
                        if (chosenPsos.Count >= 20)
                            break;
                    }
                }
            }

            Assert.IsTrue(chosenPsos.Count > 0, "Expected self-contained D3D12 PSOs in real recording.db");

            // Drop inbox shaderless.db right next to KingdomCome.exe (exact output of d3d12.dll recorder after gameplay)
            string inboxDb = Path.Combine(kcd2BinDir, "shaderless.db");
            var neededHashes = chosenPsos.SelectMany(p => SCSKiller.Core.Planning.Rehydrate.References([p])).Distinct().ToList();
            using (var fs = File.Create(inboxDb))
            {
                foreach (var h in neededHashes)
                    SCSKiller.Core.Planning.PsoDb.Write(fs, 'B', blobsByHash[h]);
                foreach (var pso in chosenPsos)
                    SCSKiller.Core.Planning.PsoDb.Write(fs, pso.Tag, pso.Payload);
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
            Assert.IsTrue(File.Exists(SCSKiller.Linux.Core.Games.RecorderManager.RecordingPath(gameId)));

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
        string? prevOverride = SCSKiller.Linux.Core.SettingsManager.TestSettingsPathOverride;

        try
        {
            SCSKiller.Linux.Core.SettingsManager.TestSettingsPathOverride = settingsPath;

            // 1. Default when missing is 10 GB
            var def = SCSKiller.Linux.Core.SettingsManager.Load();
            Assert.AreEqual(10, def.EffectiveMaxCacheSizeGb);
            Assert.AreEqual(10L * 1024 * 1024 * 1024, def.MaxCacheSizeBytes);
            Assert.AreEqual("10G", def.MesaMaxCacheSize);

            // 2. Clamping bounds (min 5 GB, max 500 GB)
            SCSKiller.Linux.Core.SettingsManager.Save(def with { MaxCacheSizeGb = 1 });
            Assert.AreEqual(5, SCSKiller.Linux.Core.SettingsManager.Load().EffectiveMaxCacheSizeGb);

            SCSKiller.Linux.Core.SettingsManager.Save(def with { MaxCacheSizeGb = 9999 });
            Assert.AreEqual(500, SCSKiller.Linux.Core.SettingsManager.Load().EffectiveMaxCacheSizeGb);

            // 3. Save 25 GB and verify NvidiaBackend + MesaBackend environment recommendations
            SCSKiller.Linux.Core.SettingsManager.Save(def with { MaxCacheSizeGb = 25 });
            var loaded = SCSKiller.Linux.Core.SettingsManager.Load();
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
                "fossilize;steam_shader_cache;steamapp_merged_shader_cache;shaderless_part1;shaderless_part2",
                doc.RootElement.GetProperty("ro_names").GetString());
        }
        finally
        {
            SCSKiller.Linux.Core.SettingsManager.TestSettingsPathOverride = prevOverride;
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
            string realSampleRecording = SCSKiller.Linux.Core.Games.RecorderManager.RecordingPath("2828710");
            Assert.IsTrue(File.Exists(realSampleRecording), "Expected sample recording.db for real blob reference validation");

            var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var psos = new List<SCSKiller.Core.Planning.PsoDb.Rec>();

            foreach (var r in SCSKiller.Core.Planning.PsoDb.Read(realSampleRecording))
            {
                if (r.Tag == 'B' && r.Payload.Length >= 20)
                    blobs[SCSKiller.Core.Planning.PsoDb.Hex(r.Payload.AsSpan(0, 20))] = r.Payload;
            }
            foreach (var r in SCSKiller.Core.Planning.PsoDb.Read(realSampleRecording))
            {
                if (r.Tag is 'G' or 'C' or 'S')
                {
                    var refs = SCSKiller.Core.Planning.Rehydrate.References([r]);
                    if (refs.Count > 0 && refs.All(blobs.ContainsKey))
                    {
                        psos.Add(r);
                        if (psos.Count >= 11) break;
                    }
                }
            }
            Assert.AreEqual(11, psos.Count);

            string recDb = Path.Combine(root, "shaderless.db");
            string genDb = Path.Combine(root, "shaderless_gen.db");

            // Put first 5 PSOs in shaderless.db and next 6 PSOs in shaderless_gen.db (plus a synthetic 'P' variant referencing recIdx 0)
            using (var fs = File.Create(recDb))
            {
                foreach (var h in psos.Take(5).SelectMany(p => SCSKiller.Core.Planning.Rehydrate.References([p])).Distinct())
                    SCSKiller.Core.Planning.PsoDb.Write(fs, 'B', blobs[h]);
                foreach (var p in psos.Take(5))
                    SCSKiller.Core.Planning.PsoDb.Write(fs, p.Tag, p.Payload);
            }

            // Build a real 'P' plan variant record whose 20-byte template key points to psos[0] (which lives in Batch 0!):
            // Layout: templateKey[20] + rootSig[20] + count(u32=1) + stage(u32=1) + shaderHash[20] + keepLayout(u32=0xFFFFFFFF)
            var parsedPso0 = SCSKiller.Core.Planning.PsoDb.Parse(psos[0]);
            var (firstStage, firstShaderHash) = parsedPso0.Stages.First();
            byte[] pPayload = new byte[20 + 20 + 4 + 4 + 20 + 4];
            Convert.FromHexString(psos[0].Key).CopyTo(pPayload.AsSpan(0, 20));
            Convert.FromHexString(parsedPso0.Rs).CopyTo(pPayload.AsSpan(20, 20));
            BitConverter.TryWriteBytes(pPayload.AsSpan(40, 4), 1u);
            BitConverter.TryWriteBytes(pPayload.AsSpan(44, 4), (uint)firstStage);
            Convert.FromHexString(firstShaderHash).CopyTo(pPayload.AsSpan(48, 20));
            BitConverter.TryWriteBytes(pPayload.AsSpan(68, 4), 0xFFFFFFFFu);

            var pRefs = SCSKiller.Core.Planning.Rehydrate.References([new SCSKiller.Core.Planning.PsoDb.Rec('P', pPayload)]);

            using (var fs = File.Create(genDb))
            {
                foreach (var h in psos.Skip(5).SelectMany(p => SCSKiller.Core.Planning.Rehydrate.References([p])).Concat(pRefs).Distinct())
                    if (blobs.TryGetValue(h, out var b))
                        SCSKiller.Core.Planning.PsoDb.Write(fs, 'B', b);
                foreach (var p in psos.Skip(5))
                    SCSKiller.Core.Planning.PsoDb.Write(fs, p.Tag, p.Payload);
                SCSKiller.Core.Planning.PsoDb.Write(fs, 'P', pPayload);
            }

            Assert.AreEqual(12, SCSKiller.Linux.Core.Games.RecorderManager.CountExecutableItems(recDb, genDb));

            var slices = SCSKiller.Linux.Core.Games.RecorderManager.SplitDatabasesIntoBatches(
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
                var recs = SCSKiller.Core.Planning.PsoDb.Read(slice.BatchDbPath).ToList();
                var sliceBlobs = recs
                    .Where(r => r.Tag == 'B' && r.Payload.Length >= 20)
                    .Select(r => SCSKiller.Core.Planning.PsoDb.Hex(r.Payload.AsSpan(0, 20)))
                    .ToHashSet(StringComparer.Ordinal);
                var nonBlobs = recs.Where(r => r.Tag != 'B').ToList();
                Assert.AreEqual(slice.SkipPrefixCount + slice.ExecutableItemCount, nonBlobs.Count);

                var activeItems = nonBlobs.Skip(slice.SkipPrefixCount).ToList();
                var neededRefs = SCSKiller.Core.Planning.Rehydrate.References(activeItems);
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
            Console.WriteLine("[Issue1-E2E] Skipping NVIDIA live driver multi-part test on non-NVIDIA hardware.");
            return;
        }

        string realSampleRecording = SCSKiller.Linux.Core.Games.RecorderManager.RecordingPath("2828710");
        if (!File.Exists(realSampleRecording) || ProtonRunner.FindWarmerBinary() == null)
        {
            Console.WriteLine("[Issue1-E2E] Skipping live Proton multi-part test (missing sample recording or warmer binary).");
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
            return;
        }

        try
        {
            // Select 16 PSOs with distinct shader hashes so NVIDIA compiles multiple distinct shader records
            var blobsByHash = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var chosenPsos = new List<SCSKiller.Core.Planning.PsoDb.Rec>();
            var seenShaderCombos = new HashSet<string>(StringComparer.Ordinal);

            foreach (var r in SCSKiller.Core.Planning.PsoDb.Read(realSampleRecording))
            {
                if (r.Tag == 'B' && r.Payload.Length >= 20)
                    blobsByHash[SCSKiller.Core.Planning.PsoDb.Hex(r.Payload.AsSpan(0, 20))] = r.Payload;
            }

            foreach (var r in SCSKiller.Core.Planning.PsoDb.Read(realSampleRecording))
            {
                if (r.Tag is 'G' or 'C' or 'S')
                {
                    var refs = SCSKiller.Core.Planning.Rehydrate.References([r]);
                    if (refs.Count > 0 && refs.All(blobsByHash.ContainsKey))
                    {
                        string combo = string.Join('+', refs.OrderBy(x => x, StringComparer.Ordinal));
                        if (seenShaderCombos.Add(combo))
                        {
                            chosenPsos.Add(r);
                            if (chosenPsos.Count >= 16) break;
                        }
                    }
                }
            }
            Assert.IsTrue(chosenPsos.Count >= 8, $"Expected >= 8 distinct PSOs, found {chosenPsos.Count}");

            string workDb = Path.Combine(workDir, "shaderless.db");
            void WriteFullWorkDb()
            {
                var needed = chosenPsos.SelectMany(p => SCSKiller.Core.Planning.Rehydrate.References([p])).Distinct().ToList();
                using var fs = File.Create(workDb);
                foreach (var h in needed)
                    SCSKiller.Core.Planning.PsoDb.Write(fs, 'B', blobsByHash[h]);
                foreach (var p in chosenPsos)
                    SCSKiller.Core.Planning.PsoDb.Write(fs, p.Tag, p.Payload);
            }
            WriteFullWorkDb();

            // PASS 1: Run real multi-batch warmer (2 batches of 8 PSOs) with 64 KB maxNvidiaPartBytes
            // so real RTX 3070 shader records (~6-15 KB each) pack multiple shaders per file and spill cleanly across contiguous parts!
            int halfBatch = Math.Max(4, chosenPsos.Count / 2);
            var pass1Events = new List<WarmerEvent>();
            bool pass1Ok = await ProtonRunner.RunWarmerAsync(
                testGame,
                workDir,
                ev => pass1Events.Add(ev),
                CancellationToken.None,
                isIncrementalRun: false,
                maxItemsPerBatch: halfBatch,
                maxNvidiaPartBytes: 65_536);

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
                string prefixesRoot = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "prefixes");
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
        string? prevOverride = SCSKiller.Linux.Core.SettingsManager.TestSettingsPathOverride;

        try
        {
            SCSKiller.Linux.Core.SettingsManager.TestSettingsPathOverride = settingsPath;
            SCSKiller.Linux.Core.SettingsManager.Save(new AppSettings(AutoRecordAllGames: false, AutoCompileOnExit: false));

            string exePath = Path.Combine(tempDir, "TestGame.exe");
            File.WriteAllBytes(exePath, new byte[64]);
            var game = MakeGame("exitmon_test_" + Guid.NewGuid().ToString("N"), GameSource.Heroic, tempDir, exePath);

            bool isRunning = true;
            int compileCalls = 0;
            var exitedSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (var monitor = new SCSKiller.Linux.Core.Games.GameExitMonitor(
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
            SCSKiller.Linux.Core.SettingsManager.TestSettingsPathOverride = prevOverride;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void ReleaseVersion_Is022Beta()
    {
        Assert.AreEqual("0.2.2-beta", SCSKiller.Linux.Core.AppPaths.Version);
    }
}
