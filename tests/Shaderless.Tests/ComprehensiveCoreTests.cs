using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shaderless.Core.Drivers;
using Shaderless.Core.Games;
using Shaderless.Core.Models;
using Shaderless.Core.Pipelines;

namespace Shaderless.Tests;

[TestClass]
public class ComprehensiveCoreTests
{
    private string? _testDataDir;

    [TestInitialize]
    public void InitTestDataDir()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "shaderless-comp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);
        Shaderless.Core.AppPaths.TestDataDirOverride = _testDataDir;
    }

    [TestCleanup]
    public void CleanupTestDataDir()
    {
        Shaderless.Core.AppPaths.TestDataDirOverride = null;
        if (!string.IsNullOrEmpty(_testDataDir) && Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
        _testDataDir = null;
    }

    [TestMethod]
    public async Task Test_DriverDetector_DetectsHardware()
    {
        var backend = await DriverDetector.DetectActiveBackendAsync();
        Assert.IsNotNull(backend, "Driver backend must not be null.");

        var info = await backend.GetDriverInfoAsync();
        Assert.IsNotNull(info, "DriverInfo must not be null.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(info.DeviceName), "DeviceName must not be empty.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(info.DriverVersion), "DriverVersion must not be empty.");
        Assert.IsTrue(Directory.Exists(info.ShaderCachePath), $"Shader cache directory should exist at: {info.ShaderCachePath}");
        Assert.IsTrue(info.CachedFilesCount >= 0, "Cached file count must be non-negative.");

        var envs = backend.GetRecommendedEnvironmentVariables();
        Assert.IsTrue(envs.Contains("SHADER_DISK_CACHE") || envs.Contains("MESA"), "Recommended environment variables must be populated.");
    }

    [TestMethod]
    public async Task Test_GameDiscovery_SteamSource()
    {
        var source = new SteamLinuxSource();
        var games = await source.DiscoverGamesAsync();

        Assert.IsNotNull(games);
        Assert.IsTrue(games.Count > 0, "Steam should discover installed games on this machine.");

        foreach (var game in games)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(game.Id), "Game Id must not be empty.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(game.Name), "Game Name must not be empty.");
            Assert.IsTrue(Directory.Exists(game.InstallPath), $"InstallPath must exist: {game.InstallPath}");
            Assert.AreEqual(GameSource.Steam, game.Source);
        }
    }

    [TestMethod]
    public async Task Test_GameDiscovery_HeroicSource()
    {
        var source = new HeroicLinuxSource();
        var games = await source.DiscoverGamesAsync();

        Assert.IsNotNull(games);
        Assert.IsTrue(games.Count > 0, "Heroic should discover installed games on this machine.");
        var hike = games.FirstOrDefault(g => g.Name.Contains("Short Hike", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(hike, "A Short Hike should be discovered from Heroic.");
        Assert.IsTrue(Directory.Exists(hike.InstallPath), "Install path for A Short Hike must exist.");
    }

    [TestMethod]
    public async Task Test_GameDiscovery_LutrisSource_DoesNotCrash()
    {
        var source = new LutrisLinuxSource();
        var games = await source.DiscoverGamesAsync();
        Assert.IsNotNull(games);
    }

    [TestMethod]
    public async Task Test_GameManager_StrictPhysicalDiskTruth()
    {
        var backend = await DriverDetector.DetectActiveBackendAsync();
        var manager = new GameManager();
        var games = await manager.ScanAllGamesAsync(backend);

        Assert.IsNotNull(games);
        Assert.IsTrue(games.Count >= 10, $"Expected at least 10 games, found {games.Count}");

        foreach (var g in games)
        {
            if (g.CacheSizeBytes > 0 && ProtonRunner.HasShaderlessCompiledArtifacts(g))
            {
                Assert.IsTrue(
                    g.State == CacheState.Cached || g.State == CacheState.Outdated,
                    $"Game {g.Name} with {g.CacheSizeBytes} bytes and Shaderless artifacts must be Cached or Outdated.");
            }
            else
            {
                Assert.AreEqual(
                    CacheState.NeedsCompilation,
                    g.State,
                    $"Game {g.Name} without Shaderless compilation artifacts (or 0 bytes) must be NeedsCompilation.");
            }
        }
    }

    [TestMethod]
    public void Test_GameEngineDetector_EdgeCases()
    {
        // Nonexistent path
        var (eng1, count1) = GameEngineDetector.Detect("/nonexistent/path/123");
        Assert.AreEqual(GameEngine.Unknown, eng1);
        Assert.AreEqual(0, count1);

        // Null/empty path for executable
        var exe = GameEngineDetector.FindExecutable("/nonexistent/path/123");
        Assert.IsNull(exe);

        // Real Cyberpunk directory test
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string cpPath = Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Cyberpunk 2077");
        if (File.Exists(Path.Combine(cpPath, "bin", "x64", "Cyberpunk2077.exe")))
        {
            var (cpEng, cpCount) = GameEngineDetector.Detect(cpPath);
            Assert.AreEqual(GameEngine.RedEngine, cpEng);
            Assert.IsTrue(cpCount > 5000);
        }
    }

    [TestMethod]
    public async Task Test_ShaderCompilerCoordinator_RunsEndToEnd()
    {
        string tempDataDir = Path.Combine(Path.GetTempPath(), "shaderless_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDataDir);
        Shaderless.Core.AppPaths.TestDataDirOverride = tempDataDir;

        try
        {
            var manager = new GameManager();
            var backend = await DriverDetector.DetectActiveBackendAsync();
            var coordinator = new ShaderCompilerCoordinator(manager, backend);

            string workDir = Path.Combine(Shaderless.Core.AppPaths.DataDir, "work", "test_verification_game");
            Directory.CreateDirectory(workDir);
            string recDb = Path.Combine(workDir, "shaderless.db");
            await File.WriteAllBytesAsync(recDb, new byte[] { (byte)'S', (byte)'C', (byte)'S', (byte)'K', 2, 0, 0, 0 });

            var testGame = new GameInfo(
                Id: "test_verification_game",
                Name: "Test Verification Game",
                Source: GameSource.Manual,
                InstallPath: "/tmp",
                ExecutablePath: "/bin/sh",
                PrefixPath: null,
                BannerPath: null,
                Engine: GameEngine.DirectX12,
                State: CacheState.NeedsCompilation,
                TotalShadersEstimated: 100,
                CompiledShadersCount: 0,
                LastCompiledAt: null
            );

            var progressSteps = new List<CompilationProgress>();
            await foreach (var step in coordinator.CompileGameShadersAsync(testGame))
            {
                progressSteps.Add(step);
                Assert.IsFalse(step.Failed, $"Step failed with: {step.ErrorMessage}");
            }

            Assert.IsTrue(progressSteps.Count > 0, "Coordinator must emit progress steps.");
            var last = progressSteps.Last();
            Assert.IsTrue(last.IsComplete, "Final progress step must be marked complete.");
            Assert.AreEqual(100.0, last.Percentage, "Final percentage must be 100%.");
        }
        finally
        {
            Shaderless.Core.AppPaths.TestDataDirOverride = null;
            try { if (Directory.Exists(tempDataDir)) Directory.Delete(tempDataDir, true); } catch { }
        }
    }

    [TestMethod]
    public void Test_FossilizeLocator_FindsReplayer()
    {
        string? path = FossilizeLocator.FindFossilizeReplay();
        Assert.IsNotNull(path, "fossilize_replay must be found on this system.");
        Assert.IsTrue(File.Exists(path), $"Path reported by locator must exist: {path}");
    }

    [TestMethod]
    public async Task Test_DriverBackend_ClearGameCache_RemovesTargetCache()
    {
        var backend = await DriverDetector.DetectActiveBackendAsync();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string fakeAppId = "9999999";
        string fakeCacheDir = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache", fakeAppId, "nvidiav1");
        Directory.CreateDirectory(fakeCacheDir);
        string fakeFile = Path.Combine(fakeCacheDir, "test_shader.bin");
        await File.WriteAllBytesAsync(fakeFile, new byte[1024 * 64]);

        try
        {
            // Verify stats before
            var (bytesBefore, filesBefore, _) = await backend.GetGameCacheStatsAsync(fakeAppId, GameSource.Steam);
            Assert.AreEqual(64 * 1024, bytesBefore);
            Assert.AreEqual(1, filesBefore);

            // Clear cache
            await backend.ClearGameCacheAsync(fakeAppId, GameSource.Steam);

            // Verify stats after
            var (bytesAfter, filesAfter, _) = await backend.GetGameCacheStatsAsync(fakeAppId, GameSource.Steam);
            Assert.AreEqual(0L, bytesAfter);
            Assert.AreEqual(0, filesAfter);
        }
        finally
        {
            string rootAppDir = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache", fakeAppId);
            if (Directory.Exists(rootAppDir))
            {
                try { Directory.Delete(rootAppDir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test_UnrealReader_DetectDawnwalker()
    {
        string dawnwalkerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "Steam", "steamapps", "common", "The Blood of Dawnwalker");
        string exePath = Path.Combine(dawnwalkerDir, "Dawnwalker", "Binaries", "Win64", "Dawnwalker.exe");
        if (!File.Exists(exePath)) return;

        var game = new Shaderless.Engine.Game("3751260", "The Blood of Dawnwalker", Shaderless.Engine.Store.Steam, dawnwalkerDir, exePath);

        var reader = new Shaderless.Engine.Unreal.UnrealReader(_testDataDir);
        var engine = reader.Detect(game, out string notes);

        Assert.IsNotNull(engine, $"UnrealReader should detect Dawnwalker! Notes: {notes}");
        Console.WriteLine($"Dawnwalker Detected: Family={engine.Family}, Version={engine.Version}, Fork={engine.Fork}, Api={engine.GraphicsApi}, Notes={notes}");
    }

    [TestMethod]
    public void Test_Dawnwalker_FullPipeline_PlanAndMaterialize()
    {
        string dawnwalkerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "Steam", "steamapps", "common", "The Blood of Dawnwalker");
        string exePath = Path.Combine(dawnwalkerDir, "Dawnwalker", "Binaries", "Win64", "Dawnwalker.exe");
        if (!File.Exists(exePath)) return;

        var game = new Shaderless.Engine.Game("3751260", "The Blood of Dawnwalker", Shaderless.Engine.Store.Steam, dawnwalkerDir, exePath);

        var reader = new Shaderless.Engine.Unreal.UnrealReader(_testDataDir);
        var engine = reader.Detect(game, out string notes);
        Assert.IsNotNull(engine);

        var progress = new Progress<string>(s => Console.WriteLine($"[Index] {s}"));
        var index = reader.Index(game, engine, progress, CancellationToken.None);
        Console.WriteLine($"[Index] Done. Unique shaders: {index.Shaders.Count}, Shader maps: {index.Maps.Count}");

        var caps = new Shaderless.Engine.VendorCaps(
            "nvidia-1",
            CacheKeyedByExeName: true,
            StateIndependentCache: true,
            CacheSizeConfigurable: true,
            PerStageCache: true,
            RtCacheGranularity: Shaderless.Engine.RtCacheGranularity.Collection,
            PackageKeyed: true);
        string workDir = Path.Combine(Path.GetTempPath(), "shaderless_dawnwalker_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        string genDb = Path.Combine(workDir, "shaderless_gen.db");

        try
        {
            var planner = new Shaderless.Engine.Planning.Planner();
            var plan = planner.Build(game, engine, index, null, caps, workDir, progress, CancellationToken.None);
            Console.WriteLine($"[Plan] Done. Plan file: {plan.FilePath}, Generated PSOs (PerStageCache=true): {plan.Stats.Generated}, RootSigs: {plan.Stats.RootSignatures}");

            planner.Materialize(plan, game, engine, reader, null, workDir, CancellationToken.None);
            Assert.IsTrue(File.Exists(genDb), "shaderless_gen.db must be generated by Materialize!");
            var fi = new FileInfo(genDb);
            Console.WriteLine($"[Materialize] Done. shaderless_gen.db size: {fi.Length:N0} bytes");
            Assert.IsTrue(fi.Length > 0, "shaderless_gen.db must not be empty!");
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
        }
    }

    [TestMethod]
    public void Test_ProtonRunner_FinalizeDriverCaches_PromotesReadOnlyBuckets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_test_cache_" + Guid.NewGuid().ToString("N"));
        string vkd3dDir = Path.Combine(tempRoot, "VKD3D_shader_cache");
        string glCacheDir = Path.Combine(tempRoot, "nvidiav1", "GLCache");
        string leafDir = Path.Combine(glCacheDir, "ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        Directory.CreateDirectory(vkd3dDir);
        Directory.CreateDirectory(leafDir);

        try
        {
            // Simulate explorer.exe 32-byte stub in bucket 0, warmer 1MB cache in bucket 1, and game 256KB cache in bucket 2
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache0.bin"), new byte[32]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache0.toc"), new byte[32]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache1.bin"), new byte[1024 * 1024]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache1.toc"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache2.bin"), new byte[256 * 1024]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache2.toc"), new byte[1024]);

            File.WriteAllBytes(Path.Combine(vkd3dDir, "vkd3d-proton.game.exe.cache.write"), new byte[8192]);

            ProtonRunner.FinalizeDriverCaches(tempRoot, vkd3dDir, glCacheDir);

            Assert.IsFalse(File.Exists(Path.Combine(leafDir, "steamapp_shader_cache0.bin")), "32-byte stub bucket 0 should be deleted");
            Assert.IsTrue(File.Exists(Path.Combine(leafDir, "steamapp_merged_shader_cache.bin")), "Largest bucket should be promoted to steamapp_merged_shader_cache.bin");
            Assert.AreEqual(1024 * 1024, new FileInfo(Path.Combine(leafDir, "steamapp_merged_shader_cache.bin")).Length);
            Assert.IsTrue(File.Exists(Path.Combine(leafDir, "steam_shader_cache.bin")), "Second bucket should be promoted to steam_shader_cache.bin");
            Assert.AreEqual(256 * 1024, new FileInfo(Path.Combine(leafDir, "steam_shader_cache.bin")).Length);
            Assert.IsTrue(File.Exists(Path.Combine(vkd3dDir, "vkd3d-proton.game.exe.cache")), "VKD3D .cache.write should be promoted to .cache");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_Sfi0_HeapIndexing_And_RootSig_Uncovered()
    {
        static byte[] BuildDxilContainer(ulong sfiFlags)
        {
            // Header (32 bytes) + 2 chunk offsets (8 bytes) + DXIL chunk (8+4=12 bytes) + SFI0 chunk (8+8=16 bytes) = 68 bytes
            var buf = new byte[68];
            "DXBC"u8.CopyTo(buf.AsSpan(0, 4));
            BitConverter.TryWriteBytes(buf.AsSpan(20, 4), 1u);   // version = 1
            BitConverter.TryWriteBytes(buf.AsSpan(24, 4), 68u);  // total size
            BitConverter.TryWriteBytes(buf.AsSpan(28, 4), 2u);   // chunk count = 2
            BitConverter.TryWriteBytes(buf.AsSpan(32, 4), 40u);  // chunk 0 offset (DXIL)
            BitConverter.TryWriteBytes(buf.AsSpan(36, 4), 52u);  // chunk 1 offset (SFI0)

            "DXIL"u8.CopyTo(buf.AsSpan(40, 4));
            BitConverter.TryWriteBytes(buf.AsSpan(44, 4), 4u);
            BitConverter.TryWriteBytes(buf.AsSpan(48, 4), 0x00000066u); // kind 0 (Pixel), SM 6.6

            "SFI0"u8.CopyTo(buf.AsSpan(52, 4));
            BitConverter.TryWriteBytes(buf.AsSpan(56, 4), 8u);
            BitConverter.TryWriteBytes(buf.AsSpan(60, 8), sfiFlags);
            return buf;
        }

        var plainShader = Shaderless.Engine.Unreal.ShaderContainer.Parse(
            BuildDxilContainer(0),
            "0000000000000000000000000000000000000001",
            new Shaderless.Engine.ResourceCounts(0, 0, 0, 0));
        Assert.IsNotNull(plainShader);
        Assert.IsFalse(plainShader.ResourceHeapIndexing);
        Assert.IsFalse(plainShader.SamplerHeapIndexing);

        var resHeapShader = Shaderless.Engine.Unreal.ShaderContainer.Parse(
            BuildDxilContainer(0x02000000UL),
            "0000000000000000000000000000000000000002",
            new Shaderless.Engine.ResourceCounts(0, 0, 0, 0));
        Assert.IsNotNull(resHeapShader);
        Assert.IsTrue(resHeapShader.ResourceHeapIndexing);
        Assert.IsFalse(resHeapShader.SamplerHeapIndexing);

        var sampHeapShader = Shaderless.Engine.Unreal.ShaderContainer.Parse(
            BuildDxilContainer(0x04000000UL),
            "0000000000000000000000000000000000000003",
            new Shaderless.Engine.ResourceCounts(0, 0, 0, 0));
        Assert.IsNotNull(sampHeapShader);
        Assert.IsFalse(sampHeapShader.ResourceHeapIndexing);
        Assert.IsTrue(sampHeapShader.SamplerHeapIndexing);

        var nonBindlessRs = new Shaderless.Engine.Planning.RootSig.Ranges(0x3eu, []);
        var resBindlessRs = new Shaderless.Engine.Planning.RootSig.Ranges(0x43eu, []);
        var sampBindlessRs = new Shaderless.Engine.Planning.RootSig.Ranges(0x83eu, []);

        Assert.IsNull(Shaderless.Engine.Planning.RootSig.Uncovered(nonBindlessRs, Shaderless.Engine.Stage.Pixel, plainShader));
        Assert.AreEqual("Pixel resource heap indexing",
            Shaderless.Engine.Planning.RootSig.Uncovered(nonBindlessRs, Shaderless.Engine.Stage.Pixel, resHeapShader));
        Assert.IsNull(Shaderless.Engine.Planning.RootSig.Uncovered(resBindlessRs, Shaderless.Engine.Stage.Pixel, resHeapShader));

        Assert.AreEqual("Pixel sampler heap indexing",
            Shaderless.Engine.Planning.RootSig.Uncovered(nonBindlessRs, Shaderless.Engine.Stage.Pixel, sampHeapShader));
        Assert.IsNull(Shaderless.Engine.Planning.RootSig.Uncovered(sampBindlessRs, Shaderless.Engine.Stage.Pixel, sampHeapShader));
    }

    [TestMethod]
    public void Test_RecorderCountsCache_HitsAndInvalidatesOnFileChanges()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_counts_" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(tempRoot, "data");
        string installDir = Path.Combine(tempRoot, "game");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(installDir);
        string exePath = Path.Combine(installDir, "FastScanTest.exe");
        File.WriteAllBytes(exePath, new byte[128]);

        string? prevOverride = Shaderless.Core.AppPaths.TestDataDirOverride;
        Shaderless.Core.AppPaths.TestDataDirOverride = dataDir;
        try
        {
            var game = new GameInfo(
                Id: "fast_scan_game",
                Name: "Fast Scan Game",
                Source: GameSource.Steam,
                InstallPath: installDir,
                ExecutablePath: exePath,
                PrefixPath: null,
                BannerPath: null,
                Engine: GameEngine.DirectX12,
                State: CacheState.NeedsCompilation,
                TotalShadersEstimated: 1000,
                CompiledShadersCount: 0,
                LastCompiledAt: null);

            Assert.AreEqual((0, 0), RecorderManager.GetRecordedAndUnwarmedCounts(game));

            string recPath = RecorderManager.RecordingPath(game.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(recPath)!);
            using (var fs = File.Create(recPath))
            {
                Shaderless.Engine.Planning.PsoDb.Write(fs, 'C', new byte[] { 1, 2, 3, 4 });
                Shaderless.Engine.Planning.PsoDb.Write(fs, 'C', new byte[] { 5, 6, 7, 8 });
            }

            var firstKey = Shaderless.Engine.Planning.PsoDb.Read(recPath).First().Key;
            Assert.AreEqual((2, 0), RecorderManager.GetRecordedAndUnwarmedCounts(game));
            Assert.IsTrue(File.Exists(Path.Combine(dataDir, "recorder-counts.json")));

            // Save 1 warmed key -> cache invalidates and Unwarmed becomes 1
            RecorderManager.SaveWarmedKeys(game, new[] { firstKey }, "615.78.08|proton-test");
            Assert.AreEqual((2, 1), RecorderManager.GetRecordedAndUnwarmedCounts(game));

            // Mutating returned HashSet from LoadWarmedKeys must not corrupt the internal cache
            var loadedWarmed = RecorderManager.LoadWarmedKeys(game.Id);
            Assert.AreEqual(1, loadedWarmed.Count);
            loadedWarmed.Add("ffffffffffffffffffffffffffffffffffffffff");
            Assert.AreEqual(1, RecorderManager.LoadWarmedKeys(game.Id).Count);

            // Adding an inbox shaderless.db record invalidates the stamp and bumps counts to (3, 2)
            string inboxPath = Path.Combine(installDir, "shaderless.db");
            using (var fs = File.Create(inboxPath))
            {
                Shaderless.Engine.Planning.PsoDb.Write(fs, 'C', new byte[] { 9, 10, 11, 12 });
            }
            Assert.AreEqual((3, 2), RecorderManager.GetRecordedAndUnwarmedCounts(game));

            // Clearing warmed keys drops Unwarmed back to 0 while Recorded stays 3
            RecorderManager.ClearWarmedKeys(game.Id, game);
            Assert.AreEqual((3, 0), RecorderManager.GetRecordedAndUnwarmedCounts(game));
        }
        finally
        {
            Shaderless.Core.AppPaths.TestDataDirOverride = prevOverride;
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_CaptureRunningExeNames_SnapshotAndWarmerExclusion()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_proc_" + Guid.NewGuid().ToString("N"));
        string procDir = Path.Combine(tempRoot, "proc");
        string gameDir = Path.Combine(tempRoot, "Onimusha");
        Directory.CreateDirectory(procDir);
        Directory.CreateDirectory(gameDir);
        string exePath = Path.Combine(gameDir, "Onimusha.exe");
        File.WriteAllBytes(exePath, new byte[64]);

        try
        {
            Directory.CreateDirectory(Path.Combine(procDir, "1001"));
            File.WriteAllText(Path.Combine(procDir, "1001", "cmdline"), "Z:\\Games\\Onimusha\\Onimusha.exe\0-dx12\0");

            Directory.CreateDirectory(Path.Combine(procDir, "1002"));
            File.WriteAllText(Path.Combine(procDir, "1002", "cmdline"), "/home/user/.local/share/shaderless/work/2638890/shaderless_warm.exe\0");

            Directory.CreateDirectory(Path.Combine(procDir, "1003"));
            File.WriteAllText(Path.Combine(procDir, "1003", "cmdline"), "/usr/bin/bash\0-l\0");

            var snapshot = ProtonRunner.CaptureRunningExeNames(procDir);
            Assert.IsTrue(snapshot.Contains("onimusha.exe"));
            Assert.IsFalse(snapshot.Contains("shaderless_warm.exe"));

            var runningGame = new GameInfo("2638890", "Onimusha", GameSource.Steam, gameDir, exePath, null, null, GameEngine.ReEngine, CacheState.Cached, 7200, 7200, null);
            var idleGame = new GameInfo("999999", "IdleGame", GameSource.Steam, Path.Combine(tempRoot, "Idle"), Path.Combine(tempRoot, "Idle", "Idle.exe"), null, null, GameEngine.Unity, CacheState.NeedsCompilation, 4500, 0, null);

            Assert.IsTrue(ProtonRunner.IsGameRunning(runningGame, snapshot, procDir));
            Assert.IsFalse(ProtonRunner.IsGameRunning(idleGame, snapshot, procDir));
            Assert.IsFalse(ProtonRunner.IsGameRunning(runningGame, new HashSet<string>(StringComparer.OrdinalIgnoreCase), procDir));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_GameEngineDetector_FastUnityAndBoundedUnrealDetection()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_det_" + Guid.NewGuid().ToString("N"));
        try
        {
            string unityDir = Path.Combine(tempRoot, "UnityGame");
            Directory.CreateDirectory(Path.Combine(unityDir, "UnityGame_Data", "StreamingAssets"));
            string unityExe = Path.Combine(unityDir, "UnityGame.exe");
            File.WriteAllBytes(unityExe, new byte[128]);
            File.WriteAllBytes(Path.Combine(unityDir, "UnityPlayer.dll"), new byte[64]);

            Assert.AreEqual(GameEngine.Unity, GameEngineDetector.Detect(unityDir).Engine);
            Assert.AreEqual(unityExe, GameEngineDetector.FindExecutable(unityDir));

            string ue5Dir = Path.Combine(tempRoot, "UE5Game");
            Directory.CreateDirectory(Path.Combine(ue5Dir, "Engine", "Binaries"));
            string paksDir = Path.Combine(ue5Dir, "Project", "Content", "Paks");
            Directory.CreateDirectory(paksDir);
            File.WriteAllBytes(Path.Combine(paksDir, "pakchunk0-Windows.ucas"), new byte[64]);

            Assert.AreEqual(GameEngine.UnrealEngine5, GameEngineDetector.Detect(ue5Dir).Engine);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static (string BlobHex, byte[] RawBytes, Shaderless.Engine.Planning.PsoDb.Rec BlobRec) MakeTestShaderBlob(byte seed)
    {
        byte[] raw = new byte[64];
        for (int i = 0; i < raw.Length; i++)
            raw[i] = (byte)(seed * 31 + i * 17 + 3);
        byte[] sha = System.Security.Cryptography.SHA1.HashData(raw);
        string hex = Shaderless.Engine.Planning.PsoDb.Hex(sha);
        byte[] payload = new byte[20 + raw.Length];
        sha.CopyTo(payload, 0);
        raw.CopyTo(payload, 20);
        return (hex, raw, new Shaderless.Engine.Planning.PsoDb.Rec('B', payload));
    }

    private static Shaderless.Engine.Planning.PsoDb.Rec MakeTestComputePso(string rsHex, string csHex, uint flags = 0)
    {
        byte[] payload = new byte[48];
        Convert.FromHexString(rsHex).CopyTo(payload, 0);
        Convert.FromHexString(csHex).CopyTo(payload, 20);
        BitConverter.TryWriteBytes(payload.AsSpan(44, 4), flags);
        return new Shaderless.Engine.Planning.PsoDb.Rec('C', payload);
    }

    [TestMethod]
    public void Test_MergeWarmedSnapshots_MatchesCombinedRecordingsWarmedByteForByte()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_merge_warm_" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(tempRoot, "data");
        string installDir = Path.Combine(tempRoot, "game");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(installDir);
        string exePath = Path.Combine(installDir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[64]);

        string? prevOverride = Shaderless.Core.AppPaths.TestDataDirOverride;
        Shaderless.Core.AppPaths.TestDataDirOverride = dataDir;
        try
        {
            var (rsHex, _, rsBlob) = MakeTestShaderBlob(1);
            var (cs1Hex, _, cs1Blob) = MakeTestShaderBlob(2);
            var (cs2Hex, _, cs2Blob) = MakeTestShaderBlob(3);
            var (cs3Hex, _, cs3Blob) = MakeTestShaderBlob(4);
            var (cs4Hex, _, cs4Blob) = MakeTestShaderBlob(5);

            var pso1 = MakeTestComputePso(rsHex, cs1Hex);
            var pso2 = MakeTestComputePso(rsHex, cs2Hex);
            var pso3 = MakeTestComputePso(rsHex, cs3Hex);
            var pso4 = MakeTestComputePso(rsHex, cs4Hex);

            string db1 = Path.Combine(tempRoot, "db1.db");
            string db2 = Path.Combine(tempRoot, "db2.db");
            string dbCombined = Path.Combine(tempRoot, "combined.db");

            using (var fs = File.Create(db1))
            {
                foreach (var b in new[] { rsBlob, cs1Blob, cs2Blob, cs3Blob })
                    Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', b.Payload);
                foreach (var p in new[] { pso1, pso2, pso3 })
                    Shaderless.Engine.Planning.PsoDb.Write(fs, p.Tag, p.Payload);
            }

            // db2 overlaps on pso2 and adds pso4
            using (var fs = File.Create(db2))
            {
                foreach (var b in new[] { rsBlob, cs2Blob, cs4Blob })
                    Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', b.Payload);
                foreach (var p in new[] { pso2, pso4 })
                    Shaderless.Engine.Planning.PsoDb.Write(fs, p.Tag, p.Payload);
            }

            using (var fs = File.Create(dbCombined))
            {
                foreach (var b in new[] { rsBlob, cs1Blob, cs2Blob, cs3Blob, cs4Blob })
                    Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', b.Payload);
                foreach (var p in new[] { pso1, pso2, pso3, pso4 })
                    Shaderless.Engine.Planning.PsoDb.Write(fs, p.Tag, p.Payload);
            }

            byte[] snap1 = Shaderless.Engine.App.Recordings.Warmed([db1], []);
            byte[] snap2 = Shaderless.Engine.App.Recordings.Warmed([db2], []);
            byte[] expected = Shaderless.Engine.App.Recordings.Warmed([dbCombined], []);

            byte[] merged = RecorderManager.MergeWarmedSnapshots(snap1, snap2);
            CollectionAssert.AreEqual(expected, merged, "Merged SCSKWRM1 snapshot must match Recordings.Warmed on combined DBs byte-for-byte.");

            var game = new GameInfo("merge_game", "Merge Game", GameSource.Steam, installDir, exePath, null, null, GameEngine.UnrealEngine5, CacheState.Cached, 100, 100, null);
            RecorderManager.SaveWarmedKeys(game, [pso1.Key, pso2.Key, pso3.Key], "drv1|proton1", prebuiltWarmedSnapshot: snap1);
            RecorderManager.SaveWarmedKeys(game, [pso4.Key], "drv1|proton1", prebuiltWarmedSnapshot: snap2, mergeWithExistingWarmedSnapshot: true);

            var savedKeys = RecorderManager.LoadWarmedKeys(game.Id, "drv1|proton1");
            Assert.HasCount(4, savedKeys);
            byte[] onDiskWarmed = File.ReadAllBytes(Path.Combine(RecorderManager.GameDir(game.Id), Shaderless.Engine.App.Recordings.WarmedFile));
            CollectionAssert.AreEqual(expected, onDiskWarmed);
        }
        finally
        {
            Shaderless.Core.AppPaths.TestDataDirOverride = prevOverride;
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_FilterDatabasesToUnwarmed_PrunesUnreferencedBlobsAndKeepsCrossDbTemplates()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_filter_blobs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var (rsSharedHex, _, rsSharedBlob) = MakeTestShaderBlob(10);
            var (csWarmed1Hex, _, csWarmed1Blob) = MakeTestShaderBlob(11);
            var (csWarmed2Hex, _, csWarmed2Blob) = MakeTestShaderBlob(12);
            var (csTemplateHex, _, csTemplateBlob) = MakeTestShaderBlob(13);
            var (csNewItemHex, _, csNewItemBlob) = MakeTestShaderBlob(14);

            var warmedPso1 = MakeTestComputePso(rsSharedHex, csWarmed1Hex);
            var warmedPso2 = MakeTestComputePso(rsSharedHex, csWarmed2Hex);
            var templatePso = MakeTestComputePso(rsSharedHex, csTemplateHex);

            // Build a 'P' plan item in genDb referencing templatePso (in recDb), rsShared (in recDb), and csNewItem (in genDb)
            byte[] planItemPayload = new byte[44 + 24];
            Convert.FromHexString(templatePso.Key).CopyTo(planItemPayload, 0);
            Convert.FromHexString(rsSharedHex).CopyTo(planItemPayload, 20);
            BitConverter.TryWriteBytes(planItemPayload.AsSpan(40, 4), 1u); // 1 stage override
            BitConverter.TryWriteBytes(planItemPayload.AsSpan(44, 4), 6u); // CS stage
            Convert.FromHexString(csNewItemHex).CopyTo(planItemPayload, 48);
            var newPlanItem = new Shaderless.Engine.Planning.PsoDb.Rec('P', planItemPayload);
            var layerRec = new Shaderless.Engine.Planning.PsoDb.Rec('W', new byte[40]);

            string recDb = Path.Combine(tempRoot, "shaderless.db");
            string genDb = Path.Combine(tempRoot, "shaderless_gen.db");

            using (var fs = File.Create(recDb))
            {
                foreach (var b in new[] { rsSharedBlob, csWarmed1Blob, csWarmed2Blob, csTemplateBlob })
                    Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', b.Payload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, warmedPso1.Tag, warmedPso1.Payload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, warmedPso2.Tag, warmedPso2.Payload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, templatePso.Tag, templatePso.Payload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, layerRec.Tag, layerRec.Payload);
            }

            using (var fs = File.Create(genDb))
            {
                Shaderless.Engine.Planning.PsoDb.Write(fs, 'B', csNewItemBlob.Payload);
                Shaderless.Engine.Planning.PsoDb.Write(fs, newPlanItem.Tag, newPlanItem.Payload);
            }

            var warmedSet = new HashSet<string>(StringComparer.Ordinal)
            {
                warmedPso1.Key,
                warmedPso2.Key,
                templatePso.Key
            };

            var (allKeys, keptExec, alreadyWarmed) = RecorderManager.FilterDatabasesToUnwarmed(recDb, genDb, warmedSet);
            Assert.HasCount(4, allKeys);
            Assert.AreEqual(3, alreadyWarmed);
            Assert.AreEqual(2, keptExec, "Expected unwarmed 'P' item + its required 'C' template.");

            var recAfter = Shaderless.Engine.Planning.PsoDb.Read(recDb).ToList();
            var recBlobHashes = recAfter
                .Where(r => r.Tag == 'B')
                .Select(r => Shaderless.Engine.Planning.PsoDb.Hex(r.Payload.AsSpan(0, 20)))
                .ToHashSet(StringComparer.Ordinal);

            Assert.DoesNotContain(csWarmed1Hex, recBlobHashes, "Unreferenced warmed blob 1 must be pruned.");
            Assert.DoesNotContain(csWarmed2Hex, recBlobHashes, "Unreferenced warmed blob 2 must be pruned.");
            Assert.Contains(rsSharedHex, recBlobHashes, "Shared root signature referenced by genDb item must stay.");
            Assert.Contains(csTemplateHex, recBlobHashes, "Template shader blob must stay.");
            Assert.IsTrue(recAfter.Any(r => r.Tag == 'W'), "Layer metadata 'W' record must be preserved.");
            Assert.IsTrue(recAfter.Any(r => r.Tag == 'C' && r.Key == templatePso.Key), "Required template PSO must be preserved.");
            Assert.IsFalse(recAfter.Any(r => r.Tag == 'C' && r.Key == warmedPso1.Key), "Warmed non-template PSO must be removed.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_MaterializeRecordedPipelines_ExtractsOnlyUnwarmedDeltaAndPrunesBlobs()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_mat_rec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var (rsHex, _, rsBlob) = MakeTestShaderBlob(20);
            var csBlobs = Enumerable.Range(21, 5).Select(i => MakeTestShaderBlob((byte)i)).ToList();
            var psos = csBlobs.Select(b => MakeTestComputePso(rsHex, b.BlobHex)).ToList();

            string storePath = Path.Combine(tempRoot, "recording.db");
            var storeRecs = new List<Shaderless.Engine.Planning.PsoDb.Rec> { rsBlob };
            storeRecs.AddRange(csBlobs.Select(b => b.BlobRec));
            storeRecs.AddRange(psos);
            storeRecs.Add(new Shaderless.Engine.Planning.PsoDb.Rec('W', new byte[40]));
            Shaderless.Engine.Planning.PsoDb.WriteCompact(storePath, storeRecs);

            var scsGame = new Shaderless.Engine.Game("bl4", "Borderlands 4", Shaderless.Engine.Store.Epic, tempRoot, Path.Combine(tempRoot, "Borderlands4.exe"));
            string outDb = Path.Combine(tempRoot, "out", "shaderless.db");

            int readerCalls = 0;
            (Shaderless.Engine.IEngineReader, Shaderless.Engine.EngineInfo?) FailIfCalledReader()
            {
                readerCalls++;
                throw new InvalidOperationException("Archive reader should not be invoked when recording.db already has all blobs!");
            }

            // 1. Fresh Recorded-Only run (0 warmed keys) -> materializes all 5 recorded PSOs
            var fresh = RecorderManager.MaterializeRecordedPipelines(
                storePath,
                scsGame,
                outDb,
                new HashSet<string>(StringComparer.Ordinal),
                FailIfCalledReader);

            Assert.HasCount(5, fresh.AllRecordedKeys);
            Assert.HasCount(5, fresh.NewlyPlannedKeys);
            Assert.AreEqual(5, fresh.KeptExecutableCount);
            Assert.AreEqual(0, fresh.AlreadyWarmedCount);
            Assert.AreEqual(0, readerCalls);
            Assert.IsTrue(File.Exists(outDb));

            // 2. Incremental Delta run (4 warmed keys, +1 new PSO) -> writes ONLY the 1 new PSO and its 2 needed blobs (rs + cs[4])
            File.Delete(outDb);
            var warmedFour = psos.Take(4).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var delta = RecorderManager.MaterializeRecordedPipelines(
                storePath,
                scsGame,
                outDb,
                warmedFour,
                FailIfCalledReader);

            Assert.HasCount(5, delta.AllRecordedKeys);
            Assert.HasCount(1, delta.NewlyPlannedKeys);
            Assert.Contains(psos[4].Key, delta.NewlyPlannedKeys);
            Assert.AreEqual(1, delta.KeptExecutableCount);
            Assert.AreEqual(4, delta.AlreadyWarmedCount);
            Assert.AreEqual(0, readerCalls);

            var deltaOutRecs = Shaderless.Engine.Planning.PsoDb.Read(outDb).ToList();
            Assert.AreEqual(2, deltaOutRecs.Count(r => r.Tag == 'B'), "Only rsBlob + the 1 new CS blob should be written.");
            Assert.AreEqual(1, deltaOutRecs.Count(r => r.Tag == 'C'), "Only the 1 unwarmed compute PSO should be written.");

            // 3. Up-to-date run (all 5 warmed) -> immediate 0-work return, no outDb created
            File.Delete(outDb);
            var warmedAll = psos.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var none = RecorderManager.MaterializeRecordedPipelines(
                storePath,
                scsGame,
                outDb,
                warmedAll,
                FailIfCalledReader);

            Assert.AreEqual(0, none.KeptExecutableCount);
            Assert.AreEqual(5, none.AlreadyWarmedCount);
            Assert.IsFalse(File.Exists(outDb));
            Assert.AreEqual(0, readerCalls);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_GameManager_IncrementalRecordedDeltaAndRecordedOnlyStateTransitions()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_gm_delta_" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(tempRoot, "data");
        Directory.CreateDirectory(dataDir);

        string? prevOverride = Shaderless.Core.AppPaths.TestDataDirOverride;
        Shaderless.Core.AppPaths.TestDataDirOverride = dataDir;
        try
        {
            var gm = new GameManager();
            string gameId = "ea5f9a88243c48908d0205c245fddea0";
            string drv = "595.58.03";
            string proton = "GE-Proton10-32";
            string build = "exe:1000:12345|pak:99999:67890";
            int plannerVer = Shaderless.Engine.Planning.Planner.Version;

            Assert.IsFalse(gm.IsSavedAsRecordedOnly(gameId));
            Assert.IsFalse(gm.CanUseIncrementalRecordedDelta(gameId, drv, proton, build, plannerVer));

            // 1. Saved via Recorded Only -> IsSavedAsRecordedOnly is true, CanUseIncrementalRecordedDelta is false (so normal Re-Compile upgrades to full plan)
            gm.SaveGameCompiled(gameId, 40117, drv, proton, build, plannerVer, recordedOnly: true);
            Assert.IsTrue(gm.IsSavedAsRecordedOnly(gameId));
            Assert.IsFalse(gm.CanUseIncrementalRecordedDelta(gameId, drv, proton, build, plannerVer));

            // 2. Legacy full compile state (PlannerVersion = null, RecordedOnly = false) -> CanUseIncrementalRecordedDelta is true!
            gm.SaveGameCompiled(gameId, 677933, drv, proton, build, plannerVersion: null, recordedOnly: false);
            Assert.IsFalse(gm.IsSavedAsRecordedOnly(gameId));
            Assert.IsTrue(gm.CanUseIncrementalRecordedDelta(gameId, drv, proton, build, plannerVer));

            // 3. Full compile with current Planner.Version -> true when stamps match, false when game patched or planner bumped
            gm.SaveGameCompiled(gameId, 679414, drv, proton, build, plannerVer, recordedOnly: false);
            Assert.IsTrue(gm.CanUseIncrementalRecordedDelta(gameId, drv, proton, build, plannerVer));
            Assert.IsFalse(gm.CanUseIncrementalRecordedDelta(gameId, drv, proton, build + "_patched", plannerVer));
            Assert.IsFalse(gm.CanUseIncrementalRecordedDelta(gameId, drv, proton, build, plannerVer + 1));
            Assert.IsFalse(gm.CanUseIncrementalRecordedDelta(gameId, "599.00.00", proton, build, plannerVer));
        }
        finally
        {
            Shaderless.Core.AppPaths.TestDataDirOverride = prevOverride;
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class StubDriverBackend : IDriverBackend
    {
        public GpuVendor Vendor { get; set; } = GpuVendor.Nvidia;
        public string DriverVersion { get; set; } = "595.58.03";
        public long FakeCacheBytes { get; set; } = 10_000_000L;

        public Task<DriverInfo> GetDriverInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DriverInfo(Vendor, "Test GPU", Vendor.ToString(), DriverVersion, "/tmp", FakeCacheBytes, 4, 0, 0, FakeCacheBytes, DriverCacheStatus.Healthy, null));

        public Task ClearCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAllCachesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearGameCacheAsync(string gameId, GameSource source, string? prefixPath = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string GetRecommendedEnvironmentVariables() => "";

        public Task<(long Bytes, int Files, string Path)> GetGameCacheStatsAsync(string gameId, GameSource source, string? prefixPath = null) =>
            Task.FromResult((FakeCacheBytes, 4, prefixPath ?? ""));
    }

    [TestMethod]
    public async Task Test_CompileGameShadersAsync_RecordedOnlyNoRecordingAndFastIncrementalZeroNew()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_coord_fast_" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(tempRoot, "data");
        string installDir = Path.Combine(tempRoot, "Borderlands4");
        string prefixDir = Path.Combine(tempRoot, "pfx");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(prefixDir);
        string exePath = Path.Combine(installDir, "ShaderlessFakeTestGame.exe");
        File.WriteAllBytes(exePath, new byte[1024]);

        string? prevOverride = Shaderless.Core.AppPaths.TestDataDirOverride;
        Shaderless.Core.AppPaths.TestDataDirOverride = dataDir;
        try
        {
            var realBackend = await DriverDetector.DetectActiveBackendAsync();
            var realDrvInfo = await realBackend.GetDriverInfoAsync();
            string drvVersion = realDrvInfo.DriverVersion;

            var gm = new GameManager();
            var backend = new StubDriverBackend { Vendor = realBackend.Vendor, DriverVersion = drvVersion, FakeCacheBytes = 0 };
            var coord = new ShaderCompilerCoordinator(gm, backend);

            string gameId = "bl4_fast_test";
            var uncompiledGame = new GameInfo(
                Id: gameId,
                Name: "Borderlands 4",
                Source: GameSource.Heroic,
                InstallPath: installDir,
                ExecutablePath: exePath,
                PrefixPath: prefixDir,
                BannerPath: null,
                Engine: GameEngine.UnrealEngine5,
                State: CacheState.NeedsCompilation,
                TotalShadersEstimated: 14000,
                CompiledShadersCount: 0,
                LastCompiledAt: null);

            // Part A: Recorded Only with 0 recorded PSOs fails cleanly & immediately without scanning archives
            var noRecSteps = new List<CompilationProgress>();
            await foreach (var p in coord.CompileGameShadersAsync(uncompiledGame, recordedOnly: true))
                noRecSteps.Add(p);

            var lastNoRec = noRecSteps.Last();
            Assert.IsTrue(lastNoRec.IsComplete);
            Assert.IsTrue(lastNoRec.Failed);
            StringAssert.Contains(lastNoRec.CurrentAction, "No recorded pipelines found yet");

            // Part B: Verified cache + full baseline + all recorded PSOs already warmed -> skips archive scan and finishes at 100%
            backend.FakeCacheBytes = 128 * 1024;
            string cacheDir = ProtonRunner.GetNonSteamShaderCachePath(gameId, prefixDir);
            string leafDir = Path.Combine(cacheDir, "nvidiav1", "GLCache", "deadbeef");
            Directory.CreateDirectory(leafDir);

            // Write a valid 32-byte CDVN header + 1 TOC entry so VerifyCacheInstalled passes strictly
            byte[] binBytes = new byte[64];
            "CDVN"u8.CopyTo(binBytes.AsSpan(0, 4));
            binBytes[4] = 2;
            byte[] tocBytes = new byte[56];
            "CDVN"u8.CopyTo(tocBytes.AsSpan(0, 4));
            tocBytes[4] = 2;
            string binPath = Path.Combine(leafDir, "shaderless.bin");
            string tocPath = Path.Combine(leafDir, "shaderless.toc");
            File.WriteAllBytes(binPath, binBytes);
            File.WriteAllBytes(tocPath, tocBytes);
            File.WriteAllLines(Path.Combine(cacheDir, "installed-files.txt"), [binPath, tocPath]);
            File.WriteAllText(Path.Combine(tempRoot, "user_settings.py"), "user_settings = {}\n");

            string protonStamp = ProtonRunner.GetProtonStamp(uncompiledGame);
            string buildStamp = ProtonRunner.GetGameBuildStamp(uncompiledGame);
            Assert.IsTrue(ProtonRunner.VerifyCacheInstalled(uncompiledGame).Verified, "Test fixture cache must pass VerifyCacheInstalled.");
            Assert.IsTrue(ProtonRunner.IsRuntimeCacheStampValid(uncompiledGame), "Test fixture runtime stamp must be valid.");

            var (rsHex, _, rsBlob) = MakeTestShaderBlob(40);
            var (csHex, _, csBlob) = MakeTestShaderBlob(41);
            var recPso = MakeTestComputePso(rsHex, csHex);

            string storePath = RecorderManager.RecordingPath(gameId);
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            Shaderless.Engine.Planning.PsoDb.WriteCompact(storePath, [rsBlob, csBlob, recPso]);

            RecorderManager.SaveWarmedKeys(uncompiledGame, [recPso.Key, "1111111111111111111111111111111111111111"], $"{drvVersion}|{protonStamp}");
            gm.SaveGameCompiled(gameId, 2, drvVersion, protonStamp, buildStamp, Shaderless.Engine.Planning.Planner.Version, recordedOnly: false);

            var cachedGame = uncompiledGame with { State = CacheState.Cached, CacheSizeBytes = backend.FakeCacheBytes, CompiledShadersCount = 2 };
            var fastSteps = new List<CompilationProgress>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await foreach (var p in coord.CompileGameShadersAsync(cachedGame, recordedOnly: false))
                fastSteps.Add(p);
            sw.Stop();

            Assert.IsTrue(fastSteps.Any(s => s.CurrentAction.Contains("skipping full archive scan", StringComparison.OrdinalIgnoreCase)),
                "Expected Fast Incremental Recorded-Delta status message.");
            Assert.IsFalse(fastSteps.Any(s => s.CurrentAction.Contains("Scanning game archives across all supported engines", StringComparison.OrdinalIgnoreCase)),
                "Must NOT enter full static archive scan when GameBuildStamp and Planner.Version match.");

            var finalFast = fastSteps.Last();
            Assert.IsTrue(finalFast.IsComplete);
            Assert.IsFalse(finalFast.Failed);
            Assert.AreEqual(100.0, finalFast.Percentage);
            StringAssert.Contains(finalFast.CurrentAction, "0 new pipelines");
            Assert.IsLessThan(2000L, sw.ElapsedMilliseconds, "Zero-delta incremental check should finish well under 2 seconds.");
        }
        finally
        {
            Shaderless.Core.AppPaths.TestDataDirOverride = prevOverride;
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static string CreateFakeProtonWithDualLatencyDlls(string rootDir, string folderName = "proton-cachyos-slr")
    {
        string protonDir = Path.Combine(rootDir, folderName);
        string protonBin = Path.Combine(protonDir, "proton");
        string wineLib = Path.Combine(protonDir, "files", "lib", "wine");

        string vkd3dStd = Path.Combine(wineLib, "vkd3d-proton", "x86_64-windows");
        string vkd3dLl = Path.Combine(wineLib, "vkd3d-low-latency", "x86_64-windows");
        string dxvkStd = Path.Combine(wineLib, "dxvk", "x86_64-windows");
        string dxvkLl = Path.Combine(wineLib, "dxvk-low-latency", "x86_64-windows");

        Directory.CreateDirectory(vkd3dStd);
        Directory.CreateDirectory(vkd3dLl);
        Directory.CreateDirectory(dxvkStd);
        Directory.CreateDirectory(dxvkLl);

        File.WriteAllText(protonBin, "#!/usr/bin/env python3\n");
        File.WriteAllBytes(Path.Combine(vkd3dStd, "d3d12.dll"), new byte[70_000]);
        File.WriteAllBytes(Path.Combine(vkd3dStd, "d3d12core.dll"), new byte[100_000]);
        File.WriteAllBytes(Path.Combine(vkd3dLl, "d3d12.dll"), new byte[72_000]);
        File.WriteAllBytes(Path.Combine(vkd3dLl, "d3d12core.dll"), new byte[120_000]);

        File.WriteAllBytes(Path.Combine(dxvkStd, "dxgi.dll"), new byte[75_000]);
        File.WriteAllBytes(Path.Combine(dxvkStd, "d3d11.dll"), new byte[80_000]);
        File.WriteAllBytes(Path.Combine(dxvkLl, "dxgi.dll"), new byte[78_000]);
        File.WriteAllBytes(Path.Combine(dxvkLl, "d3d11.dll"), new byte[90_000]);

        return protonBin;
    }

    [TestMethod]
    public void Test_HeroicLaunchEnv_IgnoresGlobalBgmodWhenNotConfigured_AndResolvesVkd3dCodegenEnv()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_heroic_env_" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(tempRoot, "home");
        string xdgData = Path.Combine(home, ".local", "share");
        string xdgConfig = Path.Combine(home, ".config");
        try
        {
            string protonBin = CreateFakeProtonWithDualLatencyDlls(tempRoot);

            // Global Goverlay bgmod.conf has LOWLATENCY=1, which MUST be ignored for Heroic games that don't invoke bgmod!
            string globalBgDir = Path.Combine(xdgData, "goverlay", "gameconfig", "global");
            Directory.CreateDirectory(globalBgDir);
            File.WriteAllText(Path.Combine(globalBgDir, "bgmod.conf"), """
                [Config]
                mangohud=1
                [Env]
                =game-performance
                PROTON_VKD3D_LOWLATENCY=1
                PROTON_DXVK_LOWLATENCY=1
                """);

            string heroicDir = Path.Combine(xdgConfig, "heroic");
            string gamesCfgDir = Path.Combine(heroicDir, "GamesConfig");
            Directory.CreateDirectory(gamesCfgDir);

            File.WriteAllText(Path.Combine(heroicDir, "config.json"), $$"""
                {
                  "defaultSettings": {
                    "wineVersion": { "type": "proton", "bin": "{{protonBin}}" },
                    "enviromentOptions": [
                      { "key": "DXVK_CONFIG", "value": "dxvk.hud = compiler" },
                      { "key": "VKD3D_CONFIG", "value": "dxr" }
                    ]
                  }
                }
                """);

            string bl4Id = "ea5f9a88243c48908d0205c245fddea0";
            string bl4Prefix = Path.Combine(tempRoot, "Prefixes", "Borderlands4");
            string bl4Sys32 = Path.Combine(bl4Prefix, "pfx", "drive_c", "windows", "system32");
            Directory.CreateDirectory(bl4Sys32);
            File.WriteAllText(Path.Combine(bl4Prefix, "config_info"), $"{Path.GetDirectoryName(protonBin)}/files/\n");
            // Leave a stale 120,000-byte low-latency d3d12core.dll in system32 to verify StageProtonRuntimeDlls prefers standard vkd3d-proton!
            File.WriteAllBytes(Path.Combine(bl4Sys32, "d3d12core.dll"), new byte[120_000]);

            File.WriteAllText(Path.Combine(gamesCfgDir, $"{bl4Id}.json"), $$"""
                {
                  "{{bl4Id}}": {
                    "wineVersion": { "type": "proton", "bin": "{{protonBin}}" },
                    "winePrefix": "{{bl4Prefix}}",
                    "wrapperOptions": [
                      { "exe": "game-performance", "args": "" }
                    ],
                    "enviromentOptions": [
                      { "key": "VKD3D_DISABLE_EXTENSIONS", "value": "VK_NV_raw_access_chains,VK_EXT_device_generated_commands" },
                      { "key": "VKD3D_CONFIG", "value": "nodxr,single_queue,vk_debug" },
                      { "key": "DXVK_CONFIG", "value": "" }
                    ]
                  }
                }
                """);

            var bl4 = new GameInfo(
                Id: bl4Id,
                Name: "Borderlands®4",
                Source: GameSource.Heroic,
                InstallPath: Path.Combine(tempRoot, "Venus"),
                ExecutablePath: Path.Combine(tempRoot, "Venus", "Borderlands4.exe"),
                PrefixPath: bl4Prefix,
                BannerPath: null,
                Engine: GameEngine.UnrealEngine5,
                State: CacheState.NeedsCompilation,
                TotalShadersEstimated: 100,
                CompiledShadersCount: 0,
                LastCompiledAt: null);

            Assert.IsFalse(ProtonRunner.IsLowLatencyConfigured(bl4, "PROTON_VKD3D_LOWLATENCY", home, xdgData, xdgConfig));
            Assert.IsFalse(ProtonRunner.IsLowLatencyConfigured(bl4, "PROTON_DXVK_LOWLATENCY", home, xdgData, xdgConfig));

            var bl4Env = ProtonRunner.GetEffectiveGameLauncherEnvironment(bl4, home, xdgData, xdgConfig);
            Assert.AreEqual("VK_NV_raw_access_chains,VK_EXT_device_generated_commands", bl4Env["VKD3D_DISABLE_EXTENSIONS"]);
            Assert.AreEqual("nodxr,single_queue,vk_debug", bl4Env["VKD3D_CONFIG"]);
            Assert.IsFalse(bl4Env.ContainsKey("DXVK_CONFIG"), "Per-game empty DXVK_CONFIG must clear defaultSettings DXVK_CONFIG.");

            string expectedBl4Stamp =
                $"{protonBin}|100000|80000|env=VKD3D_CONFIG=nodxr;VKD3D_DISABLE_EXTENSIONS=VK_EXT_device_generated_commands,VK_NV_raw_access_chains";
            Assert.AreEqual(expectedBl4Stamp, ProtonRunner.GetProtonStamp(bl4, home, xdgData, xdgConfig));

            string workDir = Path.Combine(tempRoot, "work_bl4");
            Directory.CreateDirectory(workDir);
            ProtonRunner.StageProtonRuntimeDlls(bl4, protonBin, bl4Prefix, bl4Prefix, workDir, bl4Env);
            Assert.AreEqual(100_000L, new FileInfo(Path.Combine(workDir, "d3d12core.dll")).Length,
                "StageProtonRuntimeDlls must stage standard vkd3d-proton d3d12core.dll (100,000 bytes), not stale gameSys32 low-latency DLL.");

            // A second Heroic game that explicitly wraps with bgmod MUST pick up global/bgmod.conf LOWLATENCY=1
            string bgGameId = "heroic_bgmod_game";
            File.WriteAllText(Path.Combine(gamesCfgDir, $"{bgGameId}.json"), $$"""
                {
                  "{{bgGameId}}": {
                    "wineVersion": { "type": "proton", "bin": "{{protonBin}}" },
                    "wrapperOptions": [
                      { "exe": "{{Path.Combine(globalBgDir, "bgmod")}}", "args": "--" }
                    ]
                  }
                }
                """);

            var bgGame = bl4 with { Id = bgGameId, Name = "Bgmod Heroic Game" };
            Assert.IsTrue(ProtonRunner.IsLowLatencyConfigured(bgGame, "PROTON_VKD3D_LOWLATENCY", home, xdgData, xdgConfig));
            Assert.IsTrue(ProtonRunner.IsLowLatencyConfigured(bgGame, "PROTON_DXVK_LOWLATENCY", home, xdgData, xdgConfig));
            StringAssert.StartsWith(ProtonRunner.GetProtonStamp(bgGame, home, xdgData, xdgConfig), $"{protonBin}|120000|90000|env=");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_SteamLocalConfigVdf_ResolvesBgmodWithSanitizedColonPath_InlineEnvOverrides_AndIgnoresGameArgsWithoutPercentCommand()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_steam_vdf_" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(tempRoot, "home");
        string xdgData = Path.Combine(home, ".local", "share");
        string xdgConfig = Path.Combine(home, ".config");
        try
        {
            string steamRoot = Path.Combine(xdgData, "Steam");
            string compatToolDir = Path.Combine(steamRoot, "compatibilitytools.d");
            Directory.CreateDirectory(compatToolDir);
            string protonBin = CreateFakeProtonWithDualLatencyDlls(compatToolDir, "proton-cachyos-slr");

            Directory.CreateDirectory(Path.Combine(steamRoot, "config"));
            File.WriteAllText(Path.Combine(steamRoot, "config", "config.vdf"), """
                "InstallConfigStore"
                {
                    "Software"
                    {
                        "Valve"
                        {
                            "Steam"
                            {
                                "CompatToolMapping"
                                {
                                    "0"
                                    {
                                        "name" "proton-cachyos-slr"
                                    }
                                }
                            }
                        }
                    }
                }
                """);

            // Global bgmod.conf AND per-game "Cronos_ The New Dawn/bgmod.conf" (colon replaced by underscore, matching Goverlay)
            string globalBgDir = Path.Combine(xdgData, "goverlay", "gameconfig", "global");
            string cronosBgDir = Path.Combine(xdgData, "goverlay", "gameconfig", "Cronos_ The New Dawn");
            Directory.CreateDirectory(globalBgDir);
            Directory.CreateDirectory(cronosBgDir);

            File.WriteAllText(Path.Combine(globalBgDir, "bgmod.conf"), """
                [Env]
                PROTON_VKD3D_LOWLATENCY=1
                PROTON_DXVK_LOWLATENCY=1
                """);
            File.WriteAllText(Path.Combine(cronosBgDir, "bgmod.conf"), """
                [Env]
                PROTON_VKD3D_LOWLATENCY=1
                PROTON_DXVK_LOWLATENCY=0
                VKD3D_CONFIG=dxr
                """);
            File.WriteAllText(Path.Combine(cronosBgDir, "bgmod"), "#!/bin/sh\n");

            string cronosBgBin = Path.Combine(cronosBgDir, "bgmod");
            string userCfgDir = Path.Combine(steamRoot, "userdata", "98765432", "config");
            Directory.CreateDirectory(userCfgDir);
            string localConfigPath = Path.Combine(userCfgDir, "localconfig.vdf");

            // Include root-level "apps" decoy block, escaped JSON quotes, and trailing escaped backslashes ("C:\\Games\\")
            File.WriteAllText(localConfigPath, $$"""
                "UserLocalConfigStore"
                {
                    "apps"
                    {
                        "2101960"
                        {
                            "LaunchOptions" "PROTON_VKD3D_LOWLATENCY=0 %command%"
                        }
                    }
                    "Software"
                    {
                        "Valve"
                        {
                            "Steam"
                            {
                                "LastInstallFolder" "C:\\Games\\"
                                "apps"
                                {
                                    "2101960"
                                    {
                                        "cloud"
                                        {
                                            "meta" "{\"version\":2,\"path\":\"C:\\\\save\\\\\"}"
                                        }
                                        "LaunchOptions" "\"{{cronosBgBin}}\" -- env VKD3D_CONFIG=\"no_upload_hvv\" gamemoderun %command% -dx12"
                                    }
                                    "292030"
                                    {
                                        "LaunchOptions" "PROTON_VKD3D_LOWLATENCY=1 %command%"
                                    }
                                    "1091500"
                                    {
                                        "LaunchOptions" "gamemoderun %command% VKD3D_CONFIG=force_static_cbv"
                                    }
                                    "570"
                                    {
                                        "LaunchOptions" "-novid PROTON_VKD3D_LOWLATENCY=1 VKD3D_CONFIG=nodxr"
                                    }
                                }
                            }
                        }
                    }
                }
                """);

            GameInfo MakeSteamGame(string appId, string name) => new(
                Id: appId,
                Name: name,
                Source: GameSource.Steam,
                InstallPath: Path.Combine(steamRoot, "steamapps", "common", name),
                ExecutablePath: Path.Combine(steamRoot, "steamapps", "common", name, "Game.exe"),
                PrefixPath: null,
                BannerPath: null,
                Engine: GameEngine.UnrealEngine5,
                State: CacheState.NeedsCompilation,
                TotalShadersEstimated: 10,
                CompiledShadersCount: 0,
                LastCompiledAt: null);

            var cronos = MakeSteamGame("2101960", "Cronos: The New Dawn");
            var witcher3 = MakeSteamGame("292030", "The Witcher 3: Wild Hunt");
            var cyberpunk = MakeSteamGame("1091500", "Cyberpunk 2077");
            var dota2 = MakeSteamGame("570", "Dota 2");

            // 1. Cronos uses "Cronos_ The New Dawn/bgmod.conf" (VKD3D_LOWLATENCY=1, DXVK_LOWLATENCY=0) AND inline VKD3D_CONFIG="no_upload_hvv" overrides bgmod.conf's dxr (no_upload_hvv is runtime-only, and low-latency is encoded in 120000|80000 so legacy pre-|env= stamps stay 100% compatible!)
            Assert.IsTrue(ProtonRunner.IsLowLatencyConfigured(cronos, "PROTON_VKD3D_LOWLATENCY", home, xdgData, xdgConfig));
            Assert.IsFalse(ProtonRunner.IsLowLatencyConfigured(cronos, "PROTON_DXVK_LOWLATENCY", home, xdgData, xdgConfig));
            var cronosEnv = ProtonRunner.GetEffectiveGameLauncherEnvironment(cronos, home, xdgData, xdgConfig);
            Assert.AreEqual("no_upload_hvv", cronosEnv["VKD3D_CONFIG"]);
            Assert.AreEqual($"{protonBin}|120000|80000", ProtonRunner.GetProtonStamp(cronos, home, xdgData, xdgConfig));

            // 2. Witcher 3 sets PROTON_VKD3D_LOWLATENCY=1 directly in Steam LaunchOptions without bgmod
            Assert.IsTrue(ProtonRunner.IsLowLatencyConfigured(witcher3, "PROTON_VKD3D_LOWLATENCY", home, xdgData, xdgConfig));
            Assert.IsFalse(ProtonRunner.IsLowLatencyConfigured(witcher3, "PROTON_DXVK_LOWLATENCY", home, xdgData, xdgConfig));
            Assert.AreEqual($"{protonBin}|120000|80000", ProtonRunner.GetProtonStamp(witcher3, home, xdgData, xdgConfig));

            // 3. Cyberpunk 2077 uses gamemoderun (NOT bgmod) and places VKD3D_CONFIG after %command% (game arg, not env var)
            Assert.IsFalse(ProtonRunner.IsLowLatencyConfigured(cyberpunk, "PROTON_VKD3D_LOWLATENCY", home, xdgData, xdgConfig));
            Assert.AreEqual($"{protonBin}|100000|80000", ProtonRunner.GetProtonStamp(cyberpunk, home, xdgData, xdgConfig));

            // 4. Dota 2 has no %command% -> Steam treats LaunchOptions purely as game arguments
            Assert.IsFalse(ProtonRunner.IsLowLatencyConfigured(dota2, "PROTON_VKD3D_LOWLATENCY", home, xdgData, xdgConfig));
            Assert.AreEqual($"{protonBin}|100000|80000", ProtonRunner.GetProtonStamp(dota2, home, xdgData, xdgConfig));

            // 5. Editing localconfig.vdf or bgmod.conf immediately updates GetLauncherProtonConfigSignature
            string sig1 = ProtonRunner.GetLauncherProtonConfigSignature(home, xdgData, xdgConfig);
            File.AppendAllText(localConfigPath, "\n// user edited LaunchOptions\n");
            string sig2 = ProtonRunner.GetLauncherProtonConfigSignature(home, xdgData, xdgConfig);
            Assert.AreNotEqual(sig1, sig2, "GetLauncherProtonConfigSignature must detect Steam localconfig.vdf edits.");

            File.AppendAllText(Path.Combine(cronosBgDir, "bgmod.conf"), "\nVKD3D_FEATURE_LEVEL=12_2\n");
            string sig3 = ProtonRunner.GetLauncherProtonConfigSignature(home, xdgData, xdgConfig);
            Assert.AreNotEqual(sig2, sig3, "GetLauncherProtonConfigSignature must detect Goverlay bgmod.conf edits.");
            Assert.AreEqual("12_2", ProtonRunner.GetEffectiveGameLauncherEnvironment(cronos, home, xdgData, xdgConfig)["VKD3D_FEATURE_LEVEL"]);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_LutrisPrefixCommandBgmod_AndRuntimeStampDetectsShaderCodegenEnvChanges()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_lutris_stamp_" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(tempRoot, "home");
        string xdgData = Path.Combine(home, ".local", "share");
        string xdgConfig = Path.Combine(home, ".config");
        try
        {
            string protonBin = CreateFakeProtonWithDualLatencyDlls(tempRoot);

            string globalBgDir = Path.Combine(xdgData, "goverlay", "gameconfig", "global");
            Directory.CreateDirectory(globalBgDir);
            File.WriteAllText(Path.Combine(globalBgDir, "bgmod.conf"), """
                [Env]
                PROTON_VKD3D_LOWLATENCY=1
                VKD3D_CONFIG=dxr
                """);
            File.WriteAllText(Path.Combine(globalBgDir, "bgmod"), "#!/bin/sh\n");

            string lutrisData = Path.Combine(xdgData, "lutris");
            string lutrisCfg = Path.Combine(xdgConfig, "lutris");
            Directory.CreateDirectory(Path.Combine(lutrisData, "runners", "proton"));
            Directory.CreateDirectory(Path.Combine(lutrisCfg, "games"));

            // Create minimal Lutris pga.db with row id=42 -> configpath="test-game-42"
            string dbPath = Path.Combine(lutrisData, "pga.db");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE games (id INTEGER PRIMARY KEY, name TEXT, slug TEXT, runner TEXT, configpath TEXT, directory TEXT, installed INTEGER);
                    INSERT INTO games (id, name, slug, runner, configpath, directory, installed)
                    VALUES (42, 'Lutris Test Game', 'lutris-test-game', 'wine', 'test-game-42', '/tmp/game42', 1);
                    """;
                cmd.ExecuteNonQuery();
            }

            string gameYml = Path.Combine(lutrisCfg, "games", "test-game-42.yml");
            File.WriteAllText(gameYml, $$"""
                wine:
                  version: custom
                  custom_wine_path: {{protonBin}}
                system:
                  prefix_command: ~/.local/share/goverlay/gameconfig/global/bgmod
                  env:
                    VKD3D_CONFIG: nodxr
                """);

            string prefixDir = Path.Combine(tempRoot, "lutris_pfx");
            Directory.CreateDirectory(prefixDir);

            var lutrisGame = new GameInfo(
                Id: "42",
                Name: "Lutris Test Game",
                Source: GameSource.Lutris,
                InstallPath: Path.Combine(tempRoot, "game42"),
                ExecutablePath: Path.Combine(tempRoot, "game42", "Game42.exe"),
                PrefixPath: prefixDir,
                BannerPath: null,
                Engine: GameEngine.UnrealEngine5,
                State: CacheState.Cached,
                TotalShadersEstimated: 50,
                CompiledShadersCount: 50,
                LastCompiledAt: DateTime.UtcNow);

            var parsedCfg = LutrisLinuxSource.TryReadGameRunnerConfig("42", false, home, xdgData, xdgConfig);
            Assert.IsNotNull(parsedCfg);
            Assert.AreEqual("~/.local/share/goverlay/gameconfig/global/bgmod", parsedCfg!.PrefixCommand);

            // bgmod enables PROTON_VKD3D_LOWLATENCY=1, while Lutris system.env overrides VKD3D_CONFIG to nodxr
            Assert.IsTrue(ProtonRunner.IsLowLatencyConfigured(lutrisGame, "PROTON_VKD3D_LOWLATENCY", home, xdgData, xdgConfig));
            var env1 = ProtonRunner.GetEffectiveGameLauncherEnvironment(lutrisGame, home, xdgData, xdgConfig);
            Assert.AreEqual("nodxr", env1["VKD3D_CONFIG"]);

            string cachePath = ProtonRunner.GetNonSteamShaderCachePath(lutrisGame.Id, prefixDir);
            string vkd3dCache = Path.Combine(cachePath, "VKD3D_shader_cache");
            string glCache = Path.Combine(cachePath, "nvidiav1", "GLCache");
            string workDir = Path.Combine(tempRoot, "work42");
            Directory.CreateDirectory(vkd3dCache);
            Directory.CreateDirectory(glCache);
            Directory.CreateDirectory(workDir);

            ProtonRunner.StageProtonRuntimeDlls(lutrisGame, protonBin, prefixDir, prefixDir, workDir, env1);
            string suffix1 = ProtonRunner.BuildShaderCodegenEnvStampSuffix(env1);
            Assert.AreEqual("|env=VKD3D_CONFIG=nodxr", suffix1);

            ProtonRunner.PrepareDriverCachesForWarmer(
                cachePath,
                vkd3dCache,
                glCache,
                protonBin,
                workDir,
                isIncrementalRun: false,
                shaderCodegenEnvSuffix: suffix1);

            // Runtime stamp matches current Lutris env + bgmod low-latency DLL (120000)
            Assert.IsNull(ProtonRunner.GetRuntimeCacheStampMismatchReason(lutrisGame, home, xdgData, xdgConfig));

            // User adds runtime-only single_queue, log_memory_budget, and FPS cap -> cache MUST remain valid (null mismatch)!
            File.WriteAllText(gameYml, $$"""
                wine:
                  version: custom
                  custom_wine_path: {{protonBin}}
                system:
                  prefix_command: ~/.local/share/goverlay/gameconfig/global/bgmod
                  env:
                    VKD3D_CONFIG: single_queue,nodxr,log_memory_budget,no_upload_hvv
                    DXVK_CONFIG: dxgi.maxFrameRate=120;dxvk.hud=fps;dxvk.numCompilerThreads=8
                    RADV_DEBUG: info,spirv,hang
                    PROTON_ENABLE_NVAPI: 1
                """);
            Assert.IsNull(ProtonRunner.GetRuntimeCacheStampMismatchReason(lutrisGame, home, xdgData, xdgConfig));

            // User adds VKD3D_DISABLE_EXTENSIONS in Lutris -> GetRuntimeCacheStampMismatchReason immediately flags "launch options changed"
            File.WriteAllText(gameYml, $$"""
                wine:
                  version: custom
                  custom_wine_path: {{protonBin}}
                system:
                  prefix_command: ~/.local/share/goverlay/gameconfig/global/bgmod
                  env:
                    VKD3D_CONFIG: nodxr
                    VKD3D_DISABLE_EXTENSIONS: VK_NV_raw_access_chains
                """);

            Assert.AreEqual("launch options changed", ProtonRunner.GetRuntimeCacheStampMismatchReason(lutrisGame, home, xdgData, xdgConfig));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_SmartLaunchOptionsClassifier_DistinguishesProtonUpdate_FromLaunchOptionsChanged_AndRetroactivelyIgnoresRuntimeFlags()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "shaderless_smart_cls_" + Guid.NewGuid().ToString("N"));
        try
        {
            string protonA = CreateFakeProtonWithDualLatencyDlls(tempRoot);
            const string protonB = "/home/user/.local/share/Steam/compatibilitytools.d/GE-Proton11-12/proton";

            // 1. Legacy stamp saved with runtime-only single_queue + unsorted extensions matches clean normalized stamp -> NOT stale
            string savedLegacy =
                $"{protonA}|100000|80000|env=VKD3D_CONFIG=nodxr,single_queue;VKD3D_DISABLE_EXTENSIONS=VK_NV_raw_access_chains,VK_EXT_device_generated_commands";
            string currentReordered =
                $"{protonA}|100000|80000|env=VKD3D_CONFIG=nodxr;VKD3D_DISABLE_EXTENSIONS=VK_EXT_device_generated_commands,VK_NV_raw_access_chains";

            Assert.IsNull(GameManager.EvaluateStaleReason("615.78.08", savedLegacy, "build1", "615.78.08", currentReordered, "build1"));
            Assert.IsTrue(ProtonRunner.AreDriverAndProtonStampsEquivalent($"615.78.08|{savedLegacy}", $"615.78.08|{currentReordered}"));

            // 2. Removing VK_EXT_device_generated_commands (same Proton binary) -> "launch options changed"
            string currentExtChanged =
                $"{protonA}|100000|80000|env=VKD3D_CONFIG=nodxr;VKD3D_DISABLE_EXTENSIONS=VK_NV_raw_access_chains";
            Assert.AreEqual(
                "launch options changed",
                GameManager.EvaluateStaleReason("615.78.08", savedLegacy, "build1", "615.78.08", currentExtChanged, "build1"));
            Assert.IsFalse(ProtonRunner.AreDriverAndProtonStampsEquivalent($"615.78.08|{savedLegacy}", $"615.78.08|{currentExtChanged}"));
            Assert.IsFalse(Shaderless.Core.Pipelines.ShaderCompilerCoordinator.ShouldPreserveExistingCache(
                false,
                "launch options changed",
                1_000_000_000L,
                25_000_000_000L));

            // 3. Pure Goverlay Low-Latency DLL variant toggle on same Proton (100000|80000 <-> 120000|90000, no |env= needed) -> "launch options changed"
            string stdStamp = $"{protonA}|100000|80000";
            string pureLowLatStamp = $"{protonA}|120000|90000";
            Assert.AreEqual(
                "launch options changed",
                GameManager.EvaluateStaleReason("615.78.08", stdStamp, "build1", "615.78.08", pureLowLatStamp, "build1"));
            Assert.AreEqual(
                "launch options changed",
                ProtonRunner.CompareRuntimeCacheStamps($"{protonA}|100000|615.78.08", $"{protonA}|120000|615.78.08"));

            // 4. STEAM_COMPAT_CONFIG + DXVK_CONFIG + RADV_PERFTEST + PROTON_VKD3D_HEAP canonicalization
            var sarekEnv = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["STEAM_COMPAT_CONFIG"] = "vkd3dlowlatency,dxvksarek,vkd3dheap",
                ["DXVK_CONFIG"] = "dxgi.maxFrameRate = 144 ; d3d11.invariantPosition = True",
                ["RADV_PERFTEST"] = "aco, nggc, cswave32, transfer_queue"
            };
            Assert.IsTrue(ProtonRunner.IsVkd3dLowLatencyEnabled(sarekEnv));
            Assert.IsTrue(ProtonRunner.IsDxvkSarekEnabled(sarekEnv));
            string sarekSuffix = ProtonRunner.BuildShaderCodegenEnvStampSuffix(sarekEnv);
            Assert.AreEqual(
                "|env=DXVK_CONFIG=d3d11.invariantposition=True;PROTON_VKD3D_HEAP=1;RADV_PERFTEST=cswave32,nggc",
                sarekSuffix);
            // Default/no-op RADV_PERFTEST=aco (e.g. from legacy wrapper scripts) must never mark existing stamps Stale
            Assert.IsNull(
                GameManager.EvaluateStaleReason("615.78.08", stdStamp, "build1", "615.78.08", $"{stdStamp}|env=RADV_PERFTEST=aco", "build1"));
            Assert.IsNull(
                GameManager.EvaluateStaleReason("615.78.08", $"{stdStamp}|env=RADV_PERFTEST=aco", "build1", "615.78.08", stdStamp, "build1"));
            Assert.AreEqual(
                "launch options changed",
                GameManager.EvaluateStaleReason("615.78.08", stdStamp, "build1", "615.78.08", $"{protonA}|120000|90000{sarekSuffix}", "build1"));

            // 5. Switching Proton build OR updating Proton DLLs in-place (999999 not in any variant folder) -> "Proton updated"
            string switchedProton = $"{protonB}|100000|80000";
            string updatedDllsInPlace = $"{protonA}|999999|80000";
            Assert.AreEqual(
                "Proton updated",
                GameManager.EvaluateStaleReason("615.78.08", stdStamp, "build1", "615.78.08", switchedProton, "build1"));
            Assert.AreEqual(
                "Proton updated",
                GameManager.EvaluateStaleReason("615.78.08", stdStamp, "build1", "615.78.08", updatedDllsInPlace, "build1"));
            Assert.AreEqual(
                "Proton updated",
                ProtonRunner.CompareRuntimeCacheStamps($"{protonA}|999999|615.78.08", $"{protonA}|100000|615.78.08"));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_UpdateChecker_SemVerAndReleasesJson_HandlesPrereleasesDraftsAndDismissedTags()
    {
        Assert.IsGreaterThan(0, Shaderless.Core.UpdateChecker.CompareSemVer("0.3.0", "0.3.0-beta"));
        Assert.IsGreaterThan(0, Shaderless.Core.UpdateChecker.CompareSemVer("v0.3.1-beta", "0.3.0"));
        Assert.IsGreaterThan(0, Shaderless.Core.UpdateChecker.CompareSemVer("0.3.0-beta.2", "0.3.0-beta.1"));
        Assert.IsGreaterThan(0, Shaderless.Core.UpdateChecker.CompareSemVer("0.3.0-rc1", "0.3.0-beta2"));
        Assert.IsGreaterThan(0, Shaderless.Core.UpdateChecker.CompareSemVer("0.10.0", "0.9.0"));
        Assert.AreEqual(0, Shaderless.Core.UpdateChecker.CompareSemVer("Shaderless-v0.3.0-beta+abc123", "0.3.0-beta"));

        const string releasesJson = """
            [
              {
                "tag_name": "v0.4.0-beta",
                "html_url": "https://github.com/cyberps96/Shaderless/releases/tag/v0.4.0-beta",
                "draft": true,
                "prerelease": true
              },
              {
                "tag_name": "v0.3.9-beta",
                "html_url": "https://evil.example.com/Shaderless/releases/tag/v0.3.9-beta",
                "draft": false,
                "prerelease": true
              },
              {
                "tag_name": "v0.3.2-beta",
                "html_url": "https://github.com/cyberps96/Shaderless/releases/tag/v0.3.2-beta",
                "draft": false,
                "prerelease": true,
                "published_at": "2026-11-14T10:00:00Z"
              },
              {
                "tag_name": "v0.3.1-beta",
                "html_url": "https://github.com/cyberps96/Shaderless/releases/tag/v0.3.1-beta",
                "draft": false,
                "prerelease": true
              },
              {
                "tag_name": "v0.2.3-beta",
                "html_url": "https://github.com/cyberps96/Shaderless/releases/tag/v0.2.3-beta",
                "draft": false,
                "prerelease": true
              }
            ]
            """;

        // Picks v0.3.2-beta (skips draft v0.4.0-beta and non-GitHub URL v0.3.9-beta)
        var upd = Shaderless.Core.UpdateChecker.EvaluateReleasesJson(releasesJson, "0.3.0-beta");
        Assert.IsNotNull(upd);
        Assert.AreEqual("v0.3.2-beta", upd!.LatestTag);
        Assert.AreEqual("0.3.2-beta", upd.LatestVersion);
        Assert.IsTrue(upd.IsPrerelease);

        // Dismissed tag suppresses notification for that exact version (with or without leading 'v')
        Assert.IsNull(Shaderless.Core.UpdateChecker.EvaluateReleasesJson(releasesJson, "0.3.0-beta", dismissedTag: "v0.3.2-beta"));
        Assert.IsNull(Shaderless.Core.UpdateChecker.EvaluateReleasesJson(releasesJson, "0.3.0-beta", dismissedTag: "0.3.2-beta"));

        // Stable current build ignores pre-releases, notifies only on newer stable release
        Assert.IsNull(Shaderless.Core.UpdateChecker.EvaluateReleasesJson(releasesJson, "0.3.0"));
        const string withStableJson = """
            [
              {
                "tag_name": "v0.4.0-beta",
                "html_url": "https://github.com/cyberps96/Shaderless/releases/tag/v0.4.0-beta",
                "draft": false,
                "prerelease": true
              },
              {
                "tag_name": "v0.3.1",
                "html_url": "https://github.com/cyberps96/Shaderless/releases/tag/v0.3.1",
                "draft": false,
                "prerelease": false
              }
            ]
            """;
        var stableUpd = Shaderless.Core.UpdateChecker.EvaluateReleasesJson(withStableJson, "0.3.0");
        Assert.IsNotNull(stableUpd);
        Assert.AreEqual("v0.3.1", stableUpd!.LatestTag);
        Assert.IsFalse(stableUpd.IsPrerelease);
    }

    [TestMethod]
    public void Test_CrashLogger_WritesDiagnostics_DeduplicatesSameException_AndRotatesOver1Mb()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "shaderless_crashlog_" + Guid.NewGuid().ToString("N"));
        try
        {
            var initial = Shaderless.Core.CrashLogger.GetStatus(tempDir);
            Assert.IsFalse(initial.Exists);
            Assert.IsNull(Shaderless.Core.CrashLogger.ReadRecentTail(dataDirOverride: tempDir));

            Shaderless.Core.CrashLogger.SetActiveDriverSummary("Test GPU • TestDriver (v999.0)");
            var ex = new InvalidOperationException("Simulated test crash");
            Shaderless.Core.CrashLogger.LogException(ex, "UnitTestContext", dataDirOverride: tempDir);
            // Re-logging the exact same exception reference must be deduplicated
            Shaderless.Core.CrashLogger.LogException(ex, "UnitTestContextDuplicate", dataDirOverride: tempDir);

            var after = Shaderless.Core.CrashLogger.GetStatus(tempDir);
            Assert.IsTrue(after.Exists);
            Assert.IsGreaterThan(0L, after.SizeBytes);

            string? tail = Shaderless.Core.CrashLogger.ReadRecentTail(dataDirOverride: tempDir);
            Assert.IsNotNull(tail);
            StringAssert.Contains(tail!, "Simulated test crash");
            StringAssert.Contains(tail!, "UnitTestContext");
            Assert.DoesNotContain("UnitTestContextDuplicate", tail!);

            // Write > 1 MB and confirm next log rotates crash.log -> crash.old.log
            string crashPath = Path.Combine(tempDir, Shaderless.Core.CrashLogger.FileName);
            string oldCrashPath = Path.Combine(tempDir, Shaderless.Core.CrashLogger.OldFileName);
            File.WriteAllText(crashPath, new string('X', (int)Shaderless.Core.CrashLogger.MaxLogBytes + 2048));

            Shaderless.Core.CrashLogger.LogError("Post-rotation error entry", "RotationTest", dataDirOverride: tempDir);
            Assert.IsTrue(File.Exists(oldCrashPath), "Oversized crash.log must rotate to crash.old.log.");
            Assert.IsGreaterThan(Shaderless.Core.CrashLogger.MaxLogBytes, new FileInfo(oldCrashPath).Length);
            Assert.IsLessThan(16384L, new FileInfo(crashPath).Length);
            StringAssert.Contains(File.ReadAllText(crashPath), "Post-rotation error entry");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Test_SettingsManager_PersistsWelcomeAndDismissedUpdateTag_WithoutClobberingAutomationSettings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "shaderless_settings_b7_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string settingsPath = Path.Combine(tempDir, "settings.json");

            Shaderless.Core.SettingsManager.Save(
                new Shaderless.Core.Models.AppSettings(
                    AutoRecordAllGames: true,
                    AutoCompileOnExit: true,
                    DisabledAutoRecordGameIds: new[] { "12345" },
                    MaxCacheSizeGb: 50),
                settingsPath);

            Assert.IsFalse(Shaderless.Core.SettingsManager.Load(settingsPath).HasSeenWelcome);

            Shaderless.Core.SettingsManager.MarkWelcomeSeen(settingsPath);
            Shaderless.Core.SettingsManager.SetDismissedUpdateTag("v0.3.1-beta", settingsPath);

            var reloaded = Shaderless.Core.SettingsManager.Load(settingsPath);
            Assert.IsTrue(reloaded.HasSeenWelcome);
            Assert.AreEqual("v0.3.1-beta", reloaded.DismissedUpdateTag);
            Assert.IsTrue(reloaded.AutoRecordAllGames);
            Assert.IsTrue(reloaded.AutoCompileOnExit);
            Assert.AreEqual(50, reloaded.EffectiveMaxCacheSizeGb);
            Assert.IsTrue(reloaded.IsAutoRecordDisabledForGame("12345"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

