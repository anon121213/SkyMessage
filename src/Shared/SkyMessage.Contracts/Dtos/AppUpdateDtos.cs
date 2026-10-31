namespace SkyMessage.Contracts;

/// <summary>
/// GET api/app/version (anonymous): the newest builds this deployment offers. Android apps download, verify and
/// install <see cref="Android"/> themselves; open web clients reload when <see cref="Web"/> is not the version they run.
/// </summary>
public sealed record AppVersionDto
{
    /// <summary>The latest Android build, null when none is published (CI writes it into the updates directory).</summary>
    public AndroidReleaseDto? Android { get; init; }
    /// <summary>The web client this server serves, null when it serves none.</summary>
    public WebReleaseDto? Web { get; init; }
    /// <summary>The server's own version (same format as <see cref="ServerInfoDto.Version"/>).</summary>
    public string ServerVersion { get; init; } = "";
}

public sealed record AndroidReleaseDto
{
    /// <summary>Android versionCode; grows with every release. Apps update when it is above their own.</summary>
    public int VersionCode { get; init; }
    /// <summary>Shown to people ("0.2.57").</summary>
    public string VersionName { get; init; } = "";
    /// <summary>
    /// Download path relative to the server base URL (<c>updates/SkyMessage-0.2.57.apk</c>), so it works through every
    /// address of the deployment; served with HTTP Range support (resumable downloads).
    /// </summary>
    public string ApkUrl { get; init; } = "";
    /// <summary>SHA-256 of the APK, 64 lowercase hex characters. Apps install only a package that matches it.</summary>
    public string Sha256 { get; init; } = "";
    /// <summary>Exact size of the APK in bytes.</summary>
    public long Size { get; init; }
    /// <summary>Release notes (plain text), optional.</summary>
    public string? Notes { get; init; }
    /// <summary>Installed builds below this versionCode must update (the server no longer supports them); 0 = none.</summary>
    public int MinVersionCode { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed record WebReleaseDto
{
    /// <summary>Version of the web client the server serves, comparable with the running client's own version.</summary>
    public string Version { get; init; } = "";
}
