using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace SCSKiller.Core.App;

/// <summary>The "re-warm after a driver update" task: at logon and when the PC goes idle it runs
/// <c>scskiller rewarm-stale --if-driver-changed</c> as the current user (no elevation).</summary>
public static class ScheduledTask
{
    public const string Name = @"SCSKiller\RewarmStale";

    public static string Xml(string cliExe, string user)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = new XDocument(new XDeclaration("1.0", "UTF-16", null), new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", "SCSKiller: re-warm game shader caches after a GPU driver update")),
            new XElement(ns + "Triggers",
                new XElement(ns + "LogonTrigger", new XElement(ns + "Enabled", "true"), new XElement(ns + "UserId", user), new XElement(ns + "Delay", "PT2M")),
                new XElement(ns + "IdleTrigger", new XElement(ns + "Enabled", "true"))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"),
                new XElement(ns + "UserId", user), new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "true"),
                new XElement(ns + "StopIfGoingOnBatteries", "true"),
                new XElement(ns + "IdleSettings", new XElement(ns + "Duration", "PT10M"), new XElement(ns + "WaitTimeout", "PT1H"),
                    new XElement(ns + "StopOnIdleEnd", "false"), new XElement(ns + "RestartOnIdle", "false")),   // the app pauses itself while a game runs
                new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(ns + "Priority", "7")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(ns + "Exec", new XElement(ns + "Command", cliExe), new XElement(ns + "Arguments", "rewarm-stale --if-driver-changed")))));
        return doc.Declaration + Environment.NewLine + doc;
    }

    public static void Register(string cliExe)
    {
        var file = Path.Combine(Path.GetTempPath(), "scskiller_task.xml");
        File.WriteAllText(file, Xml(cliExe, $"{Environment.UserDomainName}\\{Environment.UserName}"), Encoding.Unicode);
        try { Schtasks(true, "/Create", "/TN", Name, "/XML", file, "/F"); }
        finally { File.Delete(file); }
    }

    public static bool Registered => Schtasks(false, "/Query", "/TN", Name);

    public static void Unregister()
    {
        if (Registered) Schtasks(true, "/Delete", "/TN", Name, "/F");
    }

    static bool Schtasks(bool throwOnError, params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && throwOnError) throw new InvalidOperationException($"schtasks failed ({p.ExitCode}): {err.Trim()}");
        return p.ExitCode == 0;
    }

    /// <summary>The app argument the task's CLI launches SCSKiller.exe with in Ask mode: the app shows the driver-update
    /// notification (if <see cref="ScsKiller.ShouldNotifyStale"/>) and exits unless the user acts on it.</summary>
    public const string DriverUpdatedArg = "--driver-updated";

    /// <summary>What the task runs: the windowless copy of the CLI (scskillerw.exe, no console popping up at logon)
    /// if published, else scskiller.exe. Looks in cli\ (called from the app) and next to <paramref name="baseDir"/> (the CLI itself).</summary>
    public static string? TaskExe(string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        return new[] { @"cli\scskillerw.exe", @"cli\scskiller.exe", "scskillerw.exe", "scskiller.exe" }
            .Select(f => Path.Combine(baseDir, f)).FirstOrDefault(File.Exists);
    }

    /// <summary>The published app, one folder above the CLI (dist\SCSKiller\SCSKiller.exe next to cli\). Null in dev builds.</summary>
    public static string? AppExe(string? baseDir = null)
    {
        var app = Path.GetFullPath(Path.Combine(baseDir ?? AppContext.BaseDirectory, "..", "SCSKiller.exe"));
        return File.Exists(app) ? app : null;
    }
}
