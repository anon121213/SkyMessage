using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>The passcode and its options as stored ("passcode.v1" in the secure store).</summary>
internal sealed record PasscodeConfig
{
    public int Version { get; init; } = 1;
    public int Length { get; init; } = AppLockRules.ShortLength;
    /// <summary>Null when the stored record could not be read: no passcode matches it, only signing out removes it.</summary>
    public PasscodeHash? Hash { get; init; }
    public int AutoLockSeconds { get; init; } = (int)AppLockRules.DefaultAutoLock.TotalSeconds;
    public bool UseBiometrics { get; init; }
    public bool HideNotificationText { get; init; }

    [JsonIgnore]
    public TimeSpan AutoLock => AppLockRules.NormalizeAutoLock(TimeSpan.FromSeconds(AutoLockSeconds));
}

/// <summary>Wrong passcodes in a row and when the current pause began ("passcode.attempts.v1").</summary>
internal sealed record PasscodeAttempts
{
    public static PasscodeAttempts None { get; } = new();

    public int Failures { get; init; }
    public DateTimeOffset? PausedAt { get; init; }
    /// <summary>The boot clock when the pause began, in ms (null where the platform has none).</summary>
    public long? PausedAtBootMs { get; init; }
    /// <summary>The process clock when the pause began in this run (not stored).</summary>
    [JsonIgnore]
    public long? PausedAtSteady { get; init; }
}

/// <summary>
/// <see cref="IAppLockService"/>. The lock state lives here (a Core singleton), so a rebuilt WebView or a recreated
/// activity finds the app still locked. Every check, the attempt counter and the stored records go through one queue;
/// the in-memory state is guarded by a lock so the UI and the lifecycle callbacks read it synchronously. Leaving the
/// app starts a check every few seconds while it is away and unlocked: the lock engages (and the UI renders the lock
/// screen) before the user is back, so returning never shows the chats for a moment first.
/// </summary>
internal sealed class AppLockService : IAppLockService, IAccountScoped, IDisposable
{
    public const string ConfigKey = "passcode.v1";
    public const string AttemptsKey = "passcode.attempts.v1";

    /// <summary>How often the delay is checked while the app is away and still unlocked.</summary>
    internal static readonly TimeSpan AwayCheckInterval = TimeSpan.FromSeconds(5);

    private readonly ISecureStore _store;
    private readonly IAppLifecycle _lifecycle;
    private readonly IAppLockPlatform _platform;
    private readonly IScreenCaptureGuard _capture;
    private readonly IAuthService _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<AppLockService> _logger;
    private readonly SerialQueue _queue = new();
    private readonly object _gate = new();

    private PasscodeConfig? _config;
    private PasscodeAttempts _attempts = PasscodeAttempts.None;
    private bool _locked;
    private int _lockCount;
    private IDisposable? _screenGuard;
    private Away? _away;
    private ITimer? _awayTimer;
    private int _trips;
    private bool _initialized;

    public AppLockService(
        ISecureStore store,
        IAppLifecycle lifecycle,
        IAppLockPlatform platform,
        IScreenCaptureGuard capture,
        IAuthService auth,
        AccountLifecycle accounts,
        TimeProvider time,
        ILogger<AppLockService> logger)
    {
        _store = store;
        _lifecycle = lifecycle;
        _platform = platform;
        _capture = capture;
        _auth = auth;
        _time = time;
        _logger = logger;
        lifecycle.ForegroundChanged += OnForegroundChanged;
        accounts.Register(this);
    }

    /// <summary>Rounds for new hashes; null calibrates them on the device (tests use a few to stay fast).</summary>
    internal int? FixedIterations { get; set; }

    public event Action? Changed;

    public bool IsEnabled
    {
        get
        {
            lock (_gate) return _config is not null;
        }
    }

    public bool IsLocked
    {
        get
        {
            lock (_gate) return _locked;
        }
    }

    public int Length
    {
        get
        {
            lock (_gate) return _config?.Length ?? 0;
        }
    }

    public TimeSpan AutoLockDelay
    {
        get
        {
            lock (_gate) return _config?.AutoLock ?? AppLockRules.DefaultAutoLock;
        }
    }

    public bool UseBiometrics
    {
        get
        {
            lock (_gate) return _config?.UseBiometrics == true;
        }
    }

    public bool HideNotificationText
    {
        get
        {
            lock (_gate) return _config?.HideNotificationText == true;
        }
    }

    public bool SupportsBiometrics => _platform.SupportsBiometrics;

    public bool CanUseBiometrics => _platform.SupportsBiometrics && _platform.CanUseBiometrics;

    public int FailedAttempts
    {
        get
        {
            lock (_gate) return _attempts.Failures;
        }
    }

    public TimeSpan RetryIn
    {
        get
        {
            lock (_gate) return RetryInCore();
        }
    }

    public int LockCount
    {
        get
        {
            lock (_gate) return _lockCount;
        }
    }

    /// <summary>
    /// Reads the stored passcode before any screen shows: with one the app starts locked. A passcode left behind by a
    /// sign-out that did not finish is removed. Idempotent, never throws.
    /// </summary>
    public async Task InitializeAsync()
    {
        var hidden = false;
        var changed = await _queue.RunAsync(async () =>
        {
            if (_initialized) return false;
            _initialized = true;
            var config = await ReadConfigAsync().ConfigureAwait(false);
            var attempts = config is null ? PasscodeAttempts.None : await ReadAttemptsAsync().ConfigureAwait(false);
            if (config is not null && _auth.State == AuthState.SignedOut)
            {
                // A passcode guards a signed-in session only.
                await RemoveStoredAsync().ConfigureAwait(false);
                config = null;
                attempts = PasscodeAttempts.None;
            }
            lock (_gate)
            {
                _config = config;
                _attempts = attempts;
                // A cold start always asks.
                if (config is not null) LockCore();
            }
            hidden = config is not null;
            return config is not null;
        }).ConfigureAwait(false);
        if (!changed) return;
        _platform.SetHiddenInRecents(hidden);
        RaiseChanged();
    }

    /// <summary>
    /// Coming back: another instance of the app on this device (a second browser tab) may have turned the passcode on,
    /// off or changed it meanwhile. Turned on elsewhere while this one was away: it locks too. Never throws.
    /// </summary>
    internal async Task RefreshFromStoreAsync()
    {
        bool? enabled = null;
        await _queue.RunAsync(async () =>
        {
            if (!_initialized) return;
            PasscodeConfig? stored;
            try
            {
                stored = await ReadConfigAsync(throwOnFailure: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The passcode could not be re-read; keeping the current one");
                return;
            }
            lock (_gate)
            {
                if (stored == _config) return;
                var wasEnabled = _config is not null;
                _config = stored;
                if (stored is null) ClearCore();
                else if (!wasEnabled) LockCore();
                enabled = stored is not null;
            }
        }).ConfigureAwait(false);
        if (enabled is not { } isEnabled) return;
        _platform.SetHiddenInRecents(isEnabled);
        RaiseChanged();
    }

    public Task<PasscodeCheck> UnlockAsync(string passcode) => CheckAsync(passcode, unlock: true);

    public Task<PasscodeCheck> VerifyAsync(string passcode) => CheckAsync(passcode, unlock: false);

    public async Task<BiometricOutcome> UnlockWithBiometricsAsync(BiometricPromptText text, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_config is not { UseBiometrics: true } || !_locked) return BiometricOutcome.Unavailable;
        }
        if (!CanUseBiometrics) return BiometricOutcome.Unavailable;

        BiometricOutcome outcome;
        try
        {
            outcome = await _platform.AuthenticateAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return BiometricOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The biometric prompt failed");
            return BiometricOutcome.Unavailable;
        }
        if (outcome != BiometricOutcome.Succeeded) return outcome;

        await _queue.RunAsync(async () =>
        {
            lock (_gate)
            {
                _attempts = PasscodeAttempts.None;
                UnlockCore();
            }
            await DeleteAttemptsAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        RaiseChanged();
        return outcome;
    }

    public async Task EnableAsync(string passcode)
    {
        if (!AppLockRules.IsValidPasscode(passcode)) throw new ArgumentException("A passcode is 4 or 6 digits.", nameof(passcode));
        await _queue.RunAsync(async () =>
        {
            lock (_gate)
            {
                if (_config is not null) throw new InvalidOperationException("A passcode is already set; change it instead.");
            }
            if (_auth.State != AuthState.SignedIn) throw new InvalidOperationException("A passcode needs a signed-in session.");
            var hash = await HashAsync(passcode).ConfigureAwait(false);
            var config = new PasscodeConfig { Length = passcode.Length, Hash = hash };
            await WriteConfigAsync(config).ConfigureAwait(false);
            lock (_gate)
            {
                _config = config;
                _attempts = PasscodeAttempts.None;
                UnlockCore();
            }
            await DeleteAttemptsAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        _platform.SetHiddenInRecents(true);
        RaiseChanged();
    }

    public async Task<PasscodeCheck> ChangeAsync(string currentPasscode, string newPasscode)
    {
        if (!AppLockRules.IsValidPasscode(newPasscode)) throw new ArgumentException("A passcode is 4 or 6 digits.", nameof(newPasscode));
        var check = await VerifyAsync(currentPasscode).ConfigureAwait(false);
        if (!check.IsCorrect) return check;
        await _queue.RunAsync(async () =>
        {
            PasscodeConfig? current;
            lock (_gate) current = _config;
            if (current is null) return;
            var hash = await HashAsync(newPasscode).ConfigureAwait(false);
            var next = current with { Length = newPasscode.Length, Hash = hash };
            await WriteConfigAsync(next).ConfigureAwait(false);
            lock (_gate) _config = next;
        }).ConfigureAwait(false);
        RaiseChanged();
        return check;
    }

    public async Task<PasscodeCheck> DisableAsync(string currentPasscode)
    {
        var check = await VerifyAsync(currentPasscode).ConfigureAwait(false);
        if (!check.IsCorrect) return check;
        await _queue.RunAsync(async () =>
        {
            lock (_gate) ClearCore();
            await RemoveStoredAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        _platform.SetHiddenInRecents(false);
        RaiseChanged();
        return check;
    }

    public Task SetAutoLockDelayAsync(TimeSpan delay)
    {
        var normalized = AppLockRules.NormalizeAutoLock(delay);
        return UpdateAsync(c => c with { AutoLockSeconds = (int)normalized.TotalSeconds });
    }

    public Task SetUseBiometricsAsync(bool enabled) => UpdateAsync(c => c with { UseBiometrics = enabled });

    public Task SetHideNotificationTextAsync(bool hide) => UpdateAsync(c => c with { HideNotificationText = hide });

    public void LockNow()
    {
        bool changed;
        lock (_gate) changed = LockCore();
        if (changed) RaiseChanged();
    }

    public IDisposable ExpectReturn()
    {
        Interlocked.Increment(ref _trips);
        return new Trip(this);
    }

    public bool ShouldHideNotificationText()
    {
        CheckAway();
        lock (_gate) return _locked && _config is { HideNotificationText: true };
    }

    Task IAccountScoped.OnSignedInAsync(Guid userId) => Task.CompletedTask;

    /// <summary>Signing out of this device (also "Forgot the passcode?") removes the passcode and its attempt count.</summary>
    async Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        var had = false;
        await _queue.RunAsync(async () =>
        {
            lock (_gate)
            {
                had = _config is not null || _locked;
                ClearCore();
            }
            await RemoveStoredAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (!had) return;
        _platform.SetHiddenInRecents(false);
        RaiseChanged();
    }

    /// <summary>
    /// While away and unlocked: locks once the delay has passed (the away timer calls it every few seconds, the
    /// notification check before deciding what to show).
    /// </summary>
    internal void CheckAway()
    {
        bool changed;
        lock (_gate) changed = _away is { } away && !_locked && _config is not null && AwayTooLong(away) && LockCore();
        if (changed) RaiseChanged();
    }

    private void OnForegroundChanged(bool foreground)
    {
        bool changed;
        lock (_gate) changed = foreground ? OnReturnCore() : OnLeaveCore();
        if (changed) RaiseChanged();
        if (foreground) RefreshFromStoreAsync().Forget(_logger);
    }

    /// <summary>Under the lock. Starts measuring the time away (or locks at once for "immediately").</summary>
    private bool OnLeaveCore()
    {
        // Leaving while locked changes nothing; an unlock while away (a fingerprint prompt shown as a separate screen)
        // must not lock again on the way back either.
        if (_config is not { } config || _locked)
        {
            _away = null;
            StopAwayTimer();
            return false;
        }
        var delay = config.AutoLock;
        if (Volatile.Read(ref _trips) > 0 && delay < AppLockRules.OwnTripGrace) delay = AppLockRules.OwnTripGrace;
        _away = new Away(_time.GetUtcNow(), _time.GetTimestamp(), _platform.SinceBoot, delay);
        if (delay <= TimeSpan.Zero) return LockCore();
        StartAwayTimer();
        return false;
    }

    /// <summary>Under the lock. Locks when the app was away too long.</summary>
    private bool OnReturnCore()
    {
        StopAwayTimer();
        var changed = _away is { } away && !_locked && _config is not null && AwayTooLong(away) && LockCore();
        _away = null;
        return changed;
    }

    private async Task<PasscodeCheck> CheckAsync(string passcode, bool unlock)
    {
        var result = await _queue.RunAsync(async () =>
        {
            PasscodeConfig? config;
            TimeSpan retryIn;
            lock (_gate) config = _config;
            if (config is null) return new PasscodeCheck(PasscodeCheckStatus.NotSet);
            // Wrong attempts made in another instance (a second browser tab) count here too.
            var stored = await ReadAttemptsAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (stored.Failures > _attempts.Failures) _attempts = stored;
                retryIn = RetryInCore();
            }
            if (retryIn > TimeSpan.Zero) return new PasscodeCheck(PasscodeCheckStatus.Paused, 0, retryIn);

            var correct = config.Hash is { } hash && AppLockRules.IsValidPasscode(passcode)
                && await PasscodeHasher.VerifyAsync(passcode, hash, DeriveAsync).ConfigureAwait(false);
            if (correct)
            {
                lock (_gate)
                {
                    _attempts = PasscodeAttempts.None;
                    if (unlock) UnlockCore();
                }
                await DeleteAttemptsAsync().ConfigureAwait(false);
                return new PasscodeCheck(PasscodeCheckStatus.Correct);
            }

            PasscodeAttempts next;
            lock (_gate)
            {
                next = NextFailure(_attempts);
                _attempts = next;
            }
            // Stored before the answer is shown: closing the app does not reset the count.
            await WriteAttemptsAsync(next).ConfigureAwait(false);
            return new PasscodeCheck(PasscodeCheckStatus.Wrong, AppLockRules.AttemptsLeft(next.Failures), AppLockRules.PauseAfter(next.Failures));
        }).ConfigureAwait(false);
        RaiseChanged();
        return result;
    }

    private async Task UpdateAsync(Func<PasscodeConfig, PasscodeConfig> change)
    {
        var updated = await _queue.RunAsync(async () =>
        {
            PasscodeConfig? current;
            lock (_gate) current = _config;
            if (current is null) return false;
            var next = change(current);
            if (next == current) return false;
            await WriteConfigAsync(next).ConfigureAwait(false);
            lock (_gate) _config = next;
            return true;
        }).ConfigureAwait(false);
        if (updated) RaiseChanged();
    }

    private async Task<PasscodeHash> HashAsync(string passcode)
    {
        var iterations = FixedIterations ?? await PasscodeHasher.ChooseIterationsAsync(DeriveAsync).ConfigureAwait(false);
        return await PasscodeHasher.CreateAsync(passcode, iterations, DeriveAsync).ConfigureAwait(false);
    }

    /// <summary>The platform's PBKDF2 when it has a faster one (browsers), else .NET's — the same bytes either way.</summary>
    private async Task<byte[]> DeriveAsync(string passcode, byte[] salt, int iterations, int length)
    {
        try
        {
            if (await _platform.DeriveKeyAsync(passcode, salt, iterations, length).ConfigureAwait(false) is { } fast && fast.Length == length)
                return fast;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The platform key derivation failed; using .NET's");
        }
        return await PasscodeHasher.Managed(passcode, salt, iterations, length).ConfigureAwait(false);
    }

    // ---- state (under _gate) ----

    /// <summary>Locks; false when there is no passcode or the app is locked already.</summary>
    private bool LockCore()
    {
        if (_config is null || _locked) return false;
        _locked = true;
        _lockCount++;
        _away = null;
        StopAwayTimer();
        // Out of screenshots, screen recordings and the recents picture while locked (Android FLAG_SECURE).
        _screenGuard ??= _capture.Protect();
        return true;
    }

    private void UnlockCore()
    {
        _locked = false;
        _away = null;
        StopAwayTimer();
        _screenGuard?.Dispose();
        _screenGuard = null;
    }

    private void ClearCore()
    {
        _config = null;
        _attempts = PasscodeAttempts.None;
        UnlockCore();
    }

    private void StartAwayTimer()
    {
        StopAwayTimer();
        _awayTimer = _time.CreateTimer(_ => CheckAway(), null, AwayCheckInterval, AwayCheckInterval);
    }

    private void StopAwayTimer()
    {
        _awayTimer?.Dispose();
        _awayTimer = null;
    }

    private bool AwayTooLong(Away away)
    {
        var wall = _time.GetUtcNow() - away.Wall;
        var steady = _time.GetElapsedTime(away.Steady);
        TimeSpan? boot = away.Boot is { } since && _platform.SinceBoot is { } now ? now - since : null;
        return AppLockRules.ShouldLockAfterAway(away.Delay, wall, steady, boot);
    }

    private TimeSpan RetryInCore()
    {
        var attempts = _attempts;
        var pause = AppLockRules.PauseAfter(attempts.Failures);
        if (pause <= TimeSpan.Zero || attempts.PausedAt is not { } at) return TimeSpan.Zero;
        var wall = _time.GetUtcNow() - at;
        TimeSpan? steady = attempts.PausedAtSteady is { } started ? _time.GetElapsedTime(started) : null;
        // A boot clock that went back means the device restarted since: the wall clock has to do then.
        TimeSpan? boot = attempts.PausedAtBootMs is { } bootMs && _platform.SinceBoot is { } now
            ? now - TimeSpan.FromMilliseconds(bootMs)
            : null;
        return AppLockRules.PauseRemaining(pause, wall, steady, boot);
    }

    private PasscodeAttempts NextFailure(PasscodeAttempts current)
    {
        var failures = current.Failures + 1;
        if (AppLockRules.PauseAfter(failures) <= TimeSpan.Zero) return new PasscodeAttempts { Failures = failures };
        return new PasscodeAttempts
        {
            Failures = failures,
            PausedAt = _time.GetUtcNow(),
            PausedAtBootMs = _platform.SinceBoot is { } boot ? (long)boot.TotalMilliseconds : null,
            PausedAtSteady = _time.GetTimestamp(),
        };
    }

    // ---- storage ----

    /// <summary>
    /// The stored record; null without one. A storage failure reads as an unverifiable passcode (without the record
    /// there is no telling whether one exists: the safe side), or is rethrown with <paramref name="throwOnFailure"/>.
    /// </summary>
    private async Task<PasscodeConfig?> ReadConfigAsync(bool throwOnFailure = false)
    {
        string? json;
        try
        {
            json = await _store.GetAsync(ConfigKey).ConfigureAwait(false);
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            _logger.LogWarning(ex, "The passcode could not be read");
            return new PasscodeConfig { Hash = null };
        }
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var config = JsonSerializer.Deserialize<PasscodeConfig>(json, SkyJson.Options);
            if (config is { Hash: not null } && AppLockRules.IsValidLength(config.Length)) return config;
        }
        catch (JsonException)
        {
        }
        _logger.LogWarning("The stored passcode is unreadable; signing out of this device removes it");
        return new PasscodeConfig { Hash = null };
    }

    private async Task<PasscodeAttempts> ReadAttemptsAsync()
    {
        try
        {
            var json = await _store.GetAsync(AttemptsKey).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return PasscodeAttempts.None;
            var attempts = JsonSerializer.Deserialize<PasscodeAttempts>(json, SkyJson.Options);
            return attempts is { Failures: > 0 } ? attempts : PasscodeAttempts.None;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The passcode attempt count could not be read");
            return PasscodeAttempts.None;
        }
    }

    /// <summary>Throws when the record cannot be stored: the user must not believe a passcode is set when it is not.</summary>
    private Task WriteConfigAsync(PasscodeConfig config) => _store.SetAsync(ConfigKey, JsonSerializer.Serialize(config, SkyJson.Options));

    private async Task WriteAttemptsAsync(PasscodeAttempts attempts)
    {
        try
        {
            await _store.SetAsync(AttemptsKey, JsonSerializer.Serialize(attempts, SkyJson.Options)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The passcode attempt count could not be stored");
        }
    }

    private async Task DeleteAttemptsAsync()
    {
        try
        {
            await _store.RemoveAsync(AttemptsKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The passcode attempt count could not be removed");
        }
    }

    private async Task RemoveStoredAsync()
    {
        try
        {
            await _store.RemoveAsync(ConfigKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The passcode could not be removed");
        }
        await DeleteAttemptsAsync().ConfigureAwait(false);
    }

    private void RaiseChanged() => EventRaiser.Raise(Changed, _logger);

    public void Dispose()
    {
        _lifecycle.ForegroundChanged -= OnForegroundChanged;
        lock (_gate)
        {
            StopAwayTimer();
            _screenGuard?.Dispose();
            _screenGuard = null;
        }
    }

    /// <summary>The app left (unlocked) at these readings of the three clocks; <see cref="Delay"/> applies to this trip.</summary>
    private sealed record Away(DateTimeOffset Wall, long Steady, TimeSpan? Boot, TimeSpan Delay);

    private sealed class Trip(AppLockService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._trips);
        }
    }
}
