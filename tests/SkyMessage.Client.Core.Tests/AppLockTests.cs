using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>Wall clock, process clock and (optionally) the boot clock under test control.</summary>
internal sealed class LockClocks : TimeProvider
{
    private long _wall = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero).UtcTicks;
    private long _steady = TimeSpan.FromDays(3).Ticks;

    public TimeSpan? Boot { get; set; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _wall), TimeSpan.Zero);

    public override long GetTimestamp() => Interlocked.Read(ref _steady);

    /// <summary>Time passes with the device awake: every clock moves.</summary>
    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _wall, by.Ticks);
        Interlocked.Add(ref _steady, by.Ticks);
        if (Boot is { } boot) Boot = boot + by;
    }

    /// <summary>The phone sleeps: the process clock stands still.</summary>
    public void Sleep(TimeSpan by)
    {
        Interlocked.Add(ref _wall, by.Ticks);
        if (Boot is { } boot) Boot = boot + by;
    }

    /// <summary>The user moves the wall clock.</summary>
    public void MoveWallClock(TimeSpan by) => Interlocked.Add(ref _wall, by.Ticks);

    /// <summary>Timers never fire on their own: tests run the periodic work (AppLockService.CheckAway) themselves.</summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new IdleTimer();

    private sealed class IdleTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// One browser's tabs on one storage: a write by any tab reaches the tabs as a storage event, an announcement reaches the
/// others as a BroadcastChannel message (never the sender). A frozen tab misses both.
/// </summary>
internal sealed class FakeTabs
{
    private readonly List<FakeLockPlatform> _tabs = [];

    public FakeTabs(InMemorySecureStore store) => store.Written += _ => Send(null, AppLockSignal.StoreChanged);

    public void Join(FakeLockPlatform tab)
    {
        lock (_tabs) _tabs.Add(tab);
    }

    public void Send(FakeLockPlatform? from, AppLockSignal signal)
    {
        List<FakeLockPlatform> others;
        lock (_tabs) others = _tabs.Where(t => t != from && !t.Frozen).ToList();
        foreach (var tab in others) tab.Raise(signal);
    }
}

internal sealed class FakeLockPlatform : IAppLockPlatform
{
    private readonly LockClocks _clocks;
    private readonly FakeTabs? _tabs;

    public FakeLockPlatform(LockClocks clocks, FakeTabs? tabs = null)
    {
        _clocks = clocks;
        _tabs = tabs;
        tabs?.Join(this);
    }

    public bool SupportsBiometrics { get; set; } = true;
    public bool CanUseBiometrics { get; set; } = true;
    public BiometricOutcome NextOutcome { get; set; } = BiometricOutcome.Succeeded;
    public int Prompts { get; private set; }
    public bool HiddenInRecents { get; private set; }
    /// <summary>A platform PBKDF2 (as browsers have); null = the platform has none and .NET's is used.</summary>
    public Func<string, byte[], int, int, Task<byte[]?>>? Kdf { get; set; }
    public int KdfCalls { get; private set; }
    public List<AppLockSignal> Announced { get; } = [];
    /// <summary>A frozen tab hears nothing from the others.</summary>
    public bool Frozen { get; set; }

    public TimeSpan? SinceBoot => _clocks.Boot;

    public event Action<AppLockSignal>? Signaled;

    public void Announce(AppLockSignal signal)
    {
        Announced.Add(signal);
        _tabs?.Send(this, signal);
    }

    public void Raise(AppLockSignal signal) => Signaled?.Invoke(signal);

    public Task<BiometricOutcome> AuthenticateAsync(BiometricPromptText text, CancellationToken cancellationToken = default)
    {
        Prompts++;
        return Task.FromResult(NextOutcome);
    }

    public void SetHiddenInRecents(bool hidden) => HiddenInRecents = hidden;

    public bool BlocksScreenshotsWhileSet { get; set; }

    public Task<byte[]?> DeriveKeyAsync(string passcode, byte[] salt, int iterations, int length)
    {
        KdfCalls++;
        return Kdf?.Invoke(passcode, salt, iterations, length) ?? Task.FromResult<byte[]?>(null);
    }
}

/// <summary>
/// An <see cref="AppLockService"/> on a test device: its storage, clocks and lifecycle; <see cref="Restart"/> starts the
/// app again on the same device, <c>tabs</c> opens another browser tab next to it.
/// </summary>
internal sealed class LockHarness
{
    public const string Code = "2468";

    public LockClocks Clocks { get; }
    public InMemorySecureStore Store { get; }
    public FakeLifecycle Lifecycle { get; } = new();
    public FakeLockPlatform Platform { get; }
    public IScreenSecurityService Screen { get; } = Substitute.For<IScreenSecurityService>();
    public ScreenCaptureGuard Capture { get; }
    public IAuthService Auth { get; }
    public AccountLifecycle Accounts { get; }
    /// <summary>The signed-in account (the same one after a restart or in another tab, unless told otherwise).</summary>
    public Guid UserId { get; }
    public AppLockService Lock { get; }
    public bool Native { get; }
    public FakeTabs? Tabs { get; }
    public int Changes;

    public LockHarness(InMemorySecureStore? store = null, bool signedIn = true, LockClocks? clocks = null, bool native = false, FakeTabs? tabs = null,
        Guid? userId = null)
    {
        UserId = userId ?? Guid.CreateVersion7();
        Store = store ?? new InMemorySecureStore();
        Clocks = clocks ?? new LockClocks();
        Native = native;
        Tabs = tabs;
        Platform = new FakeLockPlatform(Clocks, tabs);
        Capture = new ScreenCaptureGuard(Screen, Log.For<ScreenCaptureGuard>());
        Auth = Fakes.Auth(UserId);
        if (!signedIn) Auth.State.Returns(AuthState.SignedOut);
        var info = Substitute.For<IPlatformInfo>();
        info.IsNativeApp.Returns(native);
        (_, Accounts) = Fakes.Account();
        Lock = new AppLockService(Store, Store, Lifecycle, Platform, Capture, Auth, info, Accounts, Clocks, Log.For<AppLockService>())
        {
            FixedIterations = 1_000,
        };
        Lock.Changed += () => Interlocked.Increment(ref Changes);
    }

    /// <summary>The app process starts over on the same device (same storage, same clocks; by default the same account).</summary>
    public LockHarness Restart(bool signedIn = true, Guid? userId = null) => new(Store, signedIn, Clocks, Native, userId: userId ?? UserId);

    /// <summary>Another tab of the same browser (same storage, clocks, tabs and session).</summary>
    public LockHarness OpenTab() => new(Store, signedIn: true, Clocks, native: false, Tabs, UserId);

    public static async Task<LockHarness> WithPasscodeAsync(TimeSpan? boot = null, bool native = false)
    {
        var h = new LockHarness(clocks: new LockClocks { Boot = boot }, native: native);
        await h.Lock.InitializeAsync();
        await h.Lock.EnableAsync(Code);
        return h;
    }

    /// <summary>Two tabs of one browser; the first one has the passcode set (when <paramref name="withPasscode"/>).</summary>
    public static async Task<(LockHarness A, LockHarness B)> TwoTabsAsync(bool withPasscode = true)
    {
        var store = new InMemorySecureStore();
        var a = new LockHarness(store, tabs: new FakeTabs(store));
        await a.Lock.InitializeAsync();
        if (withPasscode) await a.Lock.EnableAsync(Code);
        var b = a.OpenTab();
        await b.Lock.InitializeAsync();
        return (a, b);
    }

    public void Leave() => Lifecycle.Set(false);

    public void Return() => Lifecycle.Set(true);

    /// <summary>The user signs out by choice: the by-choice hooks run before the session goes.</summary>
    public async Task SignOutByChoiceAsync()
    {
        await Accounts.SignOutByChoiceAsync(UserId);
        await SignedOutAsync();
    }

    /// <summary>The session ended without the user asking (expired, or ended from another device).</summary>
    public async Task SignedOutAsync()
    {
        Auth.State.Returns(AuthState.SignedOut);
        await Accounts.SignedOutAsync(UserId);
    }

    /// <summary>Signing in with the password (after a sign-out, or at a start without a session).</summary>
    public async Task SignInAsync()
    {
        Auth.State.Returns(AuthState.SignedIn);
        await Accounts.ActivateAsync(UserId);
    }
}

/// <summary>
/// The passcode next to a real <see cref="AuthService"/> on one account lifecycle, wired as the app wires them: the sign-ins
/// and sign-outs the UI and the server start reach the passcode the way they do in the app. <see cref="Restart"/> starts
/// the app again on the same device.
/// </summary>
internal sealed class AuthedDevice
{
    public LockClocks Clocks { get; }
    public InMemorySecureStore Store { get; }
    public IAuthChannel Channel { get; } = Substitute.For<IAuthChannel>();
    public FakeLockPlatform Platform { get; }
    public AccountLifecycle Accounts { get; }
    public AuthService Auth { get; }
    public AppLockService Lock { get; }

    public AuthedDevice(InMemorySecureStore? store = null, LockClocks? clocks = null)
    {
        Store = store ?? new InMemorySecureStore();
        Clocks = clocks ?? new LockClocks();
        Platform = new FakeLockPlatform(Clocks);
        var info = Substitute.For<IPlatformInfo>();
        info.IsNativeApp.Returns(true);
        info.DeviceName.Returns("Test phone");
        (_, Accounts) = Fakes.Account();
        Auth = new AuthService(new AuthSessionStore(Store, Log.For<AuthSessionStore>()), Channel, new SharedLock(), info, Accounts, Clocks,
            Log.For<AuthService>());
        var capture = new ScreenCaptureGuard(Substitute.For<IScreenSecurityService>(), Log.For<ScreenCaptureGuard>());
        Lock = new AppLockService(Store, Store, new FakeLifecycle(), Platform, capture, Auth, info, Accounts, Clocks, Log.For<AppLockService>())
        {
            FixedIterations = 1_000,
        };
    }

    /// <summary>The app process starts over on this device (same storage and clocks).</summary>
    public AuthedDevice Restart() => new(Store, Clocks);

    /// <summary>The app starts: the session is restored, the passcode read, then the session activated (ClientBootstrapper's order).</summary>
    public async Task StartAsync()
    {
        await Auth.InitializeAsync();
        await Lock.InitializeAsync();
        if (Auth.State == AuthState.SignedIn && Auth.UserId is { } userId) await Accounts.ActivateAsync(userId);
    }

    /// <summary>Signs in with the password as <paramref name="userId"/>.</summary>
    public async Task SignInAsync(Guid userId)
    {
        var now = Clocks.GetUtcNow();
        Channel.LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>()).Returns(new AuthResponse
        {
            SessionId = Guid.CreateVersion7(),
            AccessToken = "access-" + userId,
            AccessTokenExpiresAt = now.AddMinutes(15),
            RefreshToken = "refresh-" + userId,
            RefreshTokenExpiresAt = now.AddDays(30),
            MediaKey = MediaSignature.EncodeKey(MediaSignature.NewKey()),
            User = new MeDto { Id = userId, DisplayName = "Someone" },
        });
        (await Auth.LoginAsync("someone", "password")).Success.ShouldBeTrue();
    }

    /// <summary>Any of the passcode's records (the hash, its marker, the attempt count) is still on the device.</summary>
    public bool PasscodeStored =>
        Store.Values.ContainsKey(AppLockService.ConfigKey) || Store.Values.ContainsKey(AppLockService.MarkerKey)
        || Store.Values.ContainsKey(AppLockService.AttemptsKey);
}

internal sealed class FakeLifecycle : IAppLifecycle
{
    public bool IsForeground { get; private set; } = true;

    public event Action<bool>? ForegroundChanged;

    public void Set(bool foreground)
    {
        IsForeground = foreground;
        ForegroundChanged?.Invoke(foreground);
    }
}

public sealed class AppLockRulesTests
{
    [Fact]
    public void A_passcode_is_four_or_six_ascii_digits()
    {
        AppLockRules.IsValidPasscode("0000").ShouldBeTrue();
        AppLockRules.IsValidPasscode("135790").ShouldBeTrue();
        AppLockRules.IsValidPasscode("12345").ShouldBeFalse();
        AppLockRules.IsValidPasscode("12a4").ShouldBeFalse();
        AppLockRules.IsValidPasscode("١٢٣٤").ShouldBeFalse("Arabic-Indic digits are not on our keypad");
        AppLockRules.IsValidPasscode("").ShouldBeFalse();
        AppLockRules.IsValidPasscode(null).ShouldBeFalse();
    }

    [Fact]
    public void Four_wrong_tries_are_free_then_the_pause_starts_at_30_seconds_and_doubles_up_to_an_hour()
    {
        for (var failures = 0; failures < 5; failures++) AppLockRules.PauseAfter(failures).ShouldBe(TimeSpan.Zero);
        AppLockRules.PauseAfter(5).ShouldBe(TimeSpan.FromSeconds(30));
        AppLockRules.PauseAfter(6).ShouldBe(TimeSpan.FromMinutes(1));
        AppLockRules.PauseAfter(7).ShouldBe(TimeSpan.FromMinutes(2));
        AppLockRules.PauseAfter(11).ShouldBe(TimeSpan.FromMinutes(32));
        AppLockRules.PauseAfter(12).ShouldBe(TimeSpan.FromHours(1));
        AppLockRules.PauseAfter(1000).ShouldBe(TimeSpan.FromHours(1));

        AppLockRules.AttemptsLeft(0).ShouldBe(5);
        AppLockRules.AttemptsLeft(4).ShouldBe(1);
        AppLockRules.AttemptsLeft(9).ShouldBe(0);
    }

    [Fact]
    public void Auto_lock_offers_five_delays_and_anything_else_becomes_one_minute()
    {
        AppLockRules.AutoLockChoices.ShouldBe([TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromHours(5)]);
        AppLockRules.NormalizeAutoLock(TimeSpan.FromHours(5)).ShouldBe(TimeSpan.FromHours(5));
        AppLockRules.NormalizeAutoLock(TimeSpan.FromMinutes(7)).ShouldBe(TimeSpan.FromMinutes(1));
        AppLockRules.NormalizeAutoLock(TimeSpan.FromSeconds(-5)).ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Coming_back_locks_once_the_delay_has_passed_on_any_clock()
    {
        var minute = TimeSpan.FromMinutes(1);
        AppLockRules.ShouldLockAfterAway(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null).ShouldBeTrue("immediately");
        AppLockRules.ShouldLockAfterAway(minute, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(59), null).ShouldBeFalse();
        AppLockRules.ShouldLockAfterAway(minute, minute, minute, null).ShouldBeTrue();
        AppLockRules.ShouldLockAfterAway(minute, TimeSpan.FromHours(2), TimeSpan.FromSeconds(5), null)
            .ShouldBeTrue("the process clock stops while the phone sleeps; the wall clock does not");
    }

    [Fact]
    public void A_wall_clock_moved_back_cannot_keep_the_app_open()
    {
        var hour = TimeSpan.FromHours(1);
        AppLockRules.ShouldLockAfterAway(hour, TimeSpan.FromHours(-2), TimeSpan.FromSeconds(10), null)
            .ShouldBeTrue("without a boot clock nobody can tell how long the app was away");
        AppLockRules.ShouldLockAfterAway(hour, TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(10), null)
            .ShouldBeFalse("a time sync step is not tampering");
        AppLockRules.ShouldLockAfterAway(hour, TimeSpan.FromHours(-2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10))
            .ShouldBeFalse("the boot clock says the app was away for ten seconds");
        AppLockRules.ShouldLockAfterAway(hour, TimeSpan.FromMinutes(-50), TimeSpan.FromSeconds(10), TimeSpan.FromHours(2))
            .ShouldBeTrue("the boot clock keeps running while the phone sleeps and cannot be moved");
    }

    [Fact]
    public void A_pause_trusts_the_boot_clock_and_never_grows_past_its_length()
    {
        var pause = TimeSpan.FromSeconds(30);
        AppLockRules.PauseRemaining(pause, TimeSpan.FromHours(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
            .ShouldBe(TimeSpan.FromSeconds(25), "moving the wall clock forward does not end the pause");
        AppLockRules.PauseRemaining(pause, TimeSpan.FromHours(-3), null, null).ShouldBe(pause, "a clock moved back never lengthens it");
        AppLockRules.PauseRemaining(pause, TimeSpan.FromSeconds(10), null, null).ShouldBe(TimeSpan.FromSeconds(20));
        AppLockRules.PauseRemaining(pause, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), null).ShouldBe(TimeSpan.FromSeconds(18));
        AppLockRules.PauseRemaining(TimeSpan.Zero, TimeSpan.Zero, null, null).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void After_a_phone_restart_only_the_time_since_it_counts_never_the_wall_clock()
    {
        AppLockRules.BootServed(TimeSpan.FromHours(5), TimeSpan.FromHours(5) + TimeSpan.FromSeconds(10)).ShouldBe(TimeSpan.FromSeconds(10));
        AppLockRules.BootServed(TimeSpan.FromHours(5), TimeSpan.FromSeconds(40))
            .ShouldBe(TimeSpan.FromSeconds(40), "the boot clock started over: the time before the restart is not counted");

        var hour = TimeSpan.FromHours(1);
        AppLockRules.PauseRemaining(hour, TimeSpan.FromDays(1), null, AppLockRules.BootServed(TimeSpan.FromHours(5), TimeSpan.FromSeconds(40)))
            .ShouldBe(hour - TimeSpan.FromSeconds(40), "a restart plus a clock moved a day ahead buys nothing");
    }
}

public sealed class PasscodeHasherTests
{
    private static readonly PasscodeKdf Managed = PasscodeHasher.Managed;

    [Fact]
    public async Task Only_a_salted_hash_is_kept_and_it_verifies_the_right_passcode_only()
    {
        var first = await PasscodeHasher.CreateAsync("2580", 1_000, Managed);
        var second = await PasscodeHasher.CreateAsync("2580", 1_000, Managed);

        first.Algorithm.ShouldBe("pbkdf2-sha256");
        first.Iterations.ShouldBe(1_000);
        Convert.FromBase64String(first.Salt).Length.ShouldBe(16);
        Convert.FromBase64String(first.Hash).Length.ShouldBe(32);
        first.Salt.ShouldNotBe(second.Salt);
        first.Hash.ShouldNotBe(second.Hash, "the same passcode hashes differently on every device");
        first.Hash.ShouldNotContain("2580");

        (await PasscodeHasher.VerifyAsync("2580", first, Managed)).ShouldBeTrue();
        (await PasscodeHasher.VerifyAsync("2581", first, Managed)).ShouldBeFalse();
        (await PasscodeHasher.VerifyAsync("258000", first, Managed)).ShouldBeFalse();
        (await PasscodeHasher.VerifyAsync("", first, Managed)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_hash_is_standard_pbkdf2_sha256_so_any_implementation_reads_it()
    {
        // RFC 7914 §11 test vector for PBKDF2-HMAC-SHA256 ("passwd", "salt", 1 round, 64 bytes), first 32 bytes.
        var bytes = await PasscodeHasher.Managed("passwd", "salt"u8.ToArray(), 1, 32);
        Convert.ToHexString(bytes).ShouldBe("55AC046E56E3089FEC1691C22544B605F94185216DDE0465E68B9D57C20DACBC");
    }

    [Fact]
    public async Task A_damaged_or_foreign_hash_never_verifies()
    {
        var hash = await PasscodeHasher.CreateAsync("1234", 1_000, Managed);
        (await PasscodeHasher.VerifyAsync("1234", hash with { Algorithm = "md5" }, Managed)).ShouldBeFalse();
        (await PasscodeHasher.VerifyAsync("1234", hash with { Salt = "not base64!" }, Managed)).ShouldBeFalse();
        (await PasscodeHasher.VerifyAsync("1234", hash with { Hash = Convert.ToBase64String(new byte[8]) }, Managed)).ShouldBeFalse();
        (await PasscodeHasher.VerifyAsync("1234", hash with { Iterations = 0 }, Managed)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_round_count_is_calibrated_within_bounds()
    {
        var rounds = await PasscodeHasher.ChooseIterationsAsync(Managed);
        rounds.ShouldBeInRange(PasscodeHasher.MinIterations, PasscodeHasher.MaxIterations);
        (rounds % 1000).ShouldBe(0);
    }
}

public sealed class AppLockServiceTests
{
    private const string Code = LockHarness.Code;

    private static async Task FailAsync(AppLockService service, int times)
    {
        for (var i = 0; i < times; i++) await service.UnlockAsync("0000");
    }

    [Fact]
    public async Task Turning_it_on_keeps_the_app_open_and_the_next_start_asks_for_the_passcode()
    {
        var h = await LockHarness.WithPasscodeAsync();

        h.Lock.IsEnabled.ShouldBeTrue();
        h.Lock.IsLocked.ShouldBeFalse();
        h.Lock.Length.ShouldBe(4);
        h.Lock.AutoLockDelay.ShouldBe(TimeSpan.FromMinutes(1));
        h.Platform.HiddenInRecents.ShouldBeTrue("the recents picture never shows chats while a passcode is set");
        var stored = h.Store.Values[AppLockService.ConfigKey];
        stored.ShouldNotContain(Code);
        stored.ShouldContain("pbkdf2-sha256");
        h.Store.Values.ContainsKey(AppLockService.MarkerKey).ShouldBeTrue("the plain marker says a passcode is set");

        var next = h.Restart();
        next.Lock.IsInitialized.ShouldBeFalse("nothing is known before the stored passcode is read");
        next.Lock.IsLocked.ShouldBeFalse();
        await next.Lock.InitializeAsync();
        next.Lock.IsInitialized.ShouldBeTrue();
        next.Lock.IsLocked.ShouldBeTrue();
        next.Capture.IsSecure.ShouldBeTrue("the locked app is kept out of screenshots");
        next.Platform.HiddenInRecents.ShouldBeTrue();

        (await next.Lock.UnlockAsync(Code)).Status.ShouldBe(PasscodeCheckStatus.Correct);
        next.Lock.IsLocked.ShouldBeFalse();
        next.Capture.IsSecure.ShouldBeFalse();
    }

    [Fact]
    public async Task Without_a_passcode_the_first_read_still_reports_itself()
    {
        var h = new LockHarness();
        await h.Lock.InitializeAsync();

        h.Lock.IsInitialized.ShouldBeTrue();
        h.Lock.IsLocked.ShouldBeFalse();
        h.Changes.ShouldBe(1, "the shell waits for this to stop covering itself");
        await h.Lock.InitializeAsync();
        h.Changes.ShouldBe(1, "idempotent");
    }

    [Fact]
    public async Task Five_wrong_passcodes_pause_the_checks_and_every_further_one_doubles_the_pause()
    {
        var h = await LockHarness.WithPasscodeAsync();
        h.Lock.LockNow();

        await FailAsync(h.Lock, 3);
        var fourth = await h.Lock.UnlockAsync("1111");
        fourth.Status.ShouldBe(PasscodeCheckStatus.Wrong);
        fourth.AttemptsLeft.ShouldBe(1);
        fourth.RetryIn.ShouldBe(TimeSpan.Zero);

        var fifth = await h.Lock.UnlockAsync("1111");
        fifth.Status.ShouldBe(PasscodeCheckStatus.Wrong);
        fifth.RetryIn.ShouldBe(TimeSpan.FromSeconds(30));
        h.Lock.RetryIn.ShouldBe(TimeSpan.FromSeconds(30));

        var refused = await h.Lock.UnlockAsync(Code);
        refused.Status.ShouldBe(PasscodeCheckStatus.Paused, "not even the right passcode is checked during the pause");
        h.Lock.IsLocked.ShouldBeTrue();

        h.Clocks.Advance(TimeSpan.FromSeconds(31));
        h.Lock.RetryIn.ShouldBe(TimeSpan.Zero);
        var sixth = await h.Lock.UnlockAsync("1111");
        sixth.RetryIn.ShouldBe(TimeSpan.FromMinutes(1));

        h.Clocks.Advance(TimeSpan.FromMinutes(1));
        (await h.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
        h.Lock.FailedAttempts.ShouldBe(0);
        h.Store.Values.ContainsKey(AppLockService.AttemptsKey).ShouldBeFalse();
    }

    [Fact]
    public async Task Restarting_the_app_keeps_the_count_and_the_pause()
    {
        var h = await LockHarness.WithPasscodeAsync();
        await FailAsync(h.Lock, 5);

        h.Clocks.Advance(TimeSpan.FromSeconds(10));
        var next = h.Restart();
        await next.Lock.InitializeAsync();

        next.Lock.FailedAttempts.ShouldBe(5);
        next.Lock.RetryIn.ShouldBe(TimeSpan.FromSeconds(20));
        (await next.Lock.UnlockAsync(Code)).Status.ShouldBe(PasscodeCheckStatus.Paused);
        next.Clocks.Advance(TimeSpan.FromSeconds(20));
        (await next.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
    }

    [Fact]
    public async Task With_a_boot_clock_moving_the_wall_clock_or_restarting_does_not_end_the_pause()
    {
        var h = await LockHarness.WithPasscodeAsync(boot: TimeSpan.FromHours(5), native: true);
        await FailAsync(h.Lock, 5);

        h.Clocks.MoveWallClock(TimeSpan.FromHours(2));
        h.Lock.RetryIn.ShouldBe(TimeSpan.FromSeconds(30));

        var next = h.Restart();
        await next.Lock.InitializeAsync();
        next.Lock.RetryIn.ShouldBe(TimeSpan.FromSeconds(30));
        next.Clocks.Advance(TimeSpan.FromSeconds(30));
        next.Lock.RetryIn.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Restarting_the_phone_and_moving_the_clock_forward_does_not_end_the_pause()
    {
        var h = await LockHarness.WithPasscodeAsync(boot: TimeSpan.FromHours(5), native: true);
        // Twelve wrong passcodes: the hour-long pause.
        await FailAsync(h.Lock, 5);
        h.Clocks.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 7; i++)
        {
            h.Clocks.Advance(h.Lock.RetryIn);
            await h.Lock.UnlockAsync("0000");
        }
        h.Lock.RetryIn.ShouldBe(TimeSpan.FromHours(1));

        // The phone restarts (its boot clock starts over) and the wall clock is moved a day ahead.
        h.Clocks.Boot = TimeSpan.FromSeconds(40);
        h.Clocks.MoveWallClock(TimeSpan.FromDays(1));
        var next = h.Restart();
        await next.Lock.InitializeAsync();

        next.Lock.RetryIn.ShouldBe(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(40), "only the time since the restart counts");
        (await next.Lock.UnlockAsync(Code)).Status.ShouldBe(PasscodeCheckStatus.Paused);

        // Counting from that restart, also after the app restarts again.
        next.Clocks.Advance(TimeSpan.FromMinutes(30));
        var again = next.Restart();
        await again.Lock.InitializeAsync();
        again.Lock.RetryIn.ShouldBe(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(40));
        again.Clocks.Advance(TimeSpan.FromMinutes(30));
        (await again.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
    }

    [Fact]
    public async Task Leaving_for_less_than_the_delay_keeps_the_app_open_and_longer_locks_it()
    {
        var h = await LockHarness.WithPasscodeAsync();

        h.Leave();
        h.Clocks.Advance(TimeSpan.FromSeconds(40));
        h.Return();
        h.Lock.IsLocked.ShouldBeFalse();

        h.Leave();
        h.Clocks.Advance(TimeSpan.FromSeconds(61));
        h.Return();
        h.Lock.IsLocked.ShouldBeTrue();
        h.Lock.LockCount.ShouldBe(1);
    }

    [Fact]
    public async Task Time_the_phone_spent_asleep_counts_as_time_away()
    {
        var h = await LockHarness.WithPasscodeAsync();
        await h.Lock.SetAutoLockDelayAsync(TimeSpan.FromHours(1));

        h.Leave();
        h.Clocks.Sleep(TimeSpan.FromHours(2));
        h.Clocks.Advance(TimeSpan.FromSeconds(3));
        h.Return();

        h.Lock.IsLocked.ShouldBeTrue();
    }

    [Fact]
    public async Task The_lock_engages_while_the_app_is_away_so_coming_back_shows_it_at_once()
    {
        var h = await LockHarness.WithPasscodeAsync();
        h.Leave();

        h.Clocks.Advance(TimeSpan.FromSeconds(30));
        h.Lock.CheckAway();
        h.Lock.IsLocked.ShouldBeFalse();

        h.Clocks.Advance(TimeSpan.FromSeconds(31));
        h.Lock.CheckAway();
        h.Lock.IsLocked.ShouldBeTrue("the UI renders the lock screen before the user is back");
        h.Capture.IsSecure.ShouldBeTrue();
    }

    [Fact]
    public async Task Immediately_locks_as_the_app_leaves_but_its_own_picker_gets_a_grace_period()
    {
        var h = await LockHarness.WithPasscodeAsync();
        await h.Lock.SetAutoLockDelayAsync(TimeSpan.Zero);

        h.Leave();
        h.Lock.IsLocked.ShouldBeTrue();
        h.Return();
        await h.Lock.UnlockAsync(Code);

        using (h.Lock.ExpectReturn())
        {
            h.Leave();
            h.Lock.IsLocked.ShouldBeFalse("choosing a photo is not leaving the app");
            h.Clocks.Advance(TimeSpan.FromMinutes(1));
            h.Return();
        }
        h.Lock.IsLocked.ShouldBeFalse();

        var trip = h.Lock.ExpectReturn();
        h.Leave();
        trip.Dispose();
        h.Clocks.Advance(TimeSpan.FromMinutes(3));
        h.Lock.CheckAway();
        h.Lock.IsLocked.ShouldBeTrue("someone who went elsewhere from the picker is locked out after the grace period");
    }

    [Fact]
    public async Task A_trip_whose_result_never_came_does_not_soften_immediately_for_good()
    {
        var h = await LockHarness.WithPasscodeAsync();
        await h.Lock.SetAutoLockDelayAsync(TimeSpan.Zero);

        // The picker's activity was recreated: its handle is never disposed.
        _ = h.Lock.ExpectReturn();
        h.Leave();
        h.Lock.IsLocked.ShouldBeFalse("the trip itself still gets its grace");
        h.Clocks.Advance(TimeSpan.FromSeconds(20));
        h.Return();

        h.Leave();
        h.Lock.IsLocked.ShouldBeTrue("back in the app the trip is over: the next leave locks at once");
    }

    [Fact]
    public async Task A_trip_announced_long_before_leaving_does_not_count()
    {
        var h = await LockHarness.WithPasscodeAsync();
        await h.Lock.SetAutoLockDelayAsync(TimeSpan.Zero);

        // Announced, but the picker never came up (and the handle leaked).
        _ = h.Lock.ExpectReturn();
        h.Clocks.Advance(TimeSpan.FromMinutes(2));
        h.Leave();

        h.Lock.IsLocked.ShouldBeTrue();
    }

    [Fact]
    public async Task Unlocking_with_a_fingerprint_while_away_does_not_lock_again_on_the_way_back()
    {
        var h = await LockHarness.WithPasscodeAsync();
        await h.Lock.SetAutoLockDelayAsync(TimeSpan.Zero);
        await h.Lock.SetUseBiometricsAsync(true);
        h.Lock.LockNow();

        // Some devices show the prompt as a screen of its own: the app counts as away while it is up.
        h.Leave();
        (await h.Lock.UnlockWithBiometricsAsync(new BiometricPromptText("t", null, "c"))).ShouldBe(BiometricOutcome.Succeeded);
        h.Return();

        h.Lock.IsLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task A_fingerprint_unlocks_only_when_allowed_and_resets_the_count()
    {
        var h = await LockHarness.WithPasscodeAsync();
        var text = new BiometricPromptText("Unlock", null, "Passcode");
        h.Lock.LockNow();

        (await h.Lock.UnlockWithBiometricsAsync(text)).ShouldBe(BiometricOutcome.Unavailable, "the user did not allow it");
        h.Platform.Prompts.ShouldBe(0);

        await h.Lock.SetUseBiometricsAsync(true);
        await FailAsync(h.Lock, 2);
        h.Platform.NextOutcome = BiometricOutcome.Cancelled;
        (await h.Lock.UnlockWithBiometricsAsync(text)).ShouldBe(BiometricOutcome.Cancelled);
        h.Lock.IsLocked.ShouldBeTrue();

        h.Platform.NextOutcome = BiometricOutcome.Succeeded;
        (await h.Lock.UnlockWithBiometricsAsync(text)).ShouldBe(BiometricOutcome.Succeeded);
        h.Lock.IsLocked.ShouldBeFalse();
        h.Lock.FailedAttempts.ShouldBe(0);

        h.Lock.LockNow();
        h.Platform.CanUseBiometrics = false;
        (await h.Lock.UnlockWithBiometricsAsync(text)).ShouldBe(BiometricOutcome.Unavailable, "no fingerprint is enrolled any more");
    }

    [Fact]
    public async Task Changing_and_turning_off_need_the_current_passcode()
    {
        var h = await LockHarness.WithPasscodeAsync();
        await h.Lock.SetHideNotificationTextAsync(true);

        (await h.Lock.DisableAsync("1357")).Status.ShouldBe(PasscodeCheckStatus.Wrong);
        h.Lock.IsEnabled.ShouldBeTrue();

        (await h.Lock.ChangeAsync("1357", "975310")).Status.ShouldBe(PasscodeCheckStatus.Wrong);
        (await h.Lock.ChangeAsync(Code, "975310")).IsCorrect.ShouldBeTrue();
        h.Lock.Length.ShouldBe(6);
        h.Lock.HideNotificationText.ShouldBeTrue("the options stay with a new passcode");
        h.Lock.FailedAttempts.ShouldBe(0);
        (await h.Lock.VerifyAsync(Code)).Status.ShouldBe(PasscodeCheckStatus.Wrong);
        (await h.Lock.VerifyAsync("975310")).IsCorrect.ShouldBeTrue();

        (await h.Lock.DisableAsync("975310")).IsCorrect.ShouldBeTrue();
        h.Lock.IsEnabled.ShouldBeFalse();
        h.Store.Values.ShouldBeEmpty("the record, its marker and the count are gone");
        h.Platform.HiddenInRecents.ShouldBeFalse();
        (await h.Lock.UnlockAsync("975310")).Status.ShouldBe(PasscodeCheckStatus.NotSet);
        h.Lock.LockNow();
        h.Lock.IsLocked.ShouldBeFalse("nothing to lock without a passcode");
    }

    [Fact]
    public async Task A_second_passcode_cannot_replace_the_first_without_it()
    {
        var h = await LockHarness.WithPasscodeAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => h.Lock.EnableAsync("1111"));
        await Should.ThrowAsync<ArgumentException>(() => new LockHarness().Lock.EnableAsync("123"));
        (await h.Lock.VerifyAsync(Code)).IsCorrect.ShouldBeTrue();
    }

    [Fact]
    public async Task Signing_out_by_choice_removes_the_passcode_but_keeps_the_app_locked_until_the_session_is_gone()
    {
        var h = await LockHarness.WithPasscodeAsync();
        h.Lock.LockNow();
        await FailAsync(h.Lock, 2);

        // "Forgot the passcode?": the session is still valid while calls hang up and the server is told.
        await h.Accounts.SignOutByChoiceAsync(h.UserId);
        h.Store.Values.ShouldBeEmpty("removed first, so it is gone even if the app is closed halfway");
        h.Lock.IsEnabled.ShouldBeFalse();
        h.Lock.IsLocked.ShouldBeTrue("the chats stay hidden until the session has gone");

        await h.SignedOutAsync();
        h.Lock.IsLocked.ShouldBeFalse();
        h.Lock.FailedAttempts.ShouldBe(0);
        h.Capture.IsSecure.ShouldBeFalse();
        h.Platform.HiddenInRecents.ShouldBeFalse();

        await h.SignInAsync();
        h.Lock.IsEnabled.ShouldBeFalse("the next sign-in starts without a passcode");
    }

    [Fact]
    public async Task A_sign_out_the_user_did_not_ask_for_keeps_the_passcode_for_the_next_sign_in()
    {
        var h = await LockHarness.WithPasscodeAsync();
        h.Lock.LockNow();
        await FailAsync(h.Lock, 3);

        // The session expired (or was ended from another device).
        await h.SignedOutAsync();
        h.Lock.IsLocked.ShouldBeFalse("signed out there is nothing to open");
        h.Lock.IsEnabled.ShouldBeTrue();
        h.Store.Values.ContainsKey(AppLockService.ConfigKey).ShouldBeTrue();
        h.Lock.LockNow();
        h.Lock.IsLocked.ShouldBeFalse("nothing locks while signed out");

        // The app restarts on the sign-in screen: still kept.
        var next = h.Restart(signedIn: false);
        await next.Lock.InitializeAsync();
        next.Lock.IsEnabled.ShouldBeTrue();
        next.Lock.IsLocked.ShouldBeFalse();

        // Signing in with the password opens the app; the passcode applies from then on.
        await next.SignInAsync();
        next.Lock.IsLocked.ShouldBeFalse();
        next.Lock.FailedAttempts.ShouldBe(0, "a password sign-in starts the count over");
        next.Lock.LockNow();
        next.Lock.IsLocked.ShouldBeTrue();
        (await next.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
    }

    [Fact]
    public async Task The_session_restored_at_start_is_not_a_fresh_sign_in()
    {
        var h = await LockHarness.WithPasscodeAsync();
        var next = h.Restart();
        await next.Lock.InitializeAsync();

        // The bootstrapper activates the restored session after reading the passcode.
        await next.SignInAsync();

        next.Lock.IsLocked.ShouldBeTrue("a cold start asks for the passcode");
    }

    /// <summary>When the user's request to sign out runs, relative to the sign-out the server started.</summary>
    public enum DeletionOrder
    {
        /// <summary>The answer to "delete my account" arrives first.</summary>
        AnswerFirst,
        /// <summary>The server's SessionRevoked (realtime, sent before the answer) is still signing the app out.</summary>
        WhileTheServersSignOutRuns,
        /// <summary>The server's sign-out had finished.</summary>
        AfterTheServersSignOut,
    }

    [Theory]
    [InlineData(DeletionOrder.AnswerFirst)]
    [InlineData(DeletionOrder.WhileTheServersSignOutRuns)]
    [InlineData(DeletionOrder.AfterTheServersSignOut)]
    public async Task Deleting_the_account_removes_the_passcode(DeletionOrder order)
    {
        var device = new AuthedDevice();
        await device.StartAsync();
        await device.SignInAsync(Guid.CreateVersion7());
        await device.Lock.EnableAsync(Code);
        await device.Lock.SetHideNotificationTextAsync(true);

        // DeleteAccountDialog, once the server deleted the account (and with it every session).
        switch (order)
        {
            case DeletionOrder.AnswerFirst:
                await device.Auth.LogoutAsync(revokeOnServer: false, byChoice: true);
                break;
            case DeletionOrder.WhileTheServersSignOutRuns:
                var revoked = device.Auth.LogoutAsync(revokeOnServer: false);
                await device.Auth.LogoutAsync(revokeOnServer: false, byChoice: true);
                await revoked;
                break;
            case DeletionOrder.AfterTheServersSignOut:
                await device.Auth.LogoutAsync(revokeOnServer: false);
                device.Lock.IsEnabled.ShouldBeTrue("a sign-out the user did not ask for keeps the passcode");
                await device.Auth.LogoutAsync(revokeOnServer: false, byChoice: true);
                break;
        }

        device.Auth.State.ShouldBe(AuthState.SignedOut);
        device.Lock.IsEnabled.ShouldBeFalse();
        device.Lock.IsLocked.ShouldBeFalse();
        device.PasscodeStored.ShouldBeFalse("the hash, its marker and the count go with the account");
        device.Platform.HiddenInRecents.ShouldBeFalse();
        await device.Channel.DidNotReceive().LogoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The next account on this phone starts without it, also after a restart.
        await device.SignInAsync(Guid.CreateVersion7());
        device.Lock.IsEnabled.ShouldBeFalse();
        var next = device.Restart();
        await next.StartAsync();
        next.Lock.IsEnabled.ShouldBeFalse();
        next.Lock.IsLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Another_account_signing_in_never_inherits_a_kept_passcode()
    {
        var device = new AuthedDevice();
        await device.StartAsync();
        var first = Guid.CreateVersion7();
        await device.SignInAsync(first);
        await device.Lock.EnableAsync(Code);

        // The session ends without the user asking (it expired, or was ended from another device): the passcode is kept for
        // that account, and stays kept across a restart on the sign-in screen.
        await device.Auth.LogoutAsync(revokeOnServer: false);
        device.Lock.IsEnabled.ShouldBeTrue();
        var restarted = device.Restart();
        await restarted.StartAsync();
        restarted.Lock.IsEnabled.ShouldBeTrue();

        // Another account signs in on this phone: nobody but the first one knows that code.
        await restarted.SignInAsync(Guid.CreateVersion7());
        restarted.Lock.IsEnabled.ShouldBeFalse();
        restarted.Lock.IsLocked.ShouldBeFalse();
        restarted.PasscodeStored.ShouldBeFalse();
        restarted.Platform.HiddenInRecents.ShouldBeFalse();
        restarted.Lock.LockNow();
        restarted.Lock.IsLocked.ShouldBeFalse("nothing to lock with");

        var cold = restarted.Restart();
        await cold.StartAsync();
        cold.Lock.IsLocked.ShouldBeFalse("the next start is not locked behind the other account's code");
    }

    [Fact]
    public async Task The_account_that_set_a_kept_passcode_gets_it_back_when_it_signs_in_again()
    {
        var device = new AuthedDevice();
        await device.StartAsync();
        var me = Guid.CreateVersion7();
        await device.SignInAsync(me);
        await device.Lock.EnableAsync(Code);
        device.Store.Values[AppLockService.ConfigKey].ShouldContain(me.ToString(), Case.Insensitive, "the record names the account that set it");

        await device.Auth.LogoutAsync(revokeOnServer: false);
        await device.SignInAsync(me);

        device.Lock.IsEnabled.ShouldBeTrue();
        device.Lock.IsLocked.ShouldBeFalse("the password was just entered");
        var next = device.Restart();
        await next.StartAsync();
        next.Lock.IsLocked.ShouldBeTrue("from now on it asks again");
        (await next.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
    }

    [Fact]
    public async Task A_session_of_another_account_restored_at_start_drops_the_passcode_it_never_set()
    {
        var h = await LockHarness.WithPasscodeAsync();

        // The app stopped after another account's sign-in had stored its session, before the passcode was removed.
        var next = h.Restart(userId: Guid.CreateVersion7());
        await next.Lock.InitializeAsync();
        next.Lock.IsLocked.ShouldBeTrue("a cold start asks first: the passcode is read before the session is looked at");
        await next.Accounts.ActivateAsync(next.UserId);

        next.Lock.IsEnabled.ShouldBeFalse();
        next.Lock.IsLocked.ShouldBeFalse();
        next.Store.Values.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreadable_record_keeps_the_app_locked_and_accepts_no_passcode()
    {
        var store = new InMemorySecureStore();
        store.Values[AppLockService.ConfigKey] = "{ not json";
        var h = new LockHarness(store);

        await h.Lock.InitializeAsync();

        h.Lock.IsLocked.ShouldBeTrue();
        (await h.Lock.UnlockAsync("0000")).Status.ShouldBe(PasscodeCheckStatus.Wrong);
        h.Lock.IsLocked.ShouldBeTrue("only signing out of the device removes it");
    }

    [Fact]
    public async Task A_secure_store_that_cannot_be_read_keeps_the_app_locked_and_wipes_nothing()
    {
        var h = await LockHarness.WithPasscodeAsync(native: true);
        h.Store.Unreadable.Add(AppLockService.ConfigKey);

        var next = h.Restart();
        await next.Lock.InitializeAsync();

        next.Lock.IsLocked.ShouldBeTrue();
        (await next.Lock.UnlockAsync(Code)).Status.ShouldBe(PasscodeCheckStatus.Wrong, "nothing to compare with");
        next.Store.Values.ContainsKey(AppLockService.ConfigKey).ShouldBeTrue("the record is kept for a start that can read it");

        h.Store.Unreadable.Clear();
        var later = h.Restart();
        await later.Lock.InitializeAsync();
        (await later.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
    }

    [Fact]
    public async Task A_record_the_secure_store_dropped_keeps_the_app_locked()
    {
        // Android's secure storage removes a value it cannot decrypt and reports nothing stored.
        var h = await LockHarness.WithPasscodeAsync(native: true);
        h.Store.Values.TryRemove(AppLockService.ConfigKey, out _);

        var next = h.Restart();
        await next.Lock.InitializeAsync();

        next.Lock.IsEnabled.ShouldBeTrue();
        next.Lock.IsLocked.ShouldBeTrue("the marker says a passcode was set");
        (await next.Lock.UnlockAsync(Code)).Status.ShouldBe(PasscodeCheckStatus.Wrong);

        // "Forgot the passcode?" is the way out.
        await next.SignOutByChoiceAsync();
        next.Store.Values.ShouldBeEmpty();
        var after = next.Restart(signedIn: false);
        await after.Lock.InitializeAsync();
        after.Lock.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_native_app_reads_the_keystore_at_start_only_and_a_browser_tab_on_every_return()
    {
        var native = await LockHarness.WithPasscodeAsync(native: true);
        var reads = native.Store.Reads;
        native.Leave();
        native.Return();
        (await native.Lock.VerifyAsync(Code)).IsCorrect.ShouldBeTrue();
        await Task.Delay(50);
        native.Store.Reads.ShouldBe(reads, "one instance: every extra keystore read is just one more chance for it to fail");

        var tab = await LockHarness.WithPasscodeAsync();
        reads = tab.Store.Reads;
        tab.Leave();
        tab.Return();
        await Wait.UntilAsync(() => tab.Store.Reads > reads, "another tab may have changed it meanwhile");
    }

    [Fact]
    public async Task Notification_text_is_hidden_only_while_locked_and_only_when_asked()
    {
        var h = await LockHarness.WithPasscodeAsync();
        h.Lock.LockNow();
        h.Lock.ShouldHideNotificationText().ShouldBeFalse("off by default");

        await h.Lock.UnlockAsync(Code);
        await h.Lock.SetHideNotificationTextAsync(true);
        h.Lock.ShouldHideNotificationText().ShouldBeFalse("the app is open");

        h.Leave();
        h.Clocks.Advance(TimeSpan.FromSeconds(20));
        h.Lock.ShouldHideNotificationText().ShouldBeFalse("opening the app now would not ask for the passcode");
        h.Clocks.Advance(TimeSpan.FromMinutes(1));
        h.Lock.ShouldHideNotificationText().ShouldBeTrue();
        h.Lock.IsLocked.ShouldBeTrue();
    }

    // ---- Browser tabs: one app ----------------------------------------------------------------------------------

    [Fact]
    public async Task Lock_now_in_one_tab_locks_every_tab_and_unlocking_one_opens_them_all()
    {
        var (a, b) = await LockHarness.TwoTabsAsync();
        (await b.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
        a.Lock.IsLocked.ShouldBeFalse();
        b.Lock.IsLocked.ShouldBeFalse();

        a.Lock.LockNow();

        a.Lock.IsLocked.ShouldBeTrue();
        await Wait.UntilAsync(() => b.Lock.IsLocked, "Lock now is for the whole app");

        (await b.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
        await Wait.UntilAsync(() => !a.Lock.IsLocked, "the right passcode in one tab opens the app");
    }

    [Fact]
    public async Task A_passcode_turned_on_in_another_tab_locks_a_hidden_tab_at_once_and_a_visible_one_stays_open()
    {
        var (a, b) = await LockHarness.TwoTabsAsync(withPasscode: false);
        var c = a.OpenTab();
        await c.Lock.InitializeAsync();
        b.Leave();

        await a.Lock.EnableAsync(Code);

        await Wait.UntilAsync(() => b.Lock.IsLocked, "a tab in the background locks without waiting to come back");
        await Wait.UntilAsync(() => c.Lock.IsEnabled, "a visible tab learns it at once");
        c.Lock.IsLocked.ShouldBeFalse("its user is right there");
        a.Lock.IsLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Turning_the_passcode_off_in_one_tab_opens_the_others_and_a_frozen_tab_catches_up_on_return()
    {
        var (a, b) = await LockHarness.TwoTabsAsync();
        b.Lock.IsLocked.ShouldBeTrue();
        b.Platform.Frozen = true;
        var c = a.OpenTab();
        await c.Lock.InitializeAsync();
        c.Lock.IsLocked.ShouldBeTrue();

        (await a.Lock.DisableAsync(Code)).IsCorrect.ShouldBeTrue();

        await Wait.UntilAsync(() => !c.Lock.IsEnabled && !c.Lock.IsLocked, "turned off with the passcode: every tab opens");
        b.Lock.IsEnabled.ShouldBeTrue("a frozen tab heard nothing");

        b.Platform.Frozen = false;
        b.Leave();
        b.Return();
        await Wait.UntilAsync(() => !b.Lock.IsEnabled, "coming back it reads the store again");
        b.Lock.IsLocked.ShouldBeTrue("a passcode that just vanished could be another tab's sign-out: it stays locked");
    }

    [Fact]
    public async Task A_sign_out_in_another_tab_never_opens_a_locked_tab()
    {
        var (a, b) = await LockHarness.TwoTabsAsync();
        (await a.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
        b.Lock.IsLocked.ShouldBeFalse();
        b.Lock.LockNow();
        await Wait.UntilAsync(() => a.Lock.IsLocked);

        // "Forgot the passcode?" in A: the session (shared by the tabs) ends.
        await a.SignOutByChoiceAsync();

        await Wait.UntilAsync(() => !b.Lock.IsEnabled);
        b.Lock.IsLocked.ShouldBeTrue("B follows A out, it does not open its chats meanwhile");
        await b.SignedOutAsync();
        b.Lock.IsLocked.ShouldBeFalse();
        b.Store.Values.ContainsKey(AppLockService.ConfigKey).ShouldBeFalse("B's own sign-out does not bring the passcode back");
    }

    [Fact]
    public async Task A_stale_tab_cannot_replace_a_passcode_another_tab_just_set()
    {
        var (a, b) = await LockHarness.TwoTabsAsync(withPasscode: false);
        b.Platform.Frozen = true;

        await a.Lock.EnableAsync(Code);
        b.Lock.IsEnabled.ShouldBeFalse("B missed the event");

        await Should.ThrowAsync<InvalidOperationException>(() => b.Lock.EnableAsync("1357"));

        b.Lock.IsEnabled.ShouldBeTrue("B adopted the passcode it found");
        b.Lock.IsLocked.ShouldBeFalse("its user was setting one up right there");
        (await a.Lock.VerifyAsync(Code)).IsCorrect.ShouldBeTrue();
        (await b.Lock.VerifyAsync("1357")).Status.ShouldBe(PasscodeCheckStatus.Wrong);
    }

    [Fact]
    public async Task A_stale_tab_checks_against_the_passcode_another_tab_changed()
    {
        var (a, b) = await LockHarness.TwoTabsAsync();
        b.Platform.Frozen = true;

        (await a.Lock.ChangeAsync(Code, "975310")).IsCorrect.ShouldBeTrue();
        (await b.Lock.UnlockAsync(Code)).Status.ShouldBe(PasscodeCheckStatus.Wrong, "the old passcode is gone everywhere");
        (await b.Lock.UnlockAsync("975310")).IsCorrect.ShouldBeTrue();

        // An option changed in the stale tab does not bring the old passcode back.
        b.Platform.Frozen = true;
        (await a.Lock.ChangeAsync("975310", "1122")).IsCorrect.ShouldBeTrue();
        await b.Lock.SetAutoLockDelayAsync(TimeSpan.FromHours(1));
        (await a.Lock.VerifyAsync("1122")).IsCorrect.ShouldBeTrue();
        a.Lock.AutoLockDelay.ShouldBe(TimeSpan.FromHours(1), "A read B's option change");
    }

    [Fact]
    public async Task Wrong_passcodes_in_another_tab_count_toward_the_same_pause()
    {
        var (a, b) = await LockHarness.TwoTabsAsync();
        a.Lock.LockNow();

        await FailAsync(a.Lock, 3);
        (await b.Lock.UnlockAsync("0000")).AttemptsLeft.ShouldBe(1);
        (await b.Lock.UnlockAsync("0000")).RetryIn.ShouldBe(TimeSpan.FromSeconds(30));

        await Wait.UntilAsync(() => a.Lock.RetryIn > TimeSpan.Zero, "the pause shows in the other tab too");
        (await a.Lock.UnlockAsync(Code)).Status.ShouldBe(PasscodeCheckStatus.Paused, "opening more tabs gives no extra tries");
    }

    [Fact]
    public async Task A_faster_platform_hash_is_used_and_reads_the_same_as_dotnets()
    {
        var h = new LockHarness();
        h.Platform.Kdf = async (code, salt, rounds, length) => await PasscodeHasher.Managed(code, salt, rounds, length);
        await h.Lock.InitializeAsync();
        await h.Lock.EnableAsync(Code);
        h.Platform.KdfCalls.ShouldBeGreaterThan(0);

        // The browser lost WebCrypto (or this is another host): .NET's own PBKDF2 checks the same hash.
        var next = h.Restart();
        next.Platform.Kdf = (_, _, _, _) => throw new InvalidOperationException("no WebCrypto");
        await next.Lock.InitializeAsync();
        (await next.Lock.UnlockAsync(Code)).IsCorrect.ShouldBeTrue();
    }

    [Fact]
    public async Task Options_are_stored_with_the_passcode_and_only_known_delays_are_kept()
    {
        var h = await LockHarness.WithPasscodeAsync();
        await h.Lock.SetAutoLockDelayAsync(TimeSpan.FromHours(5));
        await h.Lock.SetUseBiometricsAsync(true);

        var next = h.Restart();
        await next.Lock.InitializeAsync();
        next.Lock.AutoLockDelay.ShouldBe(TimeSpan.FromHours(5));
        next.Lock.UseBiometrics.ShouldBeTrue();

        await next.Lock.SetAutoLockDelayAsync(TimeSpan.FromMinutes(7));
        next.Lock.AutoLockDelay.ShouldBe(TimeSpan.FromMinutes(1));
        next.Changes.ShouldBeGreaterThan(0);
    }
}
