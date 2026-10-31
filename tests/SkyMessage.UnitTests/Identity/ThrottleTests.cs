using Microsoft.Extensions.Caching.Memory;
using Shouldly;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Identity;

public sealed class TypingThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Events_closer_than_the_interval_are_dropped()
    {
        var throttle = new TypingThrottle();

        throttle.TryAccept(TypingAction.Typing, T0).ShouldBeTrue();
        throttle.TryAccept(TypingAction.Typing, T0.AddMilliseconds(100)).ShouldBeFalse();
        throttle.TryAccept(TypingAction.RecordingVoice, T0.AddMilliseconds(200)).ShouldBeFalse();
        throttle.TryAccept(TypingAction.Typing, T0 + TypingThrottle.MinInterval).ShouldBeTrue();
    }

    [Fact]
    public void One_cancel_may_follow_immediately()
    {
        var throttle = new TypingThrottle();

        throttle.TryAccept(TypingAction.Typing, T0).ShouldBeTrue();
        throttle.TryAccept(TypingAction.Cancel, T0.AddMilliseconds(50)).ShouldBeTrue();
        throttle.TryAccept(TypingAction.Cancel, T0.AddMilliseconds(60)).ShouldBeFalse();
        throttle.TryAccept(TypingAction.Typing, T0.AddMilliseconds(70)).ShouldBeFalse();
    }
}

public sealed class LoginThrottleTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose() => _cache.Dispose();

    [Fact]
    public void Locks_out_after_too_many_failures()
    {
        var throttle = new LoginThrottle(_cache);
        var key = LoginThrottle.UsernameKey("alice");

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
            throttle.RegisterFailure(key);
        throttle.IsLockedOut(key).ShouldBeFalse();

        throttle.RegisterFailure(key);
        throttle.IsLockedOut(key).ShouldBeTrue();
        throttle.IsLockedOut(LoginThrottle.UsernameKey("bob")).ShouldBeFalse();
    }

    [Fact]
    public void Successful_sign_in_resets_the_counter()
    {
        var throttle = new LoginThrottle(_cache);
        var key = LoginThrottle.PhoneKey("+79991234567");
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            throttle.RegisterFailure(key);

        throttle.Reset(key);

        throttle.IsLockedOut(key).ShouldBeFalse();
    }
}
