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

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "glxinfo",
                    Arguments = "-B",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            var lines = output.Split('\n');
            foreach (var line in lines)
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

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string cachePath = Path.Combine(home, ".cache", "mesa_shader_cache_sf");
        if (!Directory.Exists(cachePath))
        {
            string legacyPath = Path.Combine(home, ".cache", "mesa_shader_cache");
            if (Directory.Exists(legacyPath))
            {
                cachePath = legacyPath;
            }
        }

        long totalSize = 0;
        int fileCount = 0;

        if (Directory.Exists(cachePath))
        {
            try
            {
                var files = Directory.GetFiles(cachePath, "*", SearchOption.AllDirectories);
                fileCount = files.Length;
                foreach (var file in files)
                {
                    totalSize += new FileInfo(file).Length;
                }
            }
            catch { }
        }

        var status = Directory.Exists(cachePath) ? DriverCacheStatus.Healthy : DriverCacheStatus.DirectoryMissing;
        string? message = status == DriverCacheStatus.DirectoryMissing ? "Mesa shader cache directory not yet created." : null;

        return new DriverInfo(
            Vendor: _vendor,
            DeviceName: deviceName,
            DriverName: _vendor == GpuVendor.Amd ? "Mesa RADV (ACO)" : "Mesa ANV",
            DriverVersion: driverVersion,
            ShaderCachePath: cachePath,
            CacheSizeBytes: totalSize,
            CachedFilesCount: fileCount,
            SteamShaderCacheSizeBytes: 0,
            SteamShaderCacheFilesCount: 0,
            TotalCacheSizeBytes: totalSize,
            Status: status,
            StatusMessage: message
        );
    }

    public async Task<(long Bytes, int Files, string Path)> GetGameCacheStatsAsync(string gameId, GameSource source, string? prefixPath = null)
    {
        return await Task.Run(() =>
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (source == GameSource.Steam)
            {
                string targetPath = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache", gameId);
                if (Directory.Exists(targetPath))
                {
                    try
                    {
                        var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                        long bytes = 0;
                        foreach (var f in files) bytes += new FileInfo(f).Length;
                        return (bytes, files.Length, targetPath);
                    }
                    catch { }
                }
            }
            else if (!string.IsNullOrEmpty(prefixPath))
            {
                string targetPath = Path.Combine(prefixPath, "shadercache");
                if (Directory.Exists(targetPath))
                {
                    try
                    {
                        var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                        long bytes = 0;
                        foreach (var f in files) bytes += new FileInfo(f).Length;
                        return (bytes, files.Length, targetPath);
                    }
                    catch { }
                }
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
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (source == GameSource.Steam)
            {
                string targetPath = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache", gameId);
                if (Directory.Exists(targetPath))
                {
                    try { Directory.Delete(targetPath, true); } catch { }
                }
            }
        }, cancellationToken);
    }

    public async Task ClearAllCachesAsync(CancellationToken cancellationToken = default)
    {
        // 1. Clear Mesa Driver Shader Cache
        await ClearCacheAsync(cancellationToken);

        // 2. Clear All Steam Game Shader Caches
        await Task.Run(() =>
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string steamShaderCache = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache");
            if (Directory.Exists(steamShaderCache))
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
        return "MESA_SHADER_CACHE_MAX_SIZE=10G\nRADV_PERFTEST=aco";
    }
}
