using Microsoft.Win32;

namespace SCSKiller.Core.App;

/// <summary>"Start SCSKiller when Windows starts" (<see cref="Settings.StartWithWindows"/>): the HKCU Run value that starts
/// the app hidden in the notification area at sign-in.</summary>
public static class WindowsStartup
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "SCSKiller", TrayArg = "--tray";

    public static string Command(string appExe) => $"\"{appExe}\" {TrayArg}";

    /// <summary>Writes the Run value (on) or removes it (off) when it differs from what is there; true if it changed.
    /// <paramref name="read"/>/<paramref name="write"/> are the value (null = absent): the registry, or a fake in tests.</summary>
    public static bool Apply(bool on, string appExe, Func<string?> read, Action<string?> write)
    {
        var want = on ? Command(appExe) : null;
        if (read() == want) return false;
        write(want);
        return true;
    }

    /// <summary><see cref="Apply(bool, string, Func{string?}, Action{string?})"/> on HKCU\...\Run.</summary>
    public static bool Apply(bool on, string appExe) => Apply(on, appExe,
        () =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        },
        value =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value == null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            else key.SetValue(ValueName, value);
        });
}
