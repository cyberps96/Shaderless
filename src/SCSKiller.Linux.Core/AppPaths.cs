namespace SCSKiller.Linux.Core;

/// <summary>Shaderless's data folder (~/.local/share/shaderless). The first use moves the folder of builds from before the
/// rename (~/.local/share/scskiller-linux) there, recordings, plans and prefixes included.</summary>
public static class AppPaths
{
    public const string AppName = "Shaderless";

    /// <summary>The app's version (set once, in SCSKiller.Linux.Core.csproj), e.g. "0.1.1-beta".</summary>
    public static string Version { get; } =
        (typeof(AppPaths).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "dev")
        .Split('+')[0];
    static readonly Lazy<string> _dataDir = new(Resolve);
    public static string DataDir => _dataDir.Value;

    static string Resolve()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var share = GetXdgDataHome(home);
        var dir = Path.Combine(share, "shaderless");
        var old = Path.Combine(share, "scskiller-linux");
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(old)) Directory.Move(old, dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return old; }   // left where it is: still used
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetXdgDataHome(string? homeOverride = null, string? xdgDataOverride = null)
    {
        string? env = xdgDataOverride ?? Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        string home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share");
    }

    public static string GetXdgConfigHome(string? homeOverride = null, string? xdgConfigOverride = null)
    {
        string? env = xdgConfigOverride ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        string home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config");
    }

    public static string GetXdgCacheHome(string? homeOverride = null, string? xdgCacheOverride = null)
    {
        string? env = xdgCacheOverride ?? Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        string home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cache");
    }

    public static string GetCanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parts = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string current = Path.DirectorySeparatorChar.ToString();
            foreach (var part in parts)
            {
                string next = Path.Combine(current, part);
                if (Directory.Exists(next))
                {
                    var di = new DirectoryInfo(next);
                    if (di.LinkTarget != null)
                    {
                        var target = Directory.ResolveLinkTarget(next, returnFinalTarget: true);
                        if (target != null)
                        {
                            current = target.FullName;
                            continue;
                        }
                    }
                }
                else if (File.Exists(next))
                {
                    var fi = new FileInfo(next);
                    if (fi.LinkTarget != null)
                    {
                        var target = File.ResolveLinkTarget(next, returnFinalTarget: true);
                        if (target != null)
                        {
                            current = target.FullName;
                            continue;
                        }
                    }
                }
                current = next;
            }
            return current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}

