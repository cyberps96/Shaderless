using System.Diagnostics;

namespace Shaderless.Core.Drivers;

public static class DriverDetector
{
    public static async Task<IDriverBackend> DetectActiveBackendAsync(CancellationToken cancellationToken = default)
    {
        // 0. Fast kernel-driver check for NVIDIA on Linux (avoids spawning nvidia-smi on every startup)
        try
        {
            if (File.Exists("/sys/module/nvidia/version") &&
                Directory.Exists("/proc/driver/nvidia/gpus") &&
                Directory.EnumerateDirectories("/proc/driver/nvidia/gpus").Any())
            {
                return new NvidiaBackend();
            }
        }
        catch { }

        // 1. Check for NVIDIA discrete GPU via nvidia-smi fallback (containers / WSL / restricted /proc)
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=gpu_name --format=csv,noheader",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return new NvidiaBackend();
            }
        }
        catch
        {
            // nvidia-smi not found or failed, continue detection
        }

        // 2. Query Vulkan devices
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "vulkaninfo",
                    Arguments = "--summary",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            if (output.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                return new NvidiaBackend();
            }
            if (output.Contains("AMD", StringComparison.OrdinalIgnoreCase) || output.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            {
                return new MesaBackend(GpuVendor.Amd);
            }
            if (output.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                return new MesaBackend(GpuVendor.Intel);
            }
        }
        catch
        {
            // vulkaninfo not found, fallback to Mesa AMD default
        }

        return new MesaBackend(GpuVendor.Amd);
    }
}
