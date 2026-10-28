using System.Diagnostics;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Drivers;

public class NvidiaBackend : IDriverBackend
{
    public GpuVendor Vendor => GpuVendor.Nvidia;

    public async Task<DriverInfo> GetDriverInfoAsync(CancellationToken cancellationToken = default)
    {
        string deviceName = "NVIDIA GeForce GPU";
        string driverVersion = "Unknown";

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=gpu_name,driver_version --format=csv,noheader",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            var parts = output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                deviceName = parts[0];
                driverVersion = parts[1];
            }
        }
        catch { }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string xdgCache = SCSKiller.Linux.Core.AppPaths.GetXdgCacheHome(home);
        string globalCachePath = Path.Combine(xdgCache, "nvidia", "GLCache");
        if (!Directory.Exists(globalCachePath))
        {
            string fallbackCache = Path.Combine(home, ".cache", "nvidia", "GLCache");
            if (Directory.Exists(fallbackCache))
            {
                globalCachePath = fallbackCache;
            }
            else
            {
                string legacyPath = Path.Combine(home, ".nv", "GLCache");
                if (Directory.Exists(legacyPath))
                {
                    globalCachePath = legacyPath;
                }
            }
        }

        long globalSize = 0;
        int globalFiles = 0;

        if (Directory.Exists(globalCachePath))
        {
            try
            {
                var files = Directory.GetFiles(globalCachePath, "*", SearchOption.AllDirectories);
                var summed = ProtonRunner.SumUniqueFileBytes(files);
                globalSize = summed.Bytes;
                globalFiles = summed.UniqueFiles;
            }
            catch { }
        }

        // Measure Steam per-game shader caches across Native & Flatpak Steam
        long steamSize = 0;
        int steamFiles = 0;
        var allSteamFiles = new List<string>();
        foreach (var steamShaderCache in ProtonRunner.GetAllSteamShaderCacheRoots())
        {
            try
            {
                allSteamFiles.AddRange(Directory.GetFiles(steamShaderCache, "*", SearchOption.AllDirectories)
                    .Where(ProtonRunner.IsCompiledShaderCacheFile));
            }
            catch { }
        }
        if (allSteamFiles.Count > 0)
        {
            var summedSteam = ProtonRunner.SumUniqueFileBytes(allSteamFiles);
            steamSize = summedSteam.Bytes;
            steamFiles = summedSteam.UniqueFiles;
        }

        long totalSize = globalSize + steamSize;
        int totalFiles = globalFiles + steamFiles;

        var status = DriverCacheStatus.Healthy;
        string? message = null;

        if (totalFiles == 0)
        {
            status = DriverCacheStatus.InvalidatedByUpdate;
            message = "Cache is currently empty or recently cleared after a driver update.";
        }

        return new DriverInfo(
            Vendor: GpuVendor.Nvidia,
            DeviceName: deviceName,
            DriverName: "NVIDIA Proprietary Driver",
            DriverVersion: driverVersion,
            ShaderCachePath: globalCachePath,
            CacheSizeBytes: globalSize,
            CachedFilesCount: globalFiles,
            SteamShaderCacheSizeBytes: steamSize,
            SteamShaderCacheFilesCount: steamFiles,
            TotalCacheSizeBytes: totalSize,
            Status: status,
            StatusMessage: message
        );
    }

    public async Task<(long Bytes, int Files, string Path)> GetGameCacheStatsAsync(string gameId, GameSource source, string? prefixPath = null)
    {
        return await Task.Run(() =>
        {
            string targetPath = source == GameSource.Steam
                ? ProtonRunner.GetSteamShaderCachePath(gameId, null, prefixPath)
                : ProtonRunner.GetNonSteamShaderCachePath(gameId, prefixPath);

            if (Directory.Exists(targetPath))
            {
                try
                {
                    var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories)
                        .Where(ProtonRunner.IsCompiledShaderCacheFile)
                        .ToArray();
                    var summed = ProtonRunner.SumUniqueFileBytes(files);
                    return (summed.Bytes, summed.UniqueFiles, targetPath);
                }
                catch { }
            }

            return (0L, 0, string.Empty);
        });
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetDriverInfoAsync(cancellationToken);
        if (Directory.Exists(info.ShaderCachePath))
        {
            await Task.Run(() =>
            {
                var dirs = Directory.GetDirectories(info.ShaderCachePath);
                foreach (var dir in dirs)
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
                var files = Directory.GetFiles(info.ShaderCachePath);
                foreach (var file in files)
                {
                    try { File.Delete(file); } catch { }
                }
            }, cancellationToken);
        }
    }

    public async Task ClearGameCacheAsync(string gameId, GameSource source, string? prefixPath = null, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            if (source == GameSource.Steam)
            {
                string targetPath = ProtonRunner.GetSteamShaderCachePath(gameId, null, prefixPath);
                if (!string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath))
                {
                    string manifest = Path.Combine(targetPath, "installed-files.txt");
                    if (File.Exists(manifest))
                    {
                        foreach (var p in File.ReadAllLines(manifest).Where(l => l.Length > 0))
                            try { File.Delete(p); } catch { }
                    }
                    foreach (var subDir in Directory.GetDirectories(targetPath))
                    {
                        string name = Path.GetFileName(subDir);
                        if (name.StartsWith("fozpipelines", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("fozmedia", StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { Directory.Delete(subDir, true); } catch { }
                    }
                    foreach (var file in Directory.GetFiles(targetPath))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            else
            {
                string effectivePrefix = !string.IsNullOrEmpty(prefixPath)
                    ? prefixPath
                    : Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "prefixes", gameId);
                ProtonRunner.RemoveInstalledCaches(effectivePrefix, gameId);
                string targetPath = ProtonRunner.GetNonSteamShaderCachePath(gameId, prefixPath);
                if (Directory.Exists(targetPath))
                {
                    try { Directory.Delete(targetPath, true); } catch { }
                }
            }
            SCSKiller.Linux.Core.Games.RecorderManager.ClearWarmedKeys(gameId);
        }, cancellationToken);
    }

    public async Task ClearAllCachesAsync(CancellationToken cancellationToken = default)
    {
        // 1. Clear Global GPU Driver Cache
        await ClearCacheAsync(cancellationToken);

        // 2. Clear All Steam Game Shader Caches across Native & Flatpak Steam
        await Task.Run(() =>
        {
            foreach (var steamShaderCache in ProtonRunner.GetAllSteamShaderCacheRoots())
            {
                var dirs = Directory.GetDirectories(steamShaderCache);
                foreach (var dir in dirs)
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }, cancellationToken);
    }

    public string GetRecommendedEnvironmentVariables()
    {
        var settings = SCSKiller.Linux.Core.SettingsManager.Load();
        return $"__GL_SHADER_DISK_CACHE=1\n__GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1\n__GL_SHADER_DISK_CACHE_SIZE={settings.MaxCacheSizeBytes}";
    }
}
