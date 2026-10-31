namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Compares the app's version with <see cref="SkyMessage.Contracts.ServerInfoDto.MinClientVersion"/> so outdated
/// apps can ask the user to update (the web client is always served by the server itself, so it never is).
/// </summary>
public static class AppVersionCheck
{
    /// <summary>True only when both versions parse and the app is older than the minimum.</summary>
    public static bool IsOutdated(string? appVersion, string? minimumVersion) =>
        TryParse(minimumVersion, out var minimum) && TryParse(appVersion, out var app) && app < minimum;

    /// <summary>"1.2", "v1.2.3", "1.2.3-beta+abc" → comparable 4-part version.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) core = core[..cut];
        if (int.TryParse(core, out var major) && major >= 0)
        {
            version = new Version(major, 0, 0, 0);
            return true;
        }
        if (!Version.TryParse(core, out var parsed)) return false;
        // Missing parts count as 0, so "1.2" equals "1.2.0".
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }
}
