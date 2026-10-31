using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>Where the self-update stands (Settings › About, the update card on the chat list).</summary>
public enum AppUpdatePhase
{
    /// <summary>Nothing known yet (no check finished since the start).</summary>
    Idle,
    /// <summary>The app runs the newest build, or this platform has nothing to install.</summary>
    UpToDate,
    /// <summary>A newer build exists but is not downloaded: auto-update is off, it waits for Wi-Fi or for a retry.</summary>
    Available,
    Downloading,
    /// <summary>Downloaded and verified. Installs by itself while the app is in the background, or when the user taps Update.</summary>
    Ready,
    /// <summary>Handed to Android's installer (its confirmation may be on screen); the app restarts when it is done.</summary>
    Installing,
    /// <summary>The last attempt failed (<see cref="AppUpdateStatus.Error"/>); automatic attempts continue later.</summary>
    Failed,
}

/// <summary>Immutable snapshot of the update state.</summary>
public sealed record AppUpdateStatus
{
    public AppUpdatePhase Phase { get; init; } = AppUpdatePhase.Idle;
    /// <summary>A check with the server is running.</summary>
    public bool IsChecking { get; init; }
    /// <summary>The newest Android build when it is newer than the installed one.</summary>
    public AndroidReleaseDto? Release { get; init; }
    /// <summary>0..1 while downloading.</summary>
    public double Progress { get; init; }
    /// <summary>Why the last attempt failed (<see cref="AppUpdateErrors"/> or <see cref="ClientErrorCodes"/>).</summary>
    public string? Error { get; init; }
    /// <summary>The installed build is below the server's minimum: this update is not optional.</summary>
    public bool Required { get; init; }
    /// <summary>Android: SkyMessage may not install apps yet (the user allows it once in the system settings).</summary>
    public bool NeedsInstallPermission { get; init; }
    /// <summary>The download waits for Wi-Fi ("Wi-Fi only" while on mobile data).</summary>
    public bool WaitingForWifi { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    /// <summary>Web: the server now serves this other web client version; a reload picks it up.</summary>
    public string? NewWebVersion { get; init; }
}

public static class AppUpdateErrors
{
    /// <summary>The download does not match the announced size or SHA-256 (it is deleted and fetched again).</summary>
    public const string Integrity = "update_integrity";
    /// <summary>The package could not be written (storage full?).</summary>
    public const string Storage = "update_storage";
    /// <summary>Android refused the package: signed with another key, or an older build (a manual reinstall is needed).</summary>
    public const string Conflict = "update_conflict";
    /// <summary>The package does not fit this device (Android version, CPU).</summary>
    public const string Incompatible = "update_incompatible";
    /// <summary>A policy (device admin, Play Protect) blocked the install.</summary>
    public const string Blocked = "update_blocked";
    /// <summary>Android reported a corrupt package; it is fetched again.</summary>
    public const string Invalid = "update_invalid";
    /// <summary>Any other installer failure.</summary>
    public const string InstallFailed = "update_install_failed";
}

/// <summary>
/// Keeps the app current with the server's releases (GET api/app/version). Android: checks on start, on resume after
/// an hour, after reconnects (a deploy restarts the server) and every few hours; downloads in the background
/// (resumable, "Wi-Fi only" honoured), verifies size and SHA-256 and installs through <see cref="Platform.IAppUpdateInstaller"/>
/// — silently while the app is in the background where Android allows it, otherwise when the user taps Update.
/// Web: notices when the server serves another web client and asks for a reload (<see cref="ReloadRequested"/>) while
/// the page is hidden and no call runs; the UI offers "Update" meanwhile. Events may fire on any thread.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>This app downloads and installs its own updates (Android).</summary>
    bool CanSelfUpdate { get; }
    AppUpdateStatus Status { get; }
    event Action? Changed;
    /// <summary>Web: reload the page now to run the new web client (the UI performs the reload).</summary>
    event Action? ReloadRequested;

    /// <summary>
    /// "Check for updates": asks the server now and lets a failed attempt retry at once. A newer Android build it finds
    /// downloads only as the settings allow ("Update automatically", "Wi-Fi only"); Download overrides them.
    /// </summary>
    Task CheckNowAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Downloads the known newer build now (the user tapped Download): ignores "Wi-Fi only", "Update automatically" and
    /// the back-off, for this build only.
    /// </summary>
    Task DownloadNowAsync();
    /// <summary>Installs the downloaded build now (the user tapped Update). Ask for the install permission first when needed.</summary>
    Task InstallNowAsync();
    /// <summary>Opens the system screen to allow installs (after our explainer); continues the install once allowed.</summary>
    Task<bool> RequestInstallPermissionAsync();
    /// <summary>Web: the user tapped Update — reload now.</summary>
    void ReloadWebNow();
}
