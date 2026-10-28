using System.Diagnostics;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Drivers;

public class MesaBackend : IDriverBackend
{
    private readonly GpuVendor _vendor;

    public MesaBackend(GpuVendor vendor = GpuVendor.Amd)
    {
        _vendor = vendor;
    }

    public GpuVendor Vendor => _vendor;

    public async Task<DriverInfo> GetDriverInfoAsync(CancellationToken cancellationToken = default)
    {
        string deviceName = _vendor == GpuVendor.Amd ? "AMD Radeon GPU" : "Intel Graphics";
        string driverVersion = "Mesa";

        // 1. Try vulkaninfo --summary first (works on pure Wayland without mesa-utils/glxinfo)
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "vulkaninfo",
                    Arguments = "--summary",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("deviceName", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
                {
                    var name = line.Split('=', 2)[1].Trim();
                    if (!name.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase))
                        deviceName = name;
                }
                else if (line.Contains("driverInfo", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
                {
                    var info = line.Split('=', 2)[1].Trim();
                    if (!string.IsNullOrWhiteSpace(info))
                        driverVersion = info;
                }
            }
        }
        catch { }

        // 2. Fallback to glxinfo -B if deviceName is still generic
        if (deviceName is "AMD Radeon GPU" or "Intel Graphics")
        {
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "glxinfo",
                        Arguments = "-B",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
                await proc.WaitForExitAsync(cancellationToken);

                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains("OpenGL renderer string:", StringComparison.OrdinalIgnoreCase))
                    {
                        deviceName = line.Split(':', 2)[1].Trim();
                    }
                    else if (line.Contains("OpenGL version string:", StringComparison.OrdinalIgnoreCase))
                    {
                        driverVersion = line.Split(':', 2)[1].Trim();
                    }
                }
            }
            catch { }
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string xdgCache = SCSKiller.Linux.Core.AppPaths.GetXdgCacheHome(home);
        string? envCacheDir = Environment.GetEnvironmentVariable("MESA_SHADER_CACHE_DIR");
        string[] candidateDirs = new[]
        {
            !string.IsNullOrWhiteSpace(envCacheDir) ? Path.Combine(envCacheDir, "mesa_shader_cache_db") : "",
            !string.IsNullOrWhiteSpace(envCacheDir) ? Path.Combine(envCacheDir, "mesa_shader_cache_sf") : "",
            !string.IsNullOrWhiteSpace(envCacheDir) ? envCacheDir : "",
            Path.Combine(xdgCache, "mesa_shader_cache_db"),
            Path.Combine(xdgCache, "mesa_shader_cache_sf"),
            Path.Combine(xdgCache, "mesa_shader_cache"),
            Path.Combine(home, ".cache", "mesa_shader_cache_db"),
            Path.Combine(home, ".cache", "mesa_shader_cache_sf"),
            Path.Combine(home, ".cache", "mesa_shader_cache")
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();

        string cachePath = candidateDirs.FirstOrDefault(Directory.Exists)
            ?? Path.Combine(xdgCache, "mesa_shader_cache_db");

        long globalSize = 0;
        int globalFiles = 0;

        var globalCacheFiles = new List<string>();
        foreach (var dir in candidateDirs.Where(Directory.Exists))
        {
            try
            {
                globalCacheFiles.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            }
            catch { }
        }
        if (globalCacheFiles.Count > 0)
        {
            var summedGlobal = ProtonRunner.SumUniqueFileBytes(globalCacheFiles);
            globalSize = summedGlobal.Bytes;
            globalFiles = summedGlobal.UniqueFiles;
        }

        // Measure Steam per-game shader caches across Native & Flatpak Steam
        long steamSize = 0;
        int steamFiles = 0;
        var steamCacheFiles = new List<string>();
        foreach (var steamShaderCache in ProtonRunner.GetAllSteamShaderCacheRoots())
        {
            try
            {
                steamCacheFiles.AddRange(Directory.GetFiles(steamShaderCache, "*", SearchOption.AllDirectories)
                    .Where(ProtonRunner.IsCompiledShaderCacheFile));
            }
            catch { }
        }
        if (steamCacheFiles.Count > 0)
        {
            var summedSteam = ProtonRunner.SumUniqueFileBytes(steamCacheFiles);
            steamSize = summedSteam.Bytes;
            steamFiles = summedSteam.UniqueFiles;
        }

        long totalSize = globalSize + steamSize;
        var status = (Directory.Exists(cachePath) || totalSize > 0) ? DriverCacheStatus.Healthy : DriverCacheStatus.DirectoryMissing;
        string? message = status == DriverCacheStatus.DirectoryMissing ? "Mesa shader cache directory not yet created." : null;

        return new DriverInfo(
            Vendor: _vendor,
            DeviceName: deviceName,
            DriverName: _vendor == GpuVendor.Amd ? "Mesa RADV (ACO)" : "Mesa ANV",
            DriverVersion: driverVersion,
            ShaderCachePath: cachePath,
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
            long totalBytes = 0;
            int totalFiles = 0;
            string foundPath = string.Empty;

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
                    totalBytes += summed.Bytes;
                    totalFiles += summed.UniqueFiles;
                    foundPath = targetPath;
                }
                catch { }
            }

            return (totalBytes, totalFiles, foundPath);
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
                if (Directory.Exists(targetPath))
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
        // 1. Clear Mesa Driver Shader Cache
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
        return $"MESA_SHADER_CACHE_MAX_SIZE={settings.MesaMaxCacheSize}\nRADV_PERFTEST=aco";
    }
}
