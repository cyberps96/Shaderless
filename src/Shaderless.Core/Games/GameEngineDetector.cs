using Shaderless.Core.Models;
using Shaderless.Core.Pipelines;

namespace Shaderless.Core.Games;

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

            // 4b. Ubisoft Anvil / Scimitar Engine (*.forge archives — Assassin's Creed, Rainbow Six, Ghost Recon, For Honor)
            if (Directory.EnumerateFiles(installPath, "*.forge", SearchOption.TopDirectoryOnly).Any())
            {
                bool hasAnvilDx12 =
                    Directory.Exists(Path.Combine(installPath, "D3D12")) ||
                    Directory.EnumerateFiles(installPath, "*_dx12.forge", SearchOption.TopDirectoryOnly).Any() ||
                    Directory.EnumerateFiles(installPath, "*d3d12*.dll", SearchOption.TopDirectoryOnly).Any() ||
                    Directory.EnumerateFiles(installPath, "*dx12*.dll", SearchOption.TopDirectoryOnly).Any();
                return hasAnvilDx12
                    ? (GameEngine.DirectX12, 8500)
                    : (GameEngine.DirectX11, 4500);
            }

            var boundedRecurse = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 5
            };

            // 5. Fast top-level CryEngine & Unreal Engine directory checks, plus Unity top-level check
            // (Checking Unity before recursive CryEngine/Unreal directory walks avoids walking thousands of Unity .bundle files!)
            bool hasCryEnginePaks =
                File.Exists(Path.Combine(installPath, "Engine", "Engine.pak")) ||
                File.Exists(Path.Combine(installPath, "Engine", "ShaderCache.pak")) ||
                File.Exists(Path.Combine(installPath, "Engine", "ShadersBin.pak"));
            if (hasCryEnginePaks)
            {
                bool hasCryDx12 =
                    Directory.EnumerateFiles(installPath, "*d3d12*.dll", boundedRecurse).Any() ||
                    Directory.EnumerateFiles(installPath, "*dx12*.dll", boundedRecurse).Any() ||
                    Directory.EnumerateFiles(installPath, "nvngx_dlss*.dll", boundedRecurse).Any();
                return hasCryDx12
                    ? (GameEngine.DirectX12, 6000)
                    : (GameEngine.DirectX11, 3500);
            }

            bool hasUnrealEngineDir =
                Directory.Exists(Path.Combine(installPath, "Engine", "Binaries")) ||
                Directory.Exists(Path.Combine(installPath, "Engine", "Content"));

            // 6. Unity (instant top-level check when neither CryEngine Engine/*.pak nor Unreal Engine/Binaries exists)
            if (!hasUnrealEngineDir &&
                (File.Exists(Path.Combine(installPath, "UnityPlayer.dll")) ||
                 Directory.EnumerateDirectories(installPath, "*_Data", SearchOption.TopDirectoryOnly).Any()))
            {
                return (GameEngine.Unity, 4500);
            }

            // 7. CryEngine subfolder DLL fallback (Kingdom Come: Deliverance I/II, Crysis, Hunt, Prey)
            if (!hasUnrealEngineDir &&
                (Directory.EnumerateFiles(installPath, "CryRenderD3D*.dll", boundedRecurse).Any() ||
                 Directory.EnumerateFiles(installPath, "WHGame.dll", boundedRecurse).Any()))
            {
                bool hasCryDx12 =
                    Directory.EnumerateFiles(installPath, "*d3d12*.dll", boundedRecurse).Any() ||
                    Directory.EnumerateFiles(installPath, "*dx12*.dll", boundedRecurse).Any() ||
                    Directory.EnumerateFiles(installPath, "nvngx_dlss*.dll", boundedRecurse).Any();
                return hasCryDx12
                    ? (GameEngine.DirectX12, 6000)
                    : (GameEngine.DirectX11, 3500);
            }

            // 8. Unreal Engine 4 & 5
            if (Directory.EnumerateFiles(installPath, "*.ucas", boundedRecurse).Any())
            {
                return (GameEngine.UnrealEngine5, 14000);
            }
            if (hasUnrealEngineDir ||
                Directory.EnumerateFiles(installPath, "*.pak", boundedRecurse).Any(f => !IsNonUnrealPak(f)))
            {
                return (GameEngine.UnrealEngine4, 8000);
            }

            // 9. Generic DirectX 12
            if (Directory.EnumerateFiles(installPath, "*d3d12*.dll", boundedRecurse).Any())
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
            // 1. Unreal Shipping executable check via Shaderless.Engine
            try
            {
                string? unrealShipping = Shaderless.Engine.Games.GameFiles.ShippingExe(installPath);
                if (!string.IsNullOrEmpty(unrealShipping) && File.Exists(unrealShipping))
                    return unrealShipping;
            }
            catch { }

            // Deprioritize Launcher / Wargaming Game Center stubs when real game executables are also present
            static bool IsLauncherStub(string path)
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                return stem.EndsWith("Launcher", StringComparison.OrdinalIgnoreCase) ||
                       stem.Equals("wgc", StringComparison.OrdinalIgnoreCase) ||
                       stem.StartsWith("wgc_", StringComparison.OrdinalIgnoreCase);
            }

            // 1b. Fast top-level Unity <Game>.exe paired with <Game>_Data folder (avoids walking thousands of asset bundles)
            foreach (var topExe in Directory.EnumerateFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (!IgnoredExeSubstrings.Any(ig => topExe.Contains(ig, StringComparison.OrdinalIgnoreCase)) &&
                    !IsLauncherStub(topExe) &&
                    Directory.Exists(Path.ChangeExtension(topExe, null) + "_Data"))
                {
                    return topExe;
                }
            }

            var boundedRecurse = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 5
            };

            var exeFiles = Directory.EnumerateFiles(installPath, "*.exe", boundedRecurse)
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
                string resolved = Shaderless.Engine.Games.GameFiles.GameExe(installPath, candidate);
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                    candidate = resolved;
            }
            catch { }

            return ResolveUbisoftPlusExecutable(candidate);
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
            string resolved = Shaderless.Engine.Games.GameFiles.GameExe(installPath, exe);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                exe = resolved;
        }
        catch { }

        exe = ResolveUbisoftPlusExecutable(exe);

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

    /// <summary>
    /// Ubisoft Connect games ship both <c>&lt;Stem&gt;.exe</c> (standard owned copy) and <c>&lt;Stem&gt;_Plus.exe</c> (Ubisoft+ subscription copy,
    /// often slightly larger than <c>&lt;Stem&gt;.exe</c>) side-by-side in the same directory. When both exist, honors whichever executable
    /// most recently ran in <c>shaderless.log</c> / <c>shaderless_creates.csv</c>, and otherwise defaults to standard <c>&lt;Stem&gt;.exe</c>.
    /// </summary>
    public static string ResolveUbisoftPlusExecutable(string exePath)
    {
        if (!TryGetUbisoftPlusExecutablePair(exePath, out string plainExe, out string plusExe))
            return exePath;

        string? dir = Path.GetDirectoryName(plainExe);
        if (!string.IsNullOrEmpty(dir))
        {
            string plainName = Path.GetFileName(plainExe);
            string plusName = Path.GetFileName(plusExe);

            foreach (var logName in new[] { "shaderless.log", "scskiller.log" })
            {
                string logPath = Path.Combine(dir, logName);
                if (!File.Exists(logPath)) continue;
                try
                {
                    var lines = File.ReadAllLines(logPath);
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        string line = lines[i];
                        if (!line.Contains("loaded into ", StringComparison.OrdinalIgnoreCase)) continue;
                        if (ContainsExecutableNameToken(line, plusName))
                            return plusExe;
                        if (ContainsExecutableNameToken(line, plainName))
                            return plainExe;
                    }
                }
                catch { }
            }

            foreach (var csvName in new[] { "shaderless_creates.csv", "scskiller_creates.csv" })
            {
                string csvPath = Path.Combine(dir, csvName);
                if (!File.Exists(csvPath)) continue;
                try
                {
                    var lines = File.ReadAllLines(csvPath);
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        string line = lines[i].Trim();
                        if (!line.StartsWith("#session,", StringComparison.OrdinalIgnoreCase)) continue;
                        var parts = line.Split(',');
                        if (parts.Length >= 3)
                        {
                            string sessionExe = Path.GetFileName(parts[2].Trim().Replace('\\', '/'));
                            if (sessionExe.Equals(plusName, StringComparison.OrdinalIgnoreCase))
                                return plusExe;
                            if (sessionExe.Equals(plainName, StringComparison.OrdinalIgnoreCase))
                                return plainExe;
                        }
                    }
                }
                catch { }
            }
        }

        return plainExe;
    }

    internal static bool TryGetUbisoftPlusExecutablePair(string? exePath, out string plainExe, out string plusExe)
    {
        plainExe = string.Empty;
        plusExe = string.Empty;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return false;

        string? dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return false;

        string stem = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrEmpty(stem))
            return false;

        string baseStem = stem.EndsWith("_Plus", StringComparison.OrdinalIgnoreCase) && stem.Length > 5
            ? stem[..^5]
            : stem;

        string? foundPlain = FindExactFileIgnoreCase(dir, baseStem + ".exe");
        string? foundPlus = FindExactFileIgnoreCase(dir, baseStem + "_Plus.exe");
        if (foundPlain == null || foundPlus == null)
            return false;

        try
        {
            long plainLen = new FileInfo(foundPlain).Length;
            long plusLen = new FileInfo(foundPlus).Length;
            if (plainLen <= 0 || plusLen <= 0 || plainLen < plusLen / 4)
                return false;
        }
        catch
        {
            return false;
        }

        plainExe = foundPlain;
        plusExe = foundPlus;
        return true;
    }

    /// <summary>
    /// Returns all case-sensitive executable filenames that VKD3D-Proton may use at runtime for <paramref name="exePath"/> on a
    /// case-sensitive Linux filesystem: the on-disk filename, any paired Ubisoft <c>&lt;Stem&gt;.exe</c> / <c>&lt;Stem&gt;_Plus.exe</c> /
    /// <c>&lt;Stem&gt;_plus.exe</c> variants (Ubisoft Connect launches <c>_plus.exe</c> in lowercase), plus any exact runtime process
    /// casing recorded in <c>shaderless.log</c> or <c>shaderless_creates.csv</c> for any game.
    /// </summary>
    public static IReadOnlyList<string> GetObservedRuntimeExecutableFileNames(string? exePath)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(exePath))
            return results;

        var seenExact = new HashSet<string>(StringComparer.Ordinal);
        var allowedIgnoreCase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddExact(string? fn)
        {
            if (string.IsNullOrWhiteSpace(fn)) return;
            string clean = Path.GetFileName(fn.Replace('\\', '/').Trim());
            if (clean.Length == 0 || !clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return;
            if (seenExact.Add(clean))
                results.Add(clean);
        }

        string primaryName = Path.GetFileName(exePath.Replace('\\', '/'));
        if (primaryName.Length == 0)
            return results;

        allowedIgnoreCase.Add(primaryName);
        AddExact(primaryName);

        if (TryGetUbisoftPlusExecutablePair(exePath, out string plainExe, out string plusExe))
        {
            string plainName = Path.GetFileName(plainExe);
            string plusName = Path.GetFileName(plusExe);
            string baseStem = Path.GetFileNameWithoutExtension(plainName);
            allowedIgnoreCase.Add(plainName);
            allowedIgnoreCase.Add(plusName);
            AddExact(plainName);
            AddExact(plusName);
            AddExact(baseStem + "_Plus.exe");
            AddExact(baseStem + "_plus.exe");
        }

        string? dir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            foreach (var logName in new[] { "shaderless.log", "scskiller.log" })
            {
                string logPath = Path.Combine(dir, logName);
                if (!File.Exists(logPath)) continue;
                try
                {
                    foreach (var line in File.ReadLines(logPath))
                    {
                        if (TryExtractLoadedExecutableFileName(line, out string runtimeExe) &&
                            allowedIgnoreCase.Contains(runtimeExe))
                        {
                            AddExact(runtimeExe);
                        }
                    }
                }
                catch { }
            }

            foreach (var csvName in new[] { "shaderless_creates.csv", "scskiller_creates.csv" })
            {
                string csvPath = Path.Combine(dir, csvName);
                if (!File.Exists(csvPath)) continue;
                try
                {
                    foreach (var rawLine in File.ReadLines(csvPath))
                    {
                        string line = rawLine.Trim();
                        if (!line.StartsWith("#session,", StringComparison.OrdinalIgnoreCase)) continue;
                        var parts = line.Split(',');
                        if (parts.Length >= 3)
                        {
                            string sessionExe = Path.GetFileName(parts[2].Trim().Replace('\\', '/'));
                            if (allowedIgnoreCase.Contains(sessionExe))
                                AddExact(sessionExe);
                        }
                    }
                }
                catch { }
            }
        }

        return results;
    }

    private static bool TryExtractLoadedExecutableFileName(string line, out string exeFileName)
    {
        exeFileName = string.Empty;
        const string marker = "loaded into ";
        int idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        ReadOnlySpan<char> rest = line.AsSpan(idx + marker.Length).Trim();
        int exeEnd = rest.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd < 0) return false;

        ReadOnlySpan<char> fullExe = rest[..(exeEnd + 4)].Trim('"');
        int lastSep = fullExe.LastIndexOfAny('\\', '/');
        ReadOnlySpan<char> baseName = lastSep >= 0 ? fullExe[(lastSep + 1)..] : fullExe;
        if (baseName.Length <= 4) return false;

        exeFileName = baseName.ToString();
        return true;
    }

    private static string? FindExactFileIgnoreCase(string directory, string fileName)
    {
        string direct = Path.Combine(directory, fileName);
        if (File.Exists(direct))
            return direct;

        try
        {
            return Directory.EnumerateFiles(directory)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsExecutableNameToken(string line, string exeFileName)
    {
        int idx = line.IndexOf(exeFileName, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        bool validBefore = idx == 0 || line[idx - 1] is '\\' or '/' or ' ' or '"';
        int after = idx + exeFileName.Length;
        bool validAfter = after >= line.Length || line[after] is ' ' or '"' or '\r' or '\n';
        return validBefore && validAfter;
    }
}
