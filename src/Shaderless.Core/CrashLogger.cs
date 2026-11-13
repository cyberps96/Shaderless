using System.Runtime.InteropServices;
using System.Text;

namespace Shaderless.Core;

public record CrashLogStatus(
    string DataDir,
    string CrashLogPath,
    bool Exists,
    long SizeBytes,
    DateTime? LastModifiedUtc
);

/// <summary>
/// Writes timestamped unhandled exception & diagnostic crash entries to ~/.local/share/shaderless/crash.log,
/// rotating files larger than 1 MB to crash.old.log so disk usage stays bounded.
/// </summary>
public static class CrashLogger
{
    public const string FileName = "crash.log";
    public const string OldFileName = "crash.old.log";
    public const long MaxLogBytes = 1024 * 1024; // 1 MB

    private static readonly object Gate = new();
    private static int _handlersRegistered;
    private static Exception? _lastLoggedException;
    private static volatile string? _activeDriverSummary;

    public static string CrashLogPath => Path.Combine(AppPaths.DataDir, FileName);
    public static string OldCrashLogPath => Path.Combine(AppPaths.DataDir, OldFileName);

    public static void SetActiveDriverSummary(string? summary)
    {
        if (!string.IsNullOrWhiteSpace(summary))
            _activeDriverSummary = summary.Trim();
    }

    public static void RegisterGlobalHandlers(string processName)
    {
        if (Interlocked.Exchange(ref _handlersRegistered, 1) != 0)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogException(ex, $"{processName} AppDomain.UnhandledException (terminating={args.IsTerminating})");
            }
            else if (args.ExceptionObject != null)
            {
                LogError(args.ExceptionObject.ToString() ?? "Unknown non-Exception fault", $"{processName} AppDomain.UnhandledException");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException(args.Exception, $"{processName} TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };
    }

    public static void LogException(Exception ex, string context = "Unhandled", string? dataDirOverride = null)
    {
        if (ex == null) return;

        lock (Gate)
        {
            if (ReferenceEquals(_lastLoggedException, ex))
                return;
            _lastLoggedException = ex;

            WriteEntryCore(context, ex.ToString(), dataDirOverride);
        }
    }

    public static void LogError(string message, string context = "Error", string? dataDirOverride = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        lock (Gate)
        {
            WriteEntryCore(context, message.Trim(), dataDirOverride);
        }
    }

    public static CrashLogStatus GetStatus(string? dataDirOverride = null)
    {
        string dir = !string.IsNullOrEmpty(dataDirOverride) ? dataDirOverride : AppPaths.DataDir;
        string path = Path.Combine(dir, FileName);
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 0)
            {
                return new CrashLogStatus(dir, path, Exists: true, fi.Length, fi.LastWriteTimeUtc);
            }
        }
        catch { }

        return new CrashLogStatus(dir, path, Exists: false, 0, null);
    }

    public static string? ReadRecentTail(int maxChars = 16384, string? dataDirOverride = null)
    {
        if (maxChars <= 0) maxChars = 4096;
        string dir = !string.IsNullOrEmpty(dataDirOverride) ? dataDirOverride : AppPaths.DataDir;
        string path = Path.Combine(dir, FileName);

        try
        {
            if (!File.Exists(path))
                return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0)
                return null;

            long maxBytes = maxChars * 4L;
            if (fs.Length > maxBytes)
                fs.Seek(-maxBytes, SeekOrigin.End);

            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string content = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(content))
                return null;

            if (content.Length > maxChars)
                content = "...(truncated older entries)...\n" + content[^maxChars..];

            return content;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteEntryCore(string context, string details, string? dataDirOverride)
    {
        try
        {
            string dir = !string.IsNullOrEmpty(dataDirOverride) ? dataDirOverride : AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, FileName);
            string oldPath = Path.Combine(dir, OldFileName);

            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    fi.MoveTo(oldPath, overwrite: true);
                }
            }
            catch (IOException)
            {
                // Another process holds the file; append to existing log rather than failing
            }

            string driverLine = ResolveDriverSummaryFallback();
            var sb = new StringBuilder(512 + details.Length);
            sb.AppendLine("================================================================================");
            sb.Append('[')
              .Append(DateTime.UtcNow.ToString("O"))
              .Append("] Shaderless v")
              .Append(AppPaths.Version)
              .Append(" — ")
              .AppendLine(context);
            sb.Append("OS / Kernel: ")
              .Append(RuntimeInformation.OSDescription.Trim())
              .Append(" (")
              .Append(RuntimeInformation.OSArchitecture)
              .Append(") | .NET ")
              .AppendLine(Environment.Version.ToString());
            sb.Append("GPU Driver:  ").AppendLine(driverLine);
            sb.AppendLine(details);
            sb.AppendLine();

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Never allow crash logging itself to throw a secondary exception
        }
    }

    private static string ResolveDriverSummaryFallback()
    {
        string? cached = _activeDriverSummary;
        if (!string.IsNullOrWhiteSpace(cached))
            return cached;

        try
        {
            const string nvVerFile = "/sys/module/nvidia/version";
            if (File.Exists(nvVerFile))
            {
                string ver = File.ReadAllText(nvVerFile).Trim();
                if (!string.IsNullOrEmpty(ver))
                    return $"NVIDIA {ver} (kernel module)";
            }
        }
        catch { }

        return "Not detected yet";
    }
}
