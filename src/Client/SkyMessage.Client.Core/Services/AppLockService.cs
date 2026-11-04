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
    /// <summary>A record that is there but cannot be read (or is gone while the marker says one was set).</summary>
    public static PasscodeConfig Unreadable { get; } = new() { Hash = null };

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
/// <para>
/// Storage: the record in <see cref="ISecureStore"/> plus a plain marker in <see cref="IPreferencesStore"/> that one
/// is set — Android's secure storage drops a value it cannot decrypt and reports nothing stored, and without the marker
/// that would open the app. Reads never use the store's own recovery (<see cref="ISecureStore.TryGetAsync"/>).
/// </para>
/// <para>
/// Browsers run one instance per tab on the same storage: what one tab stores reaches the others as
/// <see cref="AppLockSignal.StoreChanged"/> (and is re-read before every check, change and on the way back), "Lock now"
/// and unlocking are announced (<see cref="IAppLockPlatform.Announce"/>). Each tab still measures its own time away.
/// Native apps have one instance: the in-memory state is the truth and the keystore is read once.
/// </para>
/// </summary>
internal sealed class AppLockService : IAppLockService, IAccountScoped, IDisposable
{
    public const string ConfigKey = "passcode.v1";
    public const string AttemptsKey = "passcode.attempts.v1";
    /// <summary>"A passcode is set", in the plain preferences (not a secret): see the class remarks.</summary>
    public const string MarkerKey = "passcode.set.v1";

    /// <summary>How often the delay is checked while the app is away and still unlocked.</summary>
    internal static readonly TimeSpan AwayCheckInterval = TimeSpan.FromSeconds(5);

    private readonly ISecureStore _store;
    private readonly IPreferencesStore _preferences;
    private readonly IAppLifecycle _lifecycle;
    private readonly IAppLockPlatform _platform;
    private readonly IScreenCaptureGuard _capture;
    private readonly IAuthService _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<AppLockService> _logger;
    /// <summary>Other instances (browser tabs) share the storage.</summary>
    private readonly bool _shared;
    private readonly SerialQueue _queue = new();
    private readonly object _gate = new();
    private readonly List<Trip> _trips = [];

    private PasscodeConfig? _config;
    private PasscodeAttempts _attempts = PasscodeAttempts.None;
    private bool _locked;
    private int _lockCount;
    private IDisposable? _screenGuard;
    private Away? _away;
    private ITimer? _awayTimer;
    private volatile bool _initialized;
    /// <summary>Signed out at some point of this run: the next sign-in is a fresh one (the password was just entered).</summary>
    private bool _signedOutSeen;

    public AppLockService(
        ISecureStore store,
        IPreferencesStore preferences,
        IAppLifecycle lifecycle,
        IAppLockPlatform platform,
        IScreenCaptureGuard capture,
        IAuthService auth,
        IPlatformInfo platformInfo,
        AccountLifecycle accounts,
        TimeProvider time,
        ILogger<AppLockService> logger)
    {
        _store = store;
        _preferences = preferences;
        _lifecycle = lifecycle;
        _platform = platform;
        _capture = capture;
        _auth = auth;
        _time = time;
        _logger = logger;
        _shared = !platformInfo.IsNativeApp;
        lifecycle.ForegroundChanged += OnForegroundChanged;
        platform.Signaled += OnSignaled;
        accounts.Register(this);
        accounts.OnBeforeSignOut(OnBeforeSignOutAsync);
    }

    /// <summary>Rounds for new hashes; null calibrates them on the device (tests use a few to stay fast).</summary>
    internal int? FixedIterations { get; set; }

    public event Action? Changed;

    public bool IsInitialized => _initialized;

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
    /// Reads the stored passcode before any screen shows: with one the app starts locked (signed out it stays open —
    /// the next sign-in opens the app anyway). Idempotent, never throws; whatever fails, it ends initialized, on the
    /// locked side when in doubt.
    /// </summary>
    public async Task InitializeAsync()
    {
        var hidden = false;
        bool first;
        try
        {
            first = await _queue.RunAsync(async () =>
            {
                if (_initialized) return false;
                var (ok, stored) = await ReadConfigAsync().ConfigureAwait(false);
                // Unreadable store: there is no telling whether a passcode exists, so it does (the safe side) and none matches.
                var config = ok ? stored : PasscodeConfig.Unreadable;
                var attempts = PasscodeAttempts.None;
                if (config is not null)
                {
                    var (read, reanchored) = Reanchor(await ReadAttemptsAsync().ConfigureAwait(false));
                    attempts = read;
                    if (reanchored) await WriteAttemptsAsync(attempts).ConfigureAwait(false);
                    // Set by a build without the marker: from now on a lost record keeps the app locked too.
                    if (config.Hash is not null && !await HasMarkerAsync().ConfigureAwait(false)) await SetMarkerAsync(true).ConfigureAwait(false);
                }
                lock (_gate)
                {
                    _config = config;
                    _attempts = attempts;
                    if (_auth.State == AuthState.SignedOut) _signedOutSeen = true;
                    // A cold start always asks.
                    if (config is not null) LockCore();
                    _initialized = true;
                }
                hidden = config is not null;
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The passcode could not be read; the app stays locked");
            lock (_gate)
            {
                _config ??= PasscodeConfig.Unreadable;
                LockCore();
                _initialized = true;
            }
            first = true;
            hidden = true;
        }
        if (!first) return;
        _platform.SetHiddenInRecents(hidden);
        RaiseChanged();
    }

    /// <summary>
    /// Browsers: another tab turned the passcode on or off, changed it or its options, or counted wrong attempts (its
    /// storage event, or this tab coming back with <paramref name="returning"/>). Turned on elsewhere: this tab locks
    /// when it is away or coming back. Removed elsewhere: this tab stops locking but stays locked if it is — the other
    /// tab either turned it off with the passcode (and announces <see cref="AppLockSignal.Unlocked"/>) or signed out
    /// (this one is signed out with it). Never throws.
    /// </summary>
    internal async Task RefreshFromStoreAsync(bool returning)
    {
        bool? enabled;
        try
        {
            enabled = await _queue.RunAsync(() => SyncFromStoreCoreAsync(returning || !_lifecycle.IsForeground)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The passcode could not be re-read; keeping the current one");
            return;
        }
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
        _platform.Announce(AppLockSignal.Unlocked);
        RaiseChanged();
        return outcome;
    }

    public async Task EnableAsync(string passcode)
    {
        if (!AppLockRules.IsValidPasscode(passcode)) throw new ArgumentException("A passcode is 4 or 6 digits.", nameof(passcode));
        var adopted = false;
        try
        {
            await _queue.RunAsync(async () =>
            {
                // Another tab may have set one meanwhile: that one stays (this tab would replace a passcode it never checked).
                if (_shared) adopted = await SyncFromStoreCoreAsync(lockIfNew: false).ConfigureAwait(false) is not null;
                lock (_gate)
                {
                    if (_config is not null) throw new InvalidOperationException("A passcode is already set; change it instead.");
                }
                if (_auth.State != AuthState.SignedIn) throw new InvalidOperationException("A passcode needs a signed-in session.");
                var hash = await HashAsync(passcode).ConfigureAwait(false);
                var config = new PasscodeConfig { Length = passcode.Length, Hash = hash };
                await WriteConfigAsync(config).ConfigureAwait(false);
                // After the record: a passcode is never "lost" because only its marker was written.
                await SetMarkerAsync(true).ConfigureAwait(false);
                lock (_gate)
                {
                    _config = config;
                    _attempts = PasscodeAttempts.None;
                    UnlockCore();
                }
                await DeleteAttemptsAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (adopted)
        {
            _platform.SetHiddenInRecents(IsEnabled);
            RaiseChanged();
            throw;
        }
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
        // Other tabs stay locked when the passcode just vanishes (that could be a sign-out); this one proved it.
        _platform.Announce(AppLockSignal.Unlocked);
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
        bool enabled;
        lock (_gate)
        {
            enabled = _config is not null;
            changed = LockCore();
        }
        // The whole app: every tab locks (also one that is open on another screen).
        if (enabled) _platform.Announce(AppLockSignal.Locked);
        if (changed) RaiseChanged();
    }

    public IDisposable ExpectReturn()
    {
        var trip = new Trip(this, _time.GetTimestamp());
        lock (_gate) _trips.Add(trip);
        return trip;
    }

    public bool ShouldHideNotificationText()
    {
        CheckAway();
        lock (_gate) return _locked && _config is { HideNotificationText: true };
    }

    /// <summary>
    /// A sign-in in this run (after a sign-out, or a start without a session): the password was just entered, so the
    /// app opens and the wrong-attempt count starts over; a passcode kept by a sign-out the user did not ask for applies
    /// again from here on. The session restored at start is not one of them: that cold start asks for the passcode.
    /// </summary>
    async Task IAccountScoped.OnSignedInAsync(Guid userId)
    {
        var changed = await _queue.RunAsync(async () =>
        {
            bool fresh;
            bool unlocked;
            lock (_gate)
            {
                fresh = _signedOutSeen;
                _signedOutSeen = false;
                unlocked = fresh && _locked;
                if (fresh)
                {
                    _attempts = PasscodeAttempts.None;
                    UnlockCore();
                }
            }
            if (fresh) await DeleteAttemptsAsync().ConfigureAwait(false);
            return unlocked;
        }).ConfigureAwait(false);
        if (changed) RaiseChanged();
    }

    /// <summary>
    /// Any sign-out: nothing is left to open, so the app is unlocked. A passcode still set at this point was kept on
    /// purpose — the user did not ask for this sign-out (the session expired or was ended elsewhere) — and applies after
    /// the next sign-in. Signing out by choice removed it before the session went (<see cref="OnBeforeSignOutAsync"/>).
    /// </summary>
    async Task IAccountScoped.OnSignedOutAsync(Guid userId)
    {
        var changed = false;
        var enabled = false;
        await _queue.RunAsync(() =>
        {
            lock (_gate)
            {
                _signedOutSeen = true;
                changed = _locked;
                UnlockCore();
                enabled = _config is not null;
            }
        }).ConfigureAwait(false);
        if (!enabled) _platform.SetHiddenInRecents(false);
        if (changed) RaiseChanged();
    }

    /// <summary>
    /// The user signs out of this device ("Sign out", "Forgot the passcode?", another server): the passcode goes with the
    /// session. It is removed first, so it is gone even when the app is closed halfway, and the app stays locked until the
    /// session has gone too (signing out takes a moment: calls hang up, the server is told).
    /// </summary>
    private async Task OnBeforeSignOutAsync(Guid userId)
    {
        var had = false;
        await _queue.RunAsync(async () =>
        {
            lock (_gate)
            {
                had = _config is not null;
                _config = null;
                _attempts = PasscodeAttempts.None;
                _away = null;
                StopAwayTimer();
            }
            if (had) await RemoveStoredAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (had) RaiseChanged();
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
        // Browsers: a storage event missed while the tab was frozen is caught up on the way back. Native apps have no
        // other instance, and every extra keystore read is one more chance for it to fail.
        if (foreground && _shared) RefreshFromStoreAsync(returning: true).Forget(_logger);
    }

    /// <summary>Another tab: "Lock now" there locks here, unlocking there opens here, what it stored is read again.</summary>
    private void OnSignaled(AppLockSignal signal)
    {
        bool changed;
        switch (signal)
        {
            case AppLockSignal.Locked:
                lock (_gate) changed = LockCore();
                break;
            case AppLockSignal.Unlocked:
                lock (_gate)
                {
                    changed = _locked;
                    if (changed)
                    {
                        _attempts = PasscodeAttempts.None;
                        UnlockCore();
                    }
                }
                break;
            default:
                RefreshFromStoreAsync(returning: false).Forget(_logger);
                return;
        }
        if (changed) RaiseChanged();
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
        if (delay < AppLockRules.OwnTripGrace && HasTripCore()) delay = AppLockRules.OwnTripGrace;
        _away = new Away(_time.GetUtcNow(), _time.GetTimestamp(), _platform.SinceBoot, delay);
        if (delay <= TimeSpan.Zero) return LockCore();
        StartAwayTimer();
        return false;
    }

    /// <summary>Under the lock. Locks when the app was away too long; a trip of the app's own is over once it is back.</summary>
    private bool OnReturnCore()
    {
        StopAwayTimer();
        _trips.Clear();
        var changed = _away is { } away && !_locked && _config is not null && AwayTooLong(away) && LockCore();
        _away = null;
        return changed;
    }

    /// <summary>Under the lock. A trip announced within <see cref="AppLockRules.OwnTripWindow"/> is on.</summary>
    private bool HasTripCore()
    {
        _trips.RemoveAll(t => _time.GetElapsedTime(t.Started) > AppLockRules.OwnTripWindow);
        return _trips.Count > 0;
    }

    private async Task<PasscodeCheck> CheckAsync(string passcode, bool unlock)
    {
        var result = await _queue.RunAsync(async () =>
        {
            // Browsers: another tab may have changed the passcode or counted wrong attempts meanwhile (they count here too).
            if (_shared) await SyncFromStoreCoreAsync(lockIfNew: false).ConfigureAwait(false);
            PasscodeConfig? config;
            TimeSpan retryIn;
            lock (_gate)
            {
                config = _config;
                retryIn = RetryInCore();
            }
            if (config is null) return new PasscodeCheck(PasscodeCheckStatus.NotSet);
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
        if (unlock && result.IsCorrect) _platform.Announce(AppLockSignal.Unlocked);
        RaiseChanged();
        return result;
    }

    private async Task UpdateAsync(Func<PasscodeConfig, PasscodeConfig> change)
    {
        var updated = await _queue.RunAsync(async () =>
        {
            // Browsers: never write back a passcode another tab has replaced meanwhile.
            var synced = _shared && await SyncFromStoreCoreAsync(lockIfNew: false).ConfigureAwait(false) is not null;
            PasscodeConfig? current;
            lock (_gate) current = _config;
            if (current is null) return synced;
            var next = change(current);
            if (next == current) return synced;
            await WriteConfigAsync(next).ConfigureAwait(false);
            lock (_gate) _config = next;
            return true;
        }).ConfigureAwait(false);
        if (updated) RaiseChanged();
    }

    /// <summary>
    /// In the queue (browsers): adopts what another tab stored. Null when nothing changed, else whether a passcode is
    /// set now. A passcode that appeared locks when <paramref name="lockIfNew"/>; one that vanished leaves the lock as it is.
    /// </summary>
    private async Task<bool?> SyncFromStoreCoreAsync(bool lockIfNew)
    {
        if (!_initialized) return null;
        var (ok, stored) = await ReadConfigAsync().ConfigureAwait(false);
        if (!ok) return null;
        var attempts = stored is null ? PasscodeAttempts.None : await ReadAttemptsAsync().ConfigureAwait(false);
        lock (_gate)
        {
            var changed = false;
            if (stored != _config)
            {
                var wasEnabled = _config is not null;
                _config = stored;
                if (stored is null)
                {
                    _away = null;
                    StopAwayTimer();
                }
                else if (!wasEnabled && lockIfNew)
                {
                    LockCore();
                }
                changed = true;
            }
            if (attempts.Failures != _attempts.Failures || attempts.PausedAt != _attempts.PausedAt)
            {
                _attempts = attempts;
                changed = true;
            }
            return changed ? _config is not null : null;
        }
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

    /// <summary>Locks; false without a passcode, when locked already or signed out (nothing to open: the next sign-in opens it).</summary>
    private bool LockCore()
    {
        if (_config is null || _locked || _auth.State == AuthState.SignedOut) return false;
        _locked = true;
        _lockCount++;
        _away = null;
        StopAwayTimer();
        // Out of screenshots, screen recordings and the recents picture while locked (Android FLAG_SECURE).
        try
        {
            _screenGuard ??= _capture.Protect();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The locked app could not be kept out of screenshots");
        }
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
        TimeSpan? boot = attempts.PausedAtBootMs is { } bootMs && _platform.SinceBoot is { } now
            ? AppLockRules.BootServed(TimeSpan.FromMilliseconds(bootMs), now)
            : null;
        return AppLockRules.PauseRemaining(pause, wall, steady, boot);
    }

    /// <summary>
    /// A pause read back after the phone restarted (its boot clock is below the pause's start): from now on it counts
    /// from this boot — the time served before the restart is unknown, and the wall clock may have been moved forward.
    /// </summary>
    private (PasscodeAttempts Attempts, bool Changed) Reanchor(PasscodeAttempts attempts)
    {
        if (attempts.PausedAtBootMs is not { } bootMs || _platform.SinceBoot is not { } now || now >= TimeSpan.FromMilliseconds(bootMs))
            return (attempts, false);
        return (attempts with { PausedAtBootMs = 0 }, true);
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
    /// The stored record: (Ok, null) without one, not Ok when the store could not be read. A record that is there but
    /// damaged, or gone while the marker says one was set (Android's secure storage drops what it cannot decrypt), reads
    /// as <see cref="PasscodeConfig.Unreadable"/>: no passcode matches it, only signing out removes it.
    /// </summary>
    private async Task<(bool Ok, PasscodeConfig? Config)> ReadConfigAsync()
    {
        SecureStoreRead read;
        try
        {
            read = await _store.TryGetAsync(ConfigKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The passcode could not be read");
            return (false, null);
        }
        if (!read.Ok)
        {
            _logger.LogWarning("The passcode could not be read");
            return (false, null);
        }
        if (string.IsNullOrWhiteSpace(read.Value))
        {
            if (!await HasMarkerAsync().ConfigureAwait(false)) return (true, null);
            _logger.LogWarning("The stored passcode is gone although one was set; signing out of this device removes it");
            return (true, PasscodeConfig.Unreadable);
        }
        try
        {
            var config = JsonSerializer.Deserialize<PasscodeConfig>(read.Value, SkyJson.Options);
            if (config is { Hash: not null } && AppLockRules.IsValidLength(config.Length)) return (true, config);
        }
        catch (JsonException)
        {
        }
        _logger.LogWarning("The stored passcode is unreadable; signing out of this device removes it");
        return (true, PasscodeConfig.Unreadable);
    }

    private async Task<PasscodeAttempts> ReadAttemptsAsync()
    {
        try
        {
            var read = await _store.TryGetAsync(AttemptsKey).ConfigureAwait(false);
            if (!read.Ok || string.IsNullOrWhiteSpace(read.Value)) return PasscodeAttempts.None;
            var attempts = JsonSerializer.Deserialize<PasscodeAttempts>(read.Value, SkyJson.Options);
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

    private async Task<bool> HasMarkerAsync()
    {
        try
        {
            return !string.IsNullOrEmpty(await _preferences.GetAsync(MarkerKey).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The passcode marker could not be read");
            return false;
        }
    }

    private async Task<bool> SetMarkerAsync(bool set)
    {
        try
        {
            await _preferences.SetAsync(MarkerKey, set ? "1" : null).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The passcode marker could not be changed");
            return false;
        }
    }

    /// <summary>
    /// The marker goes first: a passcode removed halfway still reads as set. When the marker cannot go, the record stays
    /// too — without the record the marker would read as a lost passcode and lock the app for good.
    /// </summary>
    private async Task RemoveStoredAsync()
    {
        if (await SetMarkerAsync(false).ConfigureAwait(false))
        {
            try
            {
                await _store.RemoveAsync(ConfigKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The passcode could not be removed");
            }
        }
        await DeleteAttemptsAsync().ConfigureAwait(false);
    }

    private void RaiseChanged() => EventRaiser.Raise(Changed, _logger);

    public void Dispose()
    {
        _lifecycle.ForegroundChanged -= OnForegroundChanged;
        _platform.Signaled -= OnSignaled;
        lock (_gate)
        {
            StopAwayTimer();
            _screenGuard?.Dispose();
            _screenGuard = null;
        }
    }

    /// <summary>The app left (unlocked) at these readings of the three clocks; <see cref="Delay"/> applies to this trip.</summary>
    private sealed record Away(DateTimeOffset Wall, long Steady, TimeSpan? Boot, TimeSpan Delay);

    /// <summary>A trip of the app's own (<see cref="ExpectReturn"/>), announced at <see cref="Started"/> (process clock).</summary>
    private sealed class Trip(AppLockService owner, long started) : IDisposable
    {
        public long Started { get; } = started;

        public void Dispose()
        {
            lock (owner._gate) owner._trips.Remove(this);
        }
    }
}
