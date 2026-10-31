using NSubstitute;
using Shouldly;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Infrastructure.Identity;
using Xunit;

namespace SkyMessage.UnitTests.Identity;

public sealed class PresenceTrackerTests
{
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly PresenceTracker _tracker;
    private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public PresenceTrackerTests()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(_ => _now);
        _tracker = new PresenceTracker(clock);
    }

    [Fact]
    public void First_connection_goes_online_once()
    {
        _tracker.Connected(_user, "c1");
        _tracker.Connected(_user, "c2");

        Drain().ShouldBe([new PresenceTransition(_user, true, _now)]);
        _tracker.IsOnline(_user).ShouldBeTrue();
        _tracker.IsConnected(_user).ShouldBeTrue();
    }

    [Fact]
    public void Going_offline_waits_for_the_debounce_and_reports_when_activity_stopped()
    {
        _tracker.Connected(_user, "c1");
        Drain();
        var disconnectedAt = _now;
        _tracker.Disconnected(_user, "c1");

        Advance(PresenceTracker.OfflineDelay - TimeSpan.FromSeconds(1));
        _tracker.SweepExpired();
        Drain().ShouldBeEmpty();
        _tracker.IsOnline(_user).ShouldBeTrue();
        _tracker.IsConnected(_user).ShouldBeFalse();

        Advance(TimeSpan.FromSeconds(1));
        _tracker.SweepExpired();
        Drain().ShouldBe([new PresenceTransition(_user, false, disconnectedAt)]);
        _tracker.IsOnline(_user).ShouldBeFalse();
    }

    [Fact]
    public void Reconnecting_within_the_debounce_does_not_flap()
    {
        _tracker.Connected(_user, "c1");
        Drain();
        _tracker.Disconnected(_user, "c1");
        Advance(TimeSpan.FromSeconds(3));
        _tracker.Connected(_user, "c2");

        Advance(PresenceTracker.OfflineDelay * 2);
        _tracker.SweepExpired();

        Drain().ShouldBeEmpty();
        _tracker.IsOnline(_user).ShouldBeTrue();
    }

    [Fact]
    public void Background_connection_is_connected_but_not_online()
    {
        _tracker.Connected(_user, "c1");
        Drain();
        _tracker.SetActive(_user, "c1", false);
        Advance(PresenceTracker.OfflineDelay);
        _tracker.SweepExpired();

        Drain().ShouldHaveSingleItem().IsOnline.ShouldBeFalse();
        _tracker.IsOnline(_user).ShouldBeFalse();
        _tracker.IsConnected(_user).ShouldBeTrue();

        _tracker.SetActive(_user, "c1", true);
        Drain().ShouldBe([new PresenceTransition(_user, true, _now)]);
        _tracker.IsOnline(_user).ShouldBeTrue();
    }

    [Fact]
    public void Any_active_connection_keeps_the_user_online()
    {
        _tracker.Connected(_user, "phone");
        _tracker.Connected(_user, "desktop");
        Drain();
        _tracker.SetActive(_user, "phone", false);
        Advance(PresenceTracker.OfflineDelay * 2);
        _tracker.SweepExpired();

        Drain().ShouldBeEmpty();
        _tracker.IsOnline(_user).ShouldBeTrue();
    }

    [Fact]
    public void Unknown_connections_are_ignored()
    {
        _tracker.SetActive(_user, "nope", true);
        _tracker.Disconnected(_user, "nope");

        Drain().ShouldBeEmpty();
        _tracker.IsOnline(_user).ShouldBeFalse();
        _tracker.IsConnected(_user).ShouldBeFalse();
    }

    [Fact]
    public void Online_user_ids_lists_only_online_users()
    {
        var other = Guid.CreateVersion7();
        _tracker.Connected(_user, "c1");
        _tracker.Connected(other, "c2");
        _tracker.SetActive(other, "c2", false);
        Advance(PresenceTracker.OfflineDelay);
        _tracker.SweepExpired();

        _tracker.OnlineUserIds().ShouldBe([_user]);
    }

    private void Advance(TimeSpan by) => _now += by;

    private List<PresenceTransition> Drain()
    {
        var result = new List<PresenceTransition>();
        while (_tracker.Transitions.TryRead(out var transition))
            result.Add(transition);
        return result;
    }
}
