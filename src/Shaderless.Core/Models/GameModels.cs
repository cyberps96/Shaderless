namespace Shaderless.Core.Models;

public enum GameSource
{
    Steam,
    Heroic,
    Lutris,
    Bottles,
    Manual
}

public enum GameEngine
{
    Unknown,
    UnrealEngine4,
    UnrealEngine5,
    Unity,
    RedEngine,
    FromSoftware,
    ReEngine,
    Northlight,
    DirectX12,
    DirectX11,
    Dagor
}

public enum CacheState
{
    NeedsCompilation,
    Cached,
    Outdated,
    Compiling
}

public record GameInfo(
    string Id,
    string Name,
    GameSource Source,
    string InstallPath,
    string ExecutablePath,
    string? PrefixPath,
    string? BannerPath,
    GameEngine Engine,
    CacheState State,
    int TotalShadersEstimated,
    int CompiledShadersCount,
    DateTime? LastCompiledAt,
    long CacheSizeBytes = 0,
    int CachedFilesCount = 0,
    bool RecorderInstalled = false,
    int RecordedPsos = 0,
    string? GlCachePath = null,   // Lutris: the folder its __GL_SHADER_DISK_CACHE_PATH points to for this game (the driver adds /GLCache)
    bool IsFlatpak = false,
    string? StaleReason = null,
    int NewRecordedPsos = 0,
    string? CompileNote = null,          // e.g. a DX12 compile made without a recording: "Partial: ... ● Record ... Re-Compile"
    bool NeedsRecordingFirst = false,    // true when 0 pipelines can be compiled from static files and no usable recording exists yet
    string? ReadinessNote = null,        // scan-time readiness note on uncompiled cards
    string? LauncherRunner = null,       // Heroic store runner ("legendary", "sideload", "gog", "nile"), Lutris slug, or Manual runner path
    string? LaunchCommand = null,        // Manual game optional custom shell launch command (B4)
    bool IsRunning = false               // true when the game's executable is currently running in /proc
)
{
    public string RawId => Id.StartsWith("flatpak_", StringComparison.OrdinalIgnoreCase) ? Id["flatpak_".Length..] : Id;
    public string SourceLabel => IsFlatpak ? $"{Source} (Flatpak)" : Source.ToString();
}

