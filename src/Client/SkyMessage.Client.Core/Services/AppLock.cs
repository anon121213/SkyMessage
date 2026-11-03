using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Client.Core.Services;

/// <summary>Outcome of a passcode check (<see cref="IAppLockService"/>).</summary>
public enum PasscodeCheckStatus
{
    /// <summary>The right passcode: the count of wrong attempts starts over.</summary>
    Correct,
    /// <summary>
    /// A wrong passcode. <see cref="PasscodeCheck.AttemptsLeft"/> more are allowed before a pause; from the fifth wrong one
    /// in a row on, <see cref="PasscodeCheck.RetryIn"/> is the pause that starts now.
    /// </summary>
    Wrong,
    /// <summary>Still in a pause after wrong attempts: the passcode was not checked (<see cref="PasscodeCheck.RetryIn"/> left).</summary>
    Paused,
    /// <summary>No passcode is set.</summary>
    NotSet,
}

public sealed record PasscodeCheck(PasscodeCheckStatus Status, int AttemptsLeft = 0, TimeSpan RetryIn = default)
{
    public bool IsCorrect => Status == PasscodeCheckStatus.Correct;
}

/// <summary>The passcode rules: lengths, the pause after wrong attempts and the auto-lock policy. Pure functions.</summary>
public static class AppLockRules
{
    public const int ShortLength = 4;
    public const int LongLength = 6;
    /// <summary>Wrong passcodes in a row allowed before the first pause.</summary>
    public const int FreeAttempts = 5;
    public static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LongestPause = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultAutoLock = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A trip the app itself started (a picker, the camera, a system settings screen it comes back from) counts as leaving
    /// only after this long, or after the auto-lock delay when that is longer: attaching a photo does not lock an app set
    /// to lock "immediately". A user who goes elsewhere from the picker is still locked out after this time.
    /// </summary>
    public static readonly TimeSpan OwnTripGrace = TimeSpan.FromMinutes(2);

    /// <summary>Wall-clock steps back smaller than this (time sync) are not taken for the clock being moved back.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(1);

    /// <summary>Auto-lock choices offered in settings: immediately, 1 min, 5 min, 1 h, 5 h.</summary>
    public static IReadOnlyList<TimeSpan> AutoLockChoices { get; } =
    [
        TimeSpan.Zero,
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(5),
    ];

    public static bool IsValidLength(int length) => length is ShortLength or LongLength;

    /// <summary>Exactly 4 or 6 ASCII digits.</summary>
    public static bool IsValidPasscode(string? passcode) =>
        passcode is { Length: ShortLength or LongLength } && passcode.All(char.IsAsciiDigit);

    /// <summary>One of <see cref="AutoLockChoices"/>; anything else falls back to <see cref="DefaultAutoLock"/>.</summary>
    public static TimeSpan NormalizeAutoLock(TimeSpan delay) => AutoLockChoices.Contains(delay) ? delay : DefaultAutoLock;

    /// <summary>
    /// The pause after <paramref name="failures"/> wrong passcodes in a row: none for the first four, 30 s after the fifth,
    /// doubling with every further one (1 min, 2 min, 4 min…), at most an hour.
    /// </summary>
    public static TimeSpan PauseAfter(int failures)
    {
        if (failures < FreeAttempts) return TimeSpan.Zero;
        var doublings = Math.Min(failures - FreeAttempts, 16);
        var pause = TimeSpan.FromTicks(FirstPause.Ticks << doublings);
        return pause < LongestPause ? pause : LongestPause;
    }

    /// <summary>Wrong attempts still allowed before the first pause.</summary>
    public static int AttemptsLeft(int failures) => Math.Max(0, FreeAttempts - failures);

    /// <summary>
    /// Whether the app must ask for the passcode after being away (in the background). <paramref name="wallAway"/> is
    /// measured with the wall clock, which keeps running while the phone sleeps but can be moved by the user;
    /// <paramref name="steadyAway"/> with the process clock, which cannot be moved but stops while the phone sleeps;
    /// <paramref name="bootAway"/> with the boot clock (<see cref="IAppLockPlatform.SinceBoot"/>), which has neither flaw,
    /// when the platform has one. The longest reading counts; without a boot clock a wall clock moved back (beyond time
    /// sync corrections) locks, because how long the app was away cannot be told any more.
    /// </summary>
    public static bool ShouldLockAfterAway(TimeSpan delay, TimeSpan wallAway, TimeSpan steadyAway, TimeSpan? bootAway)
    {
        if (delay <= TimeSpan.Zero) return true;
        if (bootAway is { } boot && boot >= TimeSpan.Zero) return Max(Max(boot, steadyAway), wallAway) >= delay;
        if (wallAway < -ClockTolerance) return true;
        return Max(steadyAway, wallAway) >= delay;
    }

    /// <summary>
    /// What is left of a <paramref name="pause"/> that started <paramref name="wallServed"/> ago by the wall clock
    /// (<paramref name="steadyServed"/> by the process clock when it started in this run, <paramref name="bootServed"/> by
    /// the boot clock when the device has not restarted since). The boot clock is trusted when known: neither moving the
    /// wall clock nor restarting the app shortens the pause then; a wall clock moved back never makes it longer than it is.
    /// </summary>
    public static TimeSpan PauseRemaining(TimeSpan pause, TimeSpan wallServed, TimeSpan? steadyServed, TimeSpan? bootServed)
    {
        if (pause <= TimeSpan.Zero) return TimeSpan.Zero;
        var served = bootServed is { } boot && boot >= TimeSpan.Zero
            ? boot
            : Max(wallServed < TimeSpan.Zero ? TimeSpan.Zero : wallServed, steadyServed ?? TimeSpan.Zero);
        var left = pause - served;
        if (left <= TimeSpan.Zero) return TimeSpan.Zero;
        return left > pause ? pause : left;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>
/// The app passcode ("код-пароль"): a 4- or 6-digit code for this device and the whole app. Only a salted PBKDF2 hash is
/// kept, in <see cref="ISecureStore"/> (Android Keystore; browsers: localStorage). With a passcode the app starts locked,
/// locks itself when it was away longer than <see cref="AutoLockDelay"/> (or as it leaves, for "immediately") and on
/// <see cref="LockNow"/>. While locked the UI shows only the lock screen (an active call stays reachable), the window is
/// kept out of screenshots (Android) and notifications can hide their text; while a passcode is set the recent-apps
/// switcher shows no content. Five wrong passcodes in a row pause the checks for 30 s, every further one doubles the
/// pause (up to an hour); the count survives restarts and a right passcode (or fingerprint) starts it over. Signing out of
/// this device removes the passcode ("Forgot the passcode?" signs out). Thread-safe; <see cref="Changed"/> fires on any thread.
/// </summary>
public interface IAppLockService
{
    /// <summary>A passcode is set on this device.</summary>
    bool IsEnabled { get; }
    /// <summary>The app content is behind the lock screen until the passcode or a fingerprint unlocks it.</summary>
    bool IsLocked { get; }
    /// <summary>Digits of the passcode: 4 or 6 (0 without one).</summary>
    int Length { get; }
    /// <summary>How long the app may stay away before it asks again (zero: as soon as it leaves).</summary>
    TimeSpan AutoLockDelay { get; }
    /// <summary>The user allowed fingerprint/face unlock (offered only while <see cref="CanUseBiometrics"/>).</summary>
    bool UseBiometrics { get; }
    /// <summary>While locked, notifications show "New message" instead of who wrote what.</summary>
    bool HideNotificationText { get; }
    /// <summary>The platform has biometric unlock at all (the setting is hidden otherwise).</summary>
    bool SupportsBiometrics { get; }
    /// <summary>A fingerprint or face is enrolled on the device right now.</summary>
    bool CanUseBiometrics { get; }
    /// <summary>Wrong passcodes in a row (0 after a right one).</summary>
    int FailedAttempts { get; }
    /// <summary>How long new attempts are refused after wrong ones (zero when one may try now).</summary>
    TimeSpan RetryIn { get; }
    /// <summary>Grows every time the app locks (the lock screen offers the fingerprint once per lock).</summary>
    int LockCount { get; }
    event Action? Changed;

    /// <summary>Checks the passcode and unlocks the app when it is right (counted like every check).</summary>
    Task<PasscodeCheck> UnlockAsync(string passcode);
    /// <summary>Asks for a fingerprint/face (when allowed and possible) and unlocks the app on success.</summary>
    Task<BiometricOutcome> UnlockWithBiometricsAsync(BiometricPromptText text, CancellationToken cancellationToken = default);
    /// <summary>Checks the passcode without unlocking (settings flows); wrong ones count toward the pause too.</summary>
    Task<PasscodeCheck> VerifyAsync(string passcode);
    /// <summary>Sets the first passcode (4 or 6 digits) with default options; the app stays unlocked.</summary>
    Task EnableAsync(string passcode);
    /// <summary>Replaces the passcode after checking the current one; the options stay.</summary>
    Task<PasscodeCheck> ChangeAsync(string currentPasscode, string newPasscode);
    /// <summary>Removes the passcode after checking it.</summary>
    Task<PasscodeCheck> DisableAsync(string currentPasscode);
    /// <summary>One of <see cref="AppLockRules.AutoLockChoices"/>.</summary>
    Task SetAutoLockDelayAsync(TimeSpan delay);
    Task SetUseBiometricsAsync(bool enabled);
    Task SetHideNotificationTextAsync(bool hide);
    /// <summary>Locks the app right away ("Lock now"); nothing happens without a passcode.</summary>
    void LockNow();
    /// <summary>
    /// The app is about to open a picker, the camera or a system screen that returns to it: until the handle is disposed,
    /// leaving for it locks only after <see cref="AppLockRules.OwnTripGrace"/> (or the delay, when longer).
    /// </summary>
    IDisposable ExpectReturn();
    /// <summary>
    /// Notifications must hide their text now: the option is on and the app is locked (the lock engages here when its
    /// delay passed while the app was away).
    /// </summary>
    bool ShouldHideNotificationText();
}
