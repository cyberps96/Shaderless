using Shaderless.Core.Drivers;
using Shaderless.Core.Models;
using System.Diagnostics;

namespace Shaderless.Core.Games;

public sealed record GameLaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? EnvironmentVariables,
    string? WorkingDirectory,
    string DisplayCommand);

public sealed record GameLaunchResult(
    bool Ok,
    string Message,
    GameLaunchSpec? Spec = null);

/// <summary>
/// Launches discovered games directly through their native or Flatpak launcher URI handlers
/// (Steam, Heroic, Lutris) or through Proton/Wine with Shaderless's driver shader-cache and
/// recorder environment variables for Manual / standalone games.
/// </summary>
public static class GameLauncher
{
    public static GameLaunchSpec BuildLaunchSpec(
        GameInfo game,
        Func<string, bool>? commandExists = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        var hasCommand = commandExists ?? HasCommandInPath;

        return game.Source switch
        {
            GameSource.Steam => BuildSteamLaunchSpec(game, hasCommand),
            GameSource.Heroic => BuildHeroicLaunchSpec(game, hasCommand),
            GameSource.Lutris => BuildLutrisLaunchSpec(game, hasCommand),
            GameSource.Manual or GameSource.Bottles => BuildManualLaunchSpec(game, hasCommand),
            _ => throw new NotSupportedException($"Unsupported game source: {game.Source}")
        };
    }

    public static GameLaunchResult Launch(
        GameInfo game,
        bool isCompiling = false,
        string procRoot = "/proc",
        Func<string, bool>? commandExists = null,
        Func<GameLaunchSpec, (bool Ok, string? Error)>? processStarter = null)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (isCompiling || game.State == CacheState.Compiling)
        {
            return new GameLaunchResult(
                false,
                $"Cannot launch '{game.Name}' while shader compilation is in progress.");
        }

        if (game.IsRunning || ProtonRunner.IsGameRunning(game, procRoot))
        {
            return new GameLaunchResult(
                false,
                $"'{game.Name}' is already running.");
        }

        GameLaunchSpec spec;
        try
        {
            spec = BuildLaunchSpec(game, commandExists);
        }
        catch (Exception ex)
        {
            return new GameLaunchResult(false, ex.Message);
        }

        if (game.Source is GameSource.Manual or GameSource.Bottles)
        {
            PrepareManualRuntimeEnvironment(game, spec);
        }

        var starter = processStarter ?? StartDetachedProcess;
        var (ok, error) = starter(spec);
        if (!ok)
        {
            return new GameLaunchResult(
                false,
                string.IsNullOrWhiteSpace(error)
                    ? $"Failed to launch '{game.Name}' ({spec.DisplayCommand})."
                    : $"Failed to launch '{game.Name}': {error}",
                spec);
        }

        return new GameLaunchResult(
            true,
            $"Launching '{game.Name}' via {game.SourceLabel} ({spec.DisplayCommand})...",
            spec);
    }

    private static GameLaunchSpec BuildSteamLaunchSpec(GameInfo game, Func<string, bool> hasCommand)
    {
        string appId = game.RawId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(appId))
            throw new InvalidOperationException($"Steam game '{game.Name}' has an empty AppId.");

        string uri = $"steam://rungameid/{appId}";

        if (game.IsFlatpak)
        {
            if (!hasCommand("flatpak"))
                throw new InvalidOperationException("Cannot launch Flatpak Steam game: 'flatpak' is not installed or not in PATH.");

            var args = new[] { "run", "com.valvesoftware.Steam", uri };
            return new GameLaunchSpec("flatpak", args, null, null, FormatDisplayCommand("flatpak", args));
        }

        if (hasCommand("xdg-open"))
        {
            var args = new[] { uri };
            return new GameLaunchSpec("xdg-open", args, null, null, FormatDisplayCommand("xdg-open", args));
        }

        if (hasCommand("steam"))
        {
            var args = new[] { uri };
            return new GameLaunchSpec("steam", args, null, null, FormatDisplayCommand("steam", args));
        }

        throw new InvalidOperationException("Cannot launch Steam game: neither 'xdg-open' nor 'steam' was found in PATH.");
    }

    private static GameLaunchSpec BuildHeroicLaunchSpec(GameInfo game, Func<string, bool> hasCommand)
    {
        string appName = game.RawId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(appName))
            throw new InvalidOperationException($"Heroic game '{game.Name}' has an empty app identifier.");

        string? runner = string.IsNullOrWhiteSpace(game.LauncherRunner)
            ? null
            : game.LauncherRunner.Trim().ToLowerInvariant();

        string escapedApp = Uri.EscapeDataString(appName);
        string uri = string.IsNullOrEmpty(runner)
            ? $"heroic://launch/{escapedApp}"
            : $"heroic://launch/{Uri.EscapeDataString(runner)}/{escapedApp}";

        if (game.IsFlatpak)
        {
            if (!hasCommand("flatpak"))
                throw new InvalidOperationException("Cannot launch Flatpak Heroic game: 'flatpak' is not installed or not in PATH.");

            var args = new[] { "run", "com.heroicgameslauncher.hgl", uri };
            return new GameLaunchSpec("flatpak", args, null, null, FormatDisplayCommand("flatpak", args));
        }

        if (hasCommand("xdg-open"))
        {
            var args = new[] { uri };
            return new GameLaunchSpec("xdg-open", args, null, null, FormatDisplayCommand("xdg-open", args));
        }

        if (hasCommand("heroic"))
        {
            var args = new[] { uri };
            return new GameLaunchSpec("heroic", args, null, null, FormatDisplayCommand("heroic", args));
        }

        throw new InvalidOperationException("Cannot launch Heroic game: neither 'xdg-open' nor 'heroic' was found in PATH.");
    }

    private static GameLaunchSpec BuildLutrisLaunchSpec(GameInfo game, Func<string, bool> hasCommand)
    {
        string rawId = game.RawId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(rawId) && string.IsNullOrWhiteSpace(game.LauncherRunner))
            throw new InvalidOperationException($"Lutris game '{game.Name}' has an empty game identifier.");

        string uri;
        if (LutrisLinuxSource.TryExtractLutrisDbNumericId(rawId, out long numericId))
        {
            // Regular numeric Lutris ID ("42") or Ubisoft Connect container child ID ("12_ubisoft_8006" -> 12)
            uri = $"lutris:rungameid/{numericId}";
        }
        else
        {
            string slug = !string.IsNullOrWhiteSpace(game.LauncherRunner)
                ? game.LauncherRunner.Trim()
                : rawId;
            uri = $"lutris:rungame/{slug}";
        }

        if (game.IsFlatpak)
        {
            if (!hasCommand("flatpak"))
                throw new InvalidOperationException("Cannot launch Flatpak Lutris game: 'flatpak' is not installed or not in PATH.");

            var args = new[] { "run", "net.lutris.Lutris", uri };
            return new GameLaunchSpec("flatpak", args, null, null, FormatDisplayCommand("flatpak", args));
        }

        if (hasCommand("xdg-open"))
        {
            var args = new[] { uri };
            return new GameLaunchSpec("xdg-open", args, null, null, FormatDisplayCommand("xdg-open", args));
        }

        if (hasCommand("lutris"))
        {
            var args = new[] { uri };
            return new GameLaunchSpec("lutris", args, null, null, FormatDisplayCommand("lutris", args));
        }

        throw new InvalidOperationException("Cannot launch Lutris game: neither 'xdg-open' nor 'lutris' was found in PATH.");
    }

    private static GameLaunchSpec BuildManualLaunchSpec(GameInfo game, Func<string, bool> hasCommand)
    {
        string rawPrefix = !string.IsNullOrWhiteSpace(game.PrefixPath)
            ? game.PrefixPath.Trim()
            : Path.Combine(AppPaths.DataDir, "prefixes", game.Id);

        string trimmedPrefix = rawPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string compatData = trimmedPrefix.EndsWith("/pfx", StringComparison.Ordinal) ||
                            trimmedPrefix.EndsWith("\\pfx", StringComparison.Ordinal)
            ? (Path.GetDirectoryName(trimmedPrefix) ?? trimmedPrefix)
            : trimmedPrefix;

        string pfxCandidate = Path.Combine(compatData, "pfx");
        string winePrefix = Directory.Exists(pfxCandidate) ? pfxCandidate : trimmedPrefix;
        string shaderRoot = Path.Combine(trimmedPrefix, "shadercache");
        string glBase = !string.IsNullOrWhiteSpace(game.GlCachePath)
            ? game.GlCachePath
            : Path.Combine(shaderRoot, "nvidiav1");

        var settings = SettingsManager.Load();
        long maxCacheBytes = settings.MaxCacheSizeBytes;

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["STEAM_COMPAT_DATA_PATH"] = compatData,
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = ProtonRunner.ResolveSteamRoot(game.Id, game.InstallPath, game.PrefixPath),
            ["STEAM_COMPAT_SHADER_PATH"] = shaderRoot,
            ["WINEPREFIX"] = winePrefix,
            ["GAME_NAME"] = game.Id,
            ["VKD3D_SHADER_CACHE_PATH"] = Path.Combine(shaderRoot, "VKD3D_shader_cache"),
            ["DXVK_STATE_CACHE_PATH"] = Path.Combine(shaderRoot, "DXVK_state_cache"),
            ["AMD_VK_PIPELINE_CACHE_PATH"] = Path.Combine(shaderRoot, "AMDv1"),
            ["AMD_VK_PIPELINE_CACHE_FILENAME"] = "steamapp_shader_cache",
            ["AMD_VK_USE_PIPELINE_CACHE"] = "1",
            ["MESA_SHADER_CACHE_DIR"] = shaderRoot,
            ["MESA_GLSL_CACHE_DIR"] = shaderRoot,
            ["MESA_SHADER_CACHE_MAX_SIZE"] = settings.MesaMaxCacheSize,
            ["MESA_GLSL_CACHE_MAX_SIZE"] = settings.MesaMaxCacheSize,
            ["__GL_SHADER_DISK_CACHE"] = "1",
            ["__GL_SHADER_DISK_CACHE_PATH"] = glBase,
            ["__GL_SHADER_DISK_CACHE_APP_NAME"] = "steamapp_shader_cache",
            ["__GL_SHADER_DISK_CACHE_SIZE"] = maxCacheBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["__GL_SHADER_DISK_CACHE_SKIP_CLEANUP"] = "1"
        };

        string? roNames = DiscoverManualNvidiaReadOnlyNames(glBase, shaderRoot, game.Id);
        if (!string.IsNullOrEmpty(roNames))
        {
            env["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = roNames;
        }

        bool recorderActive = game.RecorderInstalled;
        if (!recorderActive)
        {
            try { recorderActive = RecorderManager.IsInstalled(game); } catch { }
        }

        if (recorderActive)
        {
            string? existingOverrides = Environment.GetEnvironmentVariable("WINEDLLOVERRIDES");
            env["WINEDLLOVERRIDES"] = string.IsNullOrWhiteSpace(existingOverrides)
                ? "d3d12=n,b;dxgi=n,b"
                : $"d3d12=n,b;dxgi=n,b;{existingOverrides}";
        }

        string? workingDir = !string.IsNullOrWhiteSpace(game.ExecutablePath)
            ? Path.GetDirectoryName(game.ExecutablePath)
            : game.InstallPath;
        if (string.IsNullOrWhiteSpace(workingDir))
            workingDir = null;

        if (!string.IsNullOrWhiteSpace(game.LaunchCommand))
        {
            string customCmd = game.LaunchCommand.Trim();
            var shellArgs = new[] { "-c", customCmd };
            return new GameLaunchSpec("/bin/sh", shellArgs, env, workingDir, customCmd);
        }

        if (string.IsNullOrWhiteSpace(game.ExecutablePath))
            throw new InvalidOperationException($"Manual game '{game.Name}' has no executable path or custom launch command configured.");

        string? runner = !string.IsNullOrWhiteSpace(game.LauncherRunner)
            ? game.LauncherRunner.Trim()
            : ProtonRunner.FindProtonBinaryForGame(game);

        if (string.IsNullOrWhiteSpace(runner))
        {
            if (hasCommand("wine"))
                runner = "wine";
            else
                throw new InvalidOperationException($"Cannot launch '{game.Name}': no Proton or Wine runner was found on this system.");
        }

        bool isProtonScript = Path.GetFileName(runner).Equals("proton", StringComparison.OrdinalIgnoreCase);
        if (isProtonScript)
        {
            if (hasCommand("python3"))
            {
                var args = new[] { runner, "waitforexitandrun", game.ExecutablePath };
                return new GameLaunchSpec("python3", args, env, workingDir, FormatDisplayCommand("python3", args));
            }
            else
            {
                var args = new[] { "waitforexitandrun", game.ExecutablePath };
                return new GameLaunchSpec(runner, args, env, workingDir, FormatDisplayCommand(runner, args));
            }
        }

        var wineArgs = new[] { game.ExecutablePath };
        return new GameLaunchSpec(runner, wineArgs, env, workingDir, FormatDisplayCommand(runner, wineArgs));
    }

    private static string? DiscoverManualNvidiaReadOnlyNames(string glBase, string shaderRoot, string gameId)
    {
        var roots = new[]
        {
            Path.Combine(glBase, "GLCache"),
            Path.Combine(shaderRoot, "nvidiav1", "GLCache")
        }.Distinct(StringComparer.Ordinal);

        string sanitizedTag = SanitizeTag(gameId);
        string taggedPrefix = $"shaderless_g{sanitizedTag}_part";
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var toc in Directory.EnumerateFiles(root, "*.toc", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                {
                    string stem = Path.GetFileNameWithoutExtension(toc);
                    if (stem.StartsWith(taggedPrefix, StringComparison.Ordinal) ||
                        stem.StartsWith("shaderless_part", StringComparison.Ordinal) ||
                        stem.Equals("steam_shader_cache", StringComparison.Ordinal))
                    {
                        if (seen.Add(stem))
                            names.Add(stem);
                    }
                }
            }
            catch { }
        }

        return names.Count > 0 ? string.Join(';', names) : null;
    }

    private static string SanitizeTag(string gameId)
    {
        var sb = new System.Text.StringBuilder(gameId.Length);
        foreach (char c in gameId)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        }
        return sb.Length > 0 ? sb.ToString() : "default";
    }

    private static void PrepareManualRuntimeEnvironment(GameInfo game, GameLaunchSpec spec)
    {
        try
        {
            if (spec.EnvironmentVariables != null)
            {
                if (spec.EnvironmentVariables.TryGetValue("STEAM_COMPAT_DATA_PATH", out var compatData) &&
                    !string.IsNullOrWhiteSpace(compatData))
                {
                    Directory.CreateDirectory(compatData);
                    string? protonBin = null;
                    if (spec.Arguments.Count >= 2 &&
                        Path.GetFileName(spec.Arguments[0]).Equals("proton", StringComparison.OrdinalIgnoreCase))
                    {
                        protonBin = spec.Arguments[0];
                    }
                    else if (Path.GetFileName(spec.FileName).Equals("proton", StringComparison.OrdinalIgnoreCase))
                    {
                        protonBin = spec.FileName;
                    }
                    ProtonRunner.EnsureProtonLocalShaderCacheSetting(compatData, protonBin);
                }

                if (spec.EnvironmentVariables.TryGetValue("STEAM_COMPAT_SHADER_PATH", out var shaderRoot) &&
                    !string.IsNullOrWhiteSpace(shaderRoot))
                {
                    foreach (var sub in new[] { "nvidiav1", "VKD3D_shader_cache", "DXVK_state_cache", "AMDv1", "mesa" })
                    {
                        Directory.CreateDirectory(Path.Combine(shaderRoot, sub));
                    }
                }
            }
        }
        catch { }
    }

    private static (bool Ok, string? Error) StartDetachedProcess(GameLaunchSpec spec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = spec.FileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory) && Directory.Exists(spec.WorkingDirectory))
            {
                psi.WorkingDirectory = spec.WorkingDirectory;
            }

            foreach (var arg in spec.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            if (spec.EnvironmentVariables != null)
            {
                foreach (var kv in spec.EnvironmentVariables)
                {
                    psi.Environment[kv.Key] = kv.Value;
                }
            }

            using var proc = Process.Start(psi);
            if (proc == null)
                return (false, "Operating system returned no process handle.");

            try { proc.StandardInput.Close(); } catch { }

            if (proc.WaitForExit(200) && proc.ExitCode != 0)
            {
                return (false, $"Launcher command exited immediately with code {proc.ExitCode}.");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static bool HasCommandInPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command);

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return false;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), command);
                if (File.Exists(candidate))
                    return true;
            }
            catch { }
        }

        return false;
    }

    private static string FormatDisplayCommand(string fileName, IReadOnlyList<string> args)
    {
        if (args.Count == 0) return fileName;
        var parts = new string[args.Count + 1];
        parts[0] = QuoteIfNeeded(fileName);
        for (int i = 0; i < args.Count; i++)
            parts[i + 1] = QuoteIfNeeded(args[i]);
        return string.Join(' ', parts);
    }

    private static string QuoteIfNeeded(string arg) =>
        arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;
}
