using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
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

internal sealed class FakeLockPlatform(LockClocks clocks) : IAppLockPlatform
{
    public bool SupportsBiometrics { get; set; } = true;
    public bool CanUseBiometrics { get; set; } = true;
    public BiometricOutcome NextOutcome { get; set; } = BiometricOutcome.Succeeded;
    public int Prompts { get; private set; }
    public bool HiddenInRecents { get; private set; }

    public TimeSpan? SinceBoot => clocks.Boot;

    public Task<BiometricOutcome> AuthenticateAsync(BiometricPromptText text, CancellationToken cancellationToken = default)
    {
        Prompts++;
        return Task.FromResult(NextOutcome);
    }

    public void SetHiddenInRecents(bool hidden) => HiddenInRecents = hidden;
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
        AppLockRules.PauseRemaining(pause, TimeSpan.FromMinutes(5), null, TimeSpan.FromSeconds(-40))
            .ShouldBe(TimeSpan.Zero, "after a reboot the boot clock is unknown and the wall clock decides");
        AppLockRules.PauseRemaining(TimeSpan.Zero, TimeSpan.Zero, null, null).ShouldBe(TimeSpan.Zero);
    }
}

public sealed class PasscodeHasherTests
{
    [Fact]
    public void Only_a_salted_hash_is_kept_and_it_verifies_the_right_passcode_only()
    {
        var first = PasscodeHasher.Create("2580", 1_000);
        var second = PasscodeHasher.Create("2580", 1_000);

        first.Algorithm.ShouldBe("pbkdf2-sha256");
        first.Iterations.ShouldBe(1_000);
        Convert.FromBase64String(first.Salt).Length.ShouldBe(16);
        Convert.FromBase64String(first.Hash).Length.ShouldBe(32);
        first.Salt.ShouldNotBe(second.Salt);
        first.Hash.ShouldNotBe(second.Hash, "the same passcode hashes differently on every device");
        first.Hash.ShouldNotContain("2580");

        PasscodeHasher.Verify("2580", first).ShouldBeTrue();
        PasscodeHasher.Verify("2581", first).ShouldBeFalse();
        PasscodeHasher.Verify("258000", first).ShouldBeFalse();
        PasscodeHasher.Verify("", first).ShouldBeFalse();
    }

    [Fact]
    public void A_damaged_or_foreign_hash_never_verifies()
    {
        var hash = PasscodeHasher.Create("1234", 1_000);
        PasscodeHasher.Verify("1234", hash with { Algorithm = "md5" }).ShouldBeFalse();
        PasscodeHasher.Verify("1234", hash with { Salt = "not base64!" }).ShouldBeFalse();
        PasscodeHasher.Verify("1234", hash with { Hash = Convert.ToBase64String(new byte[8]) }).ShouldBeFalse();
        PasscodeHasher.Verify("1234", hash with { Iterations = 0 }).ShouldBeFalse();
    }

    [Fact]
    public void The_round_count_is_calibrated_within_bounds()
    {
        var rounds = PasscodeHasher.ChooseIterations();
        rounds.ShouldBeInRange(PasscodeHasher.MinIterations, PasscodeHasher.MaxIterations);
        (rounds % 1000).ShouldBe(0);
    }
}

public sealed class AppLockServiceTests
{
    private const string Code = "2468";

    private sealed class Harness
    {
        public LockClocks Clocks { get; }
        public InMemorySecureStore Store { get; }
        public FakeLifecycle Lifecycle { get; } = new();
        public FakeLockPlatform Platform { get; }
        public IScreenSecurityService Screen { get; } = Substitute.For<IScreenSecurityService>();
        public ScreenCaptureGuard Capture { get; }
        public IAuthService Auth { get; }
        public AppLockService Lock { get; }
        public int Changes;

        public Harness(InMemorySecureStore? store = null, bool signedIn = true, LockClocks? clocks = null)
        {
            Store = store ?? new InMemorySecureStore();
            Clocks = clocks ?? new LockClocks();
            Platform = new FakeLockPlatform(Clocks);
            Capture = new ScreenCaptureGuard(Screen, Log.For<ScreenCaptureGuard>());
            Auth = signedIn ? Fakes.Auth(Guid.CreateVersion7()) : SignedOut();
            var (_, accounts) = Fakes.Account();
            Lock = new AppLockService(Store, Lifecycle, Platform, Capture, Auth, accounts, Clocks, Log.For<AppLockService>())
            {
                ChooseIterations = () => 1_000,
            };
            Lock.Changed += () => Interlocked.Increment(ref Changes);
        }

        /// <summary>The app process starts over on the same device (same storage, same clocks).</summary>
        public Harness Restart() => new(Store, signedIn: true, Clocks);

        public static async Task<Harness> WithPasscodeAsync(TimeSpan? boot = null)
        {
            var h = new Harness(clocks: new LockClocks { Boot = boot });
            await h.Lock.InitializeAsync();
            await h.Lock.EnableAsync(Code);
            return h;
        }

        private static IAuthService SignedOut()
        {
            var auth = Substitute.For<IAuthService>();
            auth.State.Returns(AuthState.SignedOut);
            return auth;
        }

        public void Leave() => Lifecycle.Set(false);

        public void Return() => Lifecycle.Set(true);
    }

    private static async Task FailAsync(AppLockService service, int times)
    {
        for (var i = 0; i < times; i++) await service.UnlockAsync("0000");
    }

    [Fact]
    public async Task Turning_it_on_keeps_the_app_open_and_the_next_start_asks_for_the_passcode()
    {
        var h = await Harness.WithPasscodeAsync();

        h.Lock.IsEnabled.ShouldBeTrue();
        h.Lock.IsLocked.ShouldBeFalse();
        h.Lock.Length.ShouldBe(4);
        h.Lock.AutoLockDelay.ShouldBe(TimeSpan.FromMinutes(1));
        h.Platform.HiddenInRecents.ShouldBeTrue("the recents picture never shows chats while a passcode is set");
        var stored = h.Store.Values[AppLockService.ConfigKey];
        stored.ShouldNotContain(Code);
        stored.ShouldContain("pbkdf2-sha256");

        var next = h.Restart();
        next.Lock.IsLocked.ShouldBeFalse("nothing is known before the stored passcode is read");
        await next.Lock.InitializeAsync();
        next.Lock.IsLocked.ShouldBeTrue();
        next.Capture.IsSecure.ShouldBeTrue("the locked app is kept out of screenshots");
        next.Platform.HiddenInRecents.ShouldBeTrue();

        (await next.Lock.UnlockAsync(Code)).Status.ShouldBe(PasscodeCheckStatus.Correct);
        next.Lock.IsLocked.ShouldBeFalse();
        next.Capture.IsSecure.ShouldBeFalse();
    }

    [Fact]
    public async Task Five_wrong_passcodes_pause_the_checks_and_every_further_one_doubles_the_pause()
    {
        var h = await Harness.WithPasscodeAsync();
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
        var h = await Harness.WithPasscodeAsync();
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
        var h = await Harness.WithPasscodeAsync(boot: TimeSpan.FromHours(5));
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
    public async Task Leaving_for_less_than_the_delay_keeps_the_app_open_and_longer_locks_it()
    {
        var h = await Harness.WithPasscodeAsync();

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
        var h = await Harness.WithPasscodeAsync();
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
        var h = await Harness.WithPasscodeAsync();
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
        var h = await Harness.WithPasscodeAsync();
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
    public async Task Unlocking_with_a_fingerprint_while_away_does_not_lock_again_on_the_way_back()
    {
        var h = await Harness.WithPasscodeAsync();
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
        var h = await Harness.WithPasscodeAsync();
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
        var h = await Harness.WithPasscodeAsync();
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
        h.Store.Values.ShouldBeEmpty();
        h.Platform.HiddenInRecents.ShouldBeFalse();
        (await h.Lock.UnlockAsync("975310")).Status.ShouldBe(PasscodeCheckStatus.NotSet);
        h.Lock.LockNow();
        h.Lock.IsLocked.ShouldBeFalse("nothing to lock without a passcode");
    }

    [Fact]
    public async Task A_second_passcode_cannot_replace_the_first_without_it()
    {
        var h = await Harness.WithPasscodeAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => h.Lock.EnableAsync("1111"));
        await Should.ThrowAsync<ArgumentException>(() => new Harness().Lock.EnableAsync("123"));
        (await h.Lock.VerifyAsync(Code)).IsCorrect.ShouldBeTrue();
    }

    [Fact]
    public async Task Signing_out_of_the_device_removes_the_passcode()
    {
        var h = await Harness.WithPasscodeAsync();
        h.Lock.LockNow();
        await FailAsync(h.Lock, 2);

        await ((IAccountScoped)h.Lock).OnSignedOutAsync(Guid.CreateVersion7());

        h.Lock.IsEnabled.ShouldBeFalse();
        h.Lock.IsLocked.ShouldBeFalse();
        h.Lock.FailedAttempts.ShouldBe(0);
        h.Store.Values.ShouldBeEmpty();
        h.Capture.IsSecure.ShouldBeFalse();
        h.Platform.HiddenInRecents.ShouldBeFalse();
    }

    [Fact]
    public async Task A_passcode_left_on_a_signed_out_device_is_removed_at_start()
    {
        var h = await Harness.WithPasscodeAsync();
        var next = new Harness(h.Store, signedIn: false, h.Clocks);

        await next.Lock.InitializeAsync();

        next.Lock.IsEnabled.ShouldBeFalse();
        next.Lock.IsLocked.ShouldBeFalse();
        next.Store.Values.ContainsKey(AppLockService.ConfigKey).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unreadable_record_keeps_the_app_locked_and_accepts_no_passcode()
    {
        var store = new InMemorySecureStore();
        store.Values[AppLockService.ConfigKey] = "{ not json";
        var h = new Harness(store);

        await h.Lock.InitializeAsync();

        h.Lock.IsLocked.ShouldBeTrue();
        (await h.Lock.UnlockAsync("0000")).Status.ShouldBe(PasscodeCheckStatus.Wrong);
        h.Lock.IsLocked.ShouldBeTrue("only signing out of the device removes it");
    }

    [Fact]
    public async Task Notification_text_is_hidden_only_while_locked_and_only_when_asked()
    {
        var h = await Harness.WithPasscodeAsync();
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

    [Fact]
    public async Task Options_are_stored_with_the_passcode_and_only_known_delays_are_kept()
    {
        var h = await Harness.WithPasscodeAsync();
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
