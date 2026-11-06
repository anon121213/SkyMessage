using SkyMessage.Client.Core.Services;
using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Features.Passcode;

/// <summary>Texts shared by the lock screen, the passcode dialog and the passcode settings.</summary>
public static class PasscodeUi
{
    /// <summary>"0:28", "4:00", "1:00:00" — rounded up, so a pause never reads 0:00 while it still runs.</summary>
    public static string Wait(TimeSpan left)
    {
        var span = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, left.TotalSeconds)));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    /// <summary>The line under the title after a wrong code; null when a pause started (its countdown takes the line).</summary>
    public static string? WrongHint(ILocalizer l, PasscodeCheck check) =>
        check.RetryIn > TimeSpan.Zero ? null
        : check.AttemptsLeft is > 0 and <= 2 ? l.Plural("passcode.wrong.left", check.AttemptsLeft)
        : l["passcode.wrong"];

    /// <summary>"Immediately", "After 1 minute"… (<paramref name="brief"/>: "1 min" for the settings row).</summary>
    public static string AutoLockName(ILocalizer l, TimeSpan delay, bool brief = false)
    {
        var key = delay switch
        {
            _ when delay <= TimeSpan.Zero => "immediately",
            _ when delay < TimeSpan.FromMinutes(5) => "minute",
            _ when delay < TimeSpan.FromHours(1) => "fiveMinutes",
            _ when delay < TimeSpan.FromHours(5) => "hour",
            _ => "fiveHours",
        };
        return l[brief ? $"passcode.autoLock.{key}.short" : $"passcode.autoLock.{key}"];
    }
}
