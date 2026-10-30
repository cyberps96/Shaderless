using SCSKiller.Linux.Core.Models;
using SCSKiller.Linux.Core.Pipelines;

namespace SCSKiller.Linux.Core.Games;

public static class GameEngineDetector
{
    private static readonly string[] IgnoredExeSubstrings =
    {
        "CrashReport",
        "UnityCrashHandler",
        "UnityBugReporter",
        "DXSETUP",
        "vcredist",
        "vc_redist",
        "EasyAntiCheat",
        "BEService",
        "BattlEye",
        "EpicWebHelper",
        "EpicOnlineServices",
        "EOSBootstrapper",
        "UnrealCEFSubProcess",
        "cef_browser_process",
        "cef_subprocess",
        "cef_helper",
        "QtWebEngineProcess",
        "msedgewebview2",
        "WebView2",
        "D3D12StateObjectCompiler",
        "d3dconfig",
        "7za",
        "7z.exe",
        "8za",
        "crashpad",
        "CheckEnv",
        "UEPrereq",
        "REDprelauncher",
        "ErrorReporter",
        "InstallerMessage",
        "WinMTR-Report",
        "minidump-stackwalk",
        "crs-handler",
        "unins000",
        "uninstall",
        "cleanup",
        "benchmark",
        ".original.exe"
    };

    private static readonly HashSet<string> BinaryFolderExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "binaries",
        "shipping",
        "release",
        "retail",
        "final",
        "amd64"
    };

    private static readonly string[] BinaryFolderPrefixes =
    {
        "win64",
        "win32",
        "win_",
        "x64",
        "x86"
    };

    private static readonly HashSet<string> BoundaryFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "home",
        "usr",
        "opt",
        "var",
        "mnt",
        "media",
        "run",
        "tmp",
        "common",
        "steamapps",
        "drive_c",
        "Program Files",
        "Program Files (x86)",
        "Games",
        "Heroic"
    };

    /// <summary>
    /// Infers the true root directory of a game installation from an executable path (and optional Heroic/launcher folder hint).
    /// Handles Unreal (&lt;Root&gt;/&lt;Project&gt;/Binaries/Win64), CryEngine (&lt;Root&gt;/Bin/Win64SharedMasterSteamPGO),
    /// REDengine (&lt;Root&gt;/bin/x64), Dagor (&lt;Root&gt;/win64), etc.
    /// </summary>
    public static string InferInstallRoot(string exePath, string? hintFolder = null)
    {
        string exeDir = Path.GetDirectoryName(exePath) ?? exePath;
        string inferred = WalkUpFromBinarySubfolders(exeDir);

        if (!string.IsNullOrWhiteSpace(hintFolder) && Directory.Exists(hintFolder) && !Directory.Exists(Path.Combine(hintFolder, "drive_c")))
        {
            try
            {
                string fullHint = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hintFolder));
                string fullExe = Path.GetFullPath(exePath);
                if (!IsBoundaryDirectory(new DirectoryInfo(fullHint)) &&
                    fullExe.StartsWith(fullHint + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    string walkedHint = Path.TrimEndingDirectorySeparator(WalkUpFromBinarySubfolders(fullHint));
                    string fullInferred = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inferred));
                    if (!IsBoundaryDirectory(new DirectoryInfo(walkedHint)) &&
                        (string.Equals(fullInferred, walkedHint, StringComparison.Ordinal) ||
                         fullInferred.StartsWith(walkedHint + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    {
                        return walkedHint;
                    }
                }
            }
            catch { }
        }

        return inferred;
    }

    private static string WalkUpFromBinarySubfolders(string startDir)
    {
        // 1. Unreal Engine layout: <Root>/<Project>/Binaries/<Platform>/Game.exe
        for (var dir = new DirectoryInfo(startDir); dir?.Parent != null; dir = dir.Parent)
        {
            if (!dir.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase)) continue;
            var project = dir.Parent;
            var root = project.Parent;
            if (root != null && Directory.Exists(Path.Combine(root.FullName, "Engine")))
                return root.FullName;
            return project.FullName;
        }

        // 1b. Non-standard Unreal store sub-folders: e.g. <Root>/platform/steam/PBZ.exe or <Root>/steam/PBZ.exe where <Root>/Engine exists
        var storeCheck = new DirectoryInfo(startDir);
        if (storeCheck.Parent != null &&
            storeCheck.Name is "steam" or "epic" or "gog" or "win64" or "Steam" or "Epic" or "GOG")
        {
            if (storeCheck.Parent.Name.Equals("platform", StringComparison.OrdinalIgnoreCase) &&
                storeCheck.Parent.Parent != null &&
                !IsBoundaryDirectory(storeCheck.Parent.Parent) &&
                Directory.Exists(Path.Combine(storeCheck.Parent.Parent.FullName, "Engine")))
            {
                return storeCheck.Parent.Parent.FullName;
            }

            if (!IsBoundaryDirectory(storeCheck.Parent) &&
                Directory.Exists(Path.Combine(storeCheck.Parent.FullName, "Engine")))
            {
                return storeCheck.Parent.FullName;
            }
        }

        // 2. Multi-level binary sub-folder layouts: e.g. Bin/Win64SharedMasterSteamPGO, bin/x64_dx12, win64, x64/Release
        var current = new DirectoryInfo(startDir);
        for (int step = 0; step < 3 && current.Parent != null; step++)
        {
            if (!IsBinarySubfolderName(current.Name))
                break;
            if (IsBoundaryDirectory(current.Parent))
                break;
            current = current.Parent;
        }

        return current.FullName;
    }

    private static bool IsBinarySubfolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (BinaryFolderExactNames.Contains(name)) return true;
        foreach (var prefix in BinaryFolderPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsBoundaryDirectory(DirectoryInfo dir)
    {
        if (dir.Parent == null) return true;
        string full = Path.TrimEndingDirectorySeparator(dir.FullName);
        string home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!string.IsNullOrEmpty(home) && string.Equals(full, home, StringComparison.Ordinal))
            return true;
        if (Directory.Exists(Path.Combine(full, "drive_c")))
            return true;
        return BoundaryFolderNames.Contains(dir.Name);
    }

    internal static bool IsNonUnrealPak(string filePath)
    {
        string norm = filePath.Replace('\\', '/');
        if (norm.Contains("/locales/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/cef/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/cef64/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/cef32/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/chromium/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/webview/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/webui/", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains("/electron/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string fileName = Path.GetFileName(norm);
        if (fileName.Equals("resources.pak", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("content_resources.pak", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("devtools_resources.pak", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("blink_resources.pak", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("snapshot_blob.pak", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("chrome_", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("cef", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("ui_resources", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("views_resources", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("headless_lib_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Locale pak naming pattern (e.g. "en-US.pak", "zh-CN.pak", "es-419.pak")
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length is >= 2 and <= 6 && stem.Contains('-') && stem.All(c => char.IsLetterOrDigit(c) || c == '-'))
        {
            int dash = stem.IndexOf('-');
            if (dash is 2 or 3)
                return true;
        }

        return false;
    }

    public static (GameEngine Engine, int EstimatedShaders) Detect(string installPath)
    {
        if (!Directory.Exists(installPath))
        {
            return (GameEngine.Unknown, 0);
        }

        try
        {
            // 1. REDengine (Cyberpunk 2077 / The Witcher 3)
            if (File.Exists(Path.Combine(installPath, "bin", "x64", "Cyberpunk2077.exe")) ||
                File.Exists(Path.Combine(installPath, "r6", "config", "settings.json")))
            {
                return (GameEngine.RedEngine, 12000);
            }
            if (File.Exists(Path.Combine(installPath, "bin", "x64", "witcher3.exe")) ||
                File.Exists(Path.Combine(installPath, "bin", "x64_dx12", "witcher3.exe")) ||
                File.Exists(Path.Combine(installPath, "content", "content0", "shaderdx12_0.cache")))
            {
                return (GameEngine.RedEngine, 8500);
            }

            // 2. FromSoftware (Elden Ring, Nightreign, Dark Souls III, Dark Souls Remastered)
            if (File.Exists(Path.Combine(installPath, "Game", "eldenring.exe")) ||
                File.Exists(Path.Combine(installPath, "eldenring.exe")) ||
                File.Exists(Path.Combine(installPath, "Game", "nightreign.exe")) ||
                File.Exists(Path.Combine(installPath, "nightreign.exe")) ||
                File.Exists(Path.Combine(installPath, "Game", "DarkSoulsIII.exe")) ||
                File.Exists(Path.Combine(installPath, "DarkSoulsRemastered.exe")))
            {
                return (GameEngine.FromSoftware, 9500);
            }

            // 3. Capcom RE Engine
            if (Directory.EnumerateFiles(installPath, "re_chunk_*.pak", SearchOption.TopDirectoryOnly).Any())
            {
                return (GameEngine.ReEngine, 7200);
            }

            // 4. Gaijin Dagor Engine, Remedy Northlight, & Eidos Dawn Engine
            if (Directory.Exists(Path.Combine(installPath, "compiledShaders")) ||
                Directory.EnumerateFiles(installPath, "*.vromfs.bin", SearchOption.TopDirectoryOnly).Any())
            {
                return (GameEngine.Dagor, 8500);
            }
            if (Directory.Exists(Path.Combine(installPath, "data", "shaders", "build", "pc_dxil")))
            {
                return (GameEngine.Northlight, 8500);
            }
            if (File.Exists(Path.Combine(installPath, "bin", "rawpso.store2")))
            {
                return (GameEngine.DirectX12, 8500);
            }

            // 5. CryEngine (Kingdom Come: Deliverance I/II, Crysis, Hunt, Prey) — prevent Engine/Engine.pak from matching UE4
            bool isCryEngine =
                File.Exists(Path.Combine(installPath, "Engine", "Engine.pak")) ||
                File.Exists(Path.Combine(installPath, "Engine", "ShaderCache.pak")) ||
                File.Exists(Path.Combine(installPath, "Engine", "ShadersBin.pak")) ||
                Directory.EnumerateFiles(installPath, "CryRenderD3D*.dll", SearchOption.AllDirectories).Any() ||
                Directory.EnumerateFiles(installPath, "WHGame.dll", SearchOption.AllDirectories).Any();
            if (isCryEngine)
            {
                bool hasCryDx12 =
                    Directory.EnumerateFiles(installPath, "*d3d12*.dll", SearchOption.AllDirectories).Any() ||
                    Directory.EnumerateFiles(installPath, "*dx12*.dll", SearchOption.AllDirectories).Any() ||
                    Directory.EnumerateFiles(installPath, "nvngx_dlss*.dll", SearchOption.AllDirectories).Any();
                return hasCryDx12
                    ? (GameEngine.DirectX12, 6000)
                    : (GameEngine.DirectX11, 3500);
            }

            // 6. Unreal Engine 4 & 5
            bool hasUnrealEngineDir =
                Directory.Exists(Path.Combine(installPath, "Engine", "Binaries")) ||
                Directory.Exists(Path.Combine(installPath, "Engine", "Content"));
            var ucasFiles = Directory.EnumerateFiles(installPath, "*.ucas", SearchOption.AllDirectories).Take(5).ToList();
            var pakFiles = Directory.EnumerateFiles(installPath, "*.pak", SearchOption.AllDirectories)
                .Where(f => !IsNonUnrealPak(f))
                .Take(5)
                .ToList();

            if (ucasFiles.Count > 0)
            {
                return (GameEngine.UnrealEngine5, 14000);
            }
            if (pakFiles.Count > 0 || hasUnrealEngineDir)
            {
                return (GameEngine.UnrealEngine4, 8000);
            }

            // 7. Unity
            var dataDirs = Directory.EnumerateDirectories(installPath, "*_Data", SearchOption.TopDirectoryOnly).ToList();
            if (dataDirs.Count > 0 || File.Exists(Path.Combine(installPath, "UnityPlayer.dll")))
            {
                return (GameEngine.Unity, 4500);
            }

            // 8. Generic DirectX 12
            var dx12Files = Directory.EnumerateFiles(installPath, "*d3d12*.dll", SearchOption.AllDirectories).Take(1).ToList();
            if (dx12Files.Count > 0)
            {
                return (GameEngine.DirectX12, 6000);
            }
        }
        catch
        {
            // Ignore access errors on individual directories
        }

        return (GameEngine.DirectX11, 3500);
    }

    public static string? FindExecutable(string installPath)
    {
        if (!Directory.Exists(installPath)) return null;

        try
        {
            // 1. Unreal Shipping executable check via SCSKiller.Core
            try
            {
                string? unrealShipping = SCSKiller.Core.Games.GameFiles.ShippingExe(installPath);
                if (!string.IsNullOrEmpty(unrealShipping) && File.Exists(unrealShipping))
                    return unrealShipping;
            }
            catch { }

            var exeFiles = Directory.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string norm = f.Replace('\\', '/');
                    return !IgnoredExeSubstrings.Any(ig => f.Contains(ig, StringComparison.OrdinalIgnoreCase)) &&
                           !norm.Contains("/Engine/Binaries/", StringComparison.OrdinalIgnoreCase) &&
                           !norm.Contains("/crashreporter/", StringComparison.OrdinalIgnoreCase) &&
                           !norm.Contains("/cef/", StringComparison.OrdinalIgnoreCase) &&
                           !norm.Contains("/cef64/", StringComparison.OrdinalIgnoreCase) &&
                           !norm.Contains("/cef32/", StringComparison.OrdinalIgnoreCase) &&
                           !norm.Contains("/__Installer/", StringComparison.OrdinalIgnoreCase) &&
                           !norm.Contains("/PatchData/", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (exeFiles.Count == 0) return null;

            // Deprioritize Launcher / Wargaming Game Center stubs when real game executables are also present
            static bool IsLauncherStub(string path)
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                return stem.EndsWith("Launcher", StringComparison.OrdinalIgnoreCase) ||
                       stem.Equals("wgc", StringComparison.OrdinalIgnoreCase) ||
                       stem.StartsWith("wgc_", StringComparison.OrdinalIgnoreCase);
            }

            var nonLaunchers = exeFiles.Where(f => !IsLauncherStub(f)).ToList();
            var pool = nonLaunchers.Count > 0 ? nonLaunchers : exeFiles;

            // 2. Unity <Game>.exe paired with <Game>_Data folder
            string? unityExe = pool.FirstOrDefault(f => Directory.Exists(Path.ChangeExtension(f, null) + "_Data"));
            if (unityExe != null)
                return unityExe;

            static long SafeFileLength(string path)
            {
                try { return new FileInfo(path).Length; }
                catch { return 0; }
            }

            var largestOverall = pool.OrderByDescending(SafeFileLength).First();
            long largestBytes = SafeFileLength(largestOverall);

            var shippingExe = pool
                .Where(f => f.Contains("Shipping", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(SafeFileLength)
                .FirstOrDefault();

            var x64Exe = pool
                .Where(f => f.Contains("Win64", StringComparison.OrdinalIgnoreCase) ||
                            f.Contains("x64_dx12", StringComparison.OrdinalIgnoreCase) ||
                            f.Contains("x64", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(SafeFileLength)
                .FirstOrDefault();

            // Prevent small utilities left inside Binaries/Win64 from beating a much larger real game executable
            // (e.g. Phantom Blade Zero's 488 MB platform/steam/PBZ.exe vs 5 MB Binaries/Win64 helpers).
            if (x64Exe != null && largestBytes > 0 && SafeFileLength(x64Exe) < largestBytes / 4)
            {
                x64Exe = null;
            }

            string candidate = shippingExe
                ?? x64Exe
                ?? largestOverall;

            try
            {
                string resolved = SCSKiller.Core.Games.GameFiles.GameExe(installPath, candidate);
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                    return resolved;
            }
            catch { }

            return candidate;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Resolves the actual game process executable (upgrading small bootstrapper stubs or misconfigured helper exes
    /// to either the Unreal Shipping exe or a substantially larger main executable in <paramref name="installPath"/>).</summary>
    public static string? ResolveRealGameExecutable(string installPath, string? configuredExe)
    {
        string? exe = configuredExe;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return FindExecutable(installPath) ?? configuredExe;

        try
        {
            string resolved = SCSKiller.Core.Games.GameFiles.GameExe(installPath, exe);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                exe = resolved;
        }
        catch { }

        if (exe.Contains("Shipping", StringComparison.OrdinalIgnoreCase))
            return exe;

        try
        {
            string fileName = Path.GetFileName(exe);
            bool isIgnoredHelper = IgnoredExeSubstrings.Any(ig => fileName.Contains(ig, StringComparison.OrdinalIgnoreCase));
            long currentLen = new FileInfo(exe).Length;

            // If the configured exe is a known helper or a small (< 10 MB) bootstrapper stub (like PBZ's 1.1 MB steam/PBZ.exe),
            // check if FindExecutable locates the real main game binary (>= 25 MB and >= 4x larger).
            if (isIgnoredHelper || currentLen < 10L * 1024 * 1024)
            {
                string? discovered = FindExecutable(installPath);
                if (!string.IsNullOrEmpty(discovered) && File.Exists(discovered))
                {
                    long discoveredLen = new FileInfo(discovered).Length;
                    if (isIgnoredHelper || (discoveredLen >= 25L * 1024 * 1024 && discoveredLen >= currentLen * 4))
                        return discovered;
                }
            }
        }
        catch { }

        return exe;
    }
}
