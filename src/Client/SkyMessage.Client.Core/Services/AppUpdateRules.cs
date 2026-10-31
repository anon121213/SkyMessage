using System.Security.Cryptography;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>Why the updater runs. Higher values check the server more eagerly (pending triggers merge to the highest).</summary>
internal enum UpdateTrigger
{
    /// <summary>Network or settings changed, a retry is due, an install finished: continue pending work, check only if never checked.</summary>
    Continue = 0,
    /// <summary>The periodic timer.</summary>
    Periodic = 1,
    /// <summary>The app came back to the foreground.</summary>
    Resume = 2,
    /// <summary>Realtime reconnected after losing its state (the server may have been redeployed).</summary>
    Reconnect = 3,
    /// <summary>App start.</summary>
    Start = 4,
    /// <summary>The user asked ("Check for updates").</summary>
    Manual = 5,
}

/// <summary>The updater's rules as pure functions (version comparison, offer validation, scheduling, integrity).</summary>
internal static class AppUpdateRules
{
    /// <summary>The first check waits for the start-up work (chat list, realtime) to settle.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(8);
    /// <summary>How often the timer wakes up; it checks the server only when <see cref="PeriodicInterval"/> passed.</summary>
    public static readonly TimeSpan TimerPeriod = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(4);
    public static readonly TimeSpan ResumeInterval = TimeSpan.FromHours(1);
    /// <summary>Reconnects are frequent on lossy networks; a deploy is caught by the first one after it anyway.</summary>
    public static readonly TimeSpan ReconnectInterval = TimeSpan.FromMinutes(10);
    /// <summary>The app must stay in the background this long before an update installs silently (quick app switches).</summary>
    public static readonly TimeSpan BackgroundInstallDelay = TimeSpan.FromSeconds(30);
    /// <summary>Web: the page must stay hidden this long before it reloads by itself.</summary>
    public static readonly TimeSpan WebReloadDelay = TimeSpan.FromSeconds(10);
    /// <summary>A committed install that reported nothing for this long is offered again.</summary>
    public static readonly TimeSpan InstallingTimeout = TimeSpan.FromMinutes(10);
    /// <summary>After the user went to allow installs, a restarted app resumes the install within this window.</summary>
    public static readonly TimeSpan InstallRequestWindow = TimeSpan.FromMinutes(15);

    public static bool ShouldCheck(UpdateTrigger trigger, DateTimeOffset? lastCheck, DateTimeOffset now) => trigger switch
    {
        UpdateTrigger.Manual or UpdateTrigger.Start => true,
        UpdateTrigger.Reconnect => Due(lastCheck, now, ReconnectInterval),
        UpdateTrigger.Resume => Due(lastCheck, now, ResumeInterval),
        UpdateTrigger.Periodic => Due(lastCheck, now, PeriodicInterval),
        _ => lastCheck is null,
    };

    /// <summary>Back-off after failed downloads/installs: 1 min, 5 min, 15 min, 1 h, then every 3 h.</summary>
    public static TimeSpan RetryDelay(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(3),
    };

    /// <summary>"Wi-Fi only" allows unmetered Wi-Fi/Ethernet (a phone's hotspot is metered); without it any network goes.</summary>
    public static bool NetworkAllows(bool wifiOnly, NetworkKind kind, bool metered) =>
        !wifiOnly || (kind is NetworkKind.Wifi or NetworkKind.Ethernet && !metered);

    /// <summary>
    /// An offer we can act on: positive versionCode and size, a SHA-256, and a download address on this server (a
    /// relative path) or over https. Anything else is ignored rather than failing later.
    /// </summary>
    public static bool IsValid(AndroidReleaseDto? release)
    {
        if (release is null || release.VersionCode <= 0 || release.Size <= 0) return false;
        if (release.Sha256 is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigit)) return false;
        var url = release.ApkUrl?.Trim();
        if (string.IsNullOrEmpty(url) || url.StartsWith("//", StringComparison.Ordinal) || url.Contains('\\')) return false;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && !url.StartsWith('/'))
            return absolute.Scheme == Uri.UriSchemeHttps;
        return !url.Split('/').Contains("..");
    }

    /// <summary>A valid offer above the installed build.</summary>
    public static bool IsNewer(AndroidReleaseDto? release, int installedVersionCode) =>
        IsValid(release) && release!.VersionCode > installedVersionCode;

    /// <summary>The installed build is below the offer's minimum (the server no longer supports it).</summary>
    public static bool IsRequired(AndroidReleaseDto? release, int installedVersionCode) =>
        release is not null && installedVersionCode < release.MinVersionCode;

    /// <summary>The server's web client when it is not the one running (any difference: a rollback counts too), else null.</summary>
    public static string? NewWebVersion(string? served, string? running)
    {
        var server = served?.Trim();
        if (string.IsNullOrEmpty(server)) return null;
        return string.Equals(server, running?.Trim(), StringComparison.OrdinalIgnoreCase) ? null : server;
    }

    /// <summary>Background installs need: auto-update on, the app out of sight, no call, and Android expected to allow a silent install.</summary>
    public static bool MayInstallInBackground(bool autoUpdate, bool foreground, bool inCall, bool canInstallSilently, bool confirmationNeeded) =>
        autoUpdate && !foreground && !inCall && canInstallSilently && !confirmationNeeded;

    public static string PackageFileName(AndroidReleaseDto release) =>
        $"skymessage-{release.VersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}.apk";

    /// <summary>True when <paramref name="package"/> has exactly <paramref name="size"/> bytes with the given SHA-256 (hex, any case).</summary>
    public static async Task<bool> VerifyAsync(Stream package, long size, string sha256, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await package.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > size) return false;
            hash.AppendData(buffer, 0, read);
        }
        if (total != size) return false;
        return string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool Due(DateTimeOffset? last, DateTimeOffset now, TimeSpan interval) =>
        last is not { } at || now - at >= interval || now < at;
}
