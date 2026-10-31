using Shouldly;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Identity;

public sealed class PresenceCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static PresenceFacts Facts(
        PrivacyLevel level = PrivacyLevel.Everybody,
        bool isSelf = false,
        bool isDeleted = false,
        bool ownerBlockedViewer = false,
        bool inContacts = false,
        bool isOnline = false,
        DateTimeOffset? lastSeenAt = null) =>
        new(level, isSelf, isDeleted, ownerBlockedViewer, inContacts, isOnline, lastSeenAt);

    [Fact]
    public void Visible_and_online_shows_online_without_a_timestamp()
    {
        var presence = PresenceCalculator.For(Facts(isOnline: true, lastSeenAt: Now.AddHours(-5)), Now);

        presence.IsOnline.ShouldBeTrue();
        presence.Approx.ShouldBe(LastSeenApprox.Online);
        presence.LastSeenAt.ShouldBeNull();
    }

    [Fact]
    public void Visible_and_offline_shows_the_exact_time()
    {
        var seen = Now.AddMinutes(-42);
        var presence = PresenceCalculator.For(Facts(lastSeenAt: seen), Now);

        presence.IsOnline.ShouldBeFalse();
        presence.Approx.ShouldBe(LastSeenApprox.Exact);
        presence.LastSeenAt.ShouldBe(seen);
    }

    [Fact]
    public void Visible_but_never_seen_is_long_ago()
    {
        PresenceCalculator.For(Facts(), Now).Approx.ShouldBe(LastSeenApprox.LongAgo);
    }

    [Fact]
    public void Hidden_online_user_is_never_shown_online()
    {
        var presence = PresenceCalculator.For(Facts(level: PrivacyLevel.Nobody, isOnline: true, lastSeenAt: Now.AddDays(-20)), Now);

        presence.IsOnline.ShouldBeFalse();
        presence.LastSeenAt.ShouldBeNull();
        presence.Approx.ShouldBe(LastSeenApprox.Recently);
    }

    [Theory]
    [InlineData(1, LastSeenApprox.Recently)]
    [InlineData(72, LastSeenApprox.Recently)]
    [InlineData(73, LastSeenApprox.WithinWeek)]
    [InlineData(7 * 24, LastSeenApprox.WithinWeek)]
    [InlineData(7 * 24 + 1, LastSeenApprox.WithinMonth)]
    [InlineData(30 * 24, LastSeenApprox.WithinMonth)]
    [InlineData(30 * 24 + 1, LastSeenApprox.LongAgo)]
    public void Hidden_offline_user_is_approximated_by_recency(int hoursAgo, LastSeenApprox expected)
    {
        var presence = PresenceCalculator.For(Facts(level: PrivacyLevel.Nobody, lastSeenAt: Now.AddHours(-hoursAgo)), Now);

        presence.Approx.ShouldBe(expected);
        presence.IsOnline.ShouldBeFalse();
        presence.LastSeenAt.ShouldBeNull();
    }

    [Fact]
    public void Contacts_level_is_visible_only_to_the_owners_contacts()
    {
        var seen = Now.AddHours(-2);

        PresenceCalculator.For(Facts(level: PrivacyLevel.Contacts, inContacts: true, lastSeenAt: seen), Now)
            .Approx.ShouldBe(LastSeenApprox.Exact);
        PresenceCalculator.For(Facts(level: PrivacyLevel.Contacts, inContacts: false, lastSeenAt: seen), Now)
            .Approx.ShouldBe(LastSeenApprox.Recently);
    }

    [Fact]
    public void Viewer_blocked_by_the_owner_always_sees_long_ago()
    {
        var presence = PresenceCalculator.For(Facts(ownerBlockedViewer: true, inContacts: true, isOnline: true, lastSeenAt: Now), Now);

        presence.IsOnline.ShouldBeFalse();
        presence.LastSeenAt.ShouldBeNull();
        presence.Approx.ShouldBe(LastSeenApprox.LongAgo);
    }

    [Fact]
    public void Deleted_accounts_are_long_ago()
    {
        PresenceCalculator.For(Facts(isDeleted: true, isOnline: true, lastSeenAt: Now), Now)
            .ShouldBe(PresenceCalculator.LongAgo);
    }

    [Fact]
    public void Owners_always_see_their_own_presence()
    {
        PresenceCalculator.For(Facts(level: PrivacyLevel.Nobody, isSelf: true, isOnline: true), Now)
            .Approx.ShouldBe(LastSeenApprox.Online);
    }

    [Theory]
    [InlineData(PrivacyLevel.Everybody, false, true)]
    [InlineData(PrivacyLevel.Contacts, true, true)]
    [InlineData(PrivacyLevel.Contacts, false, false)]
    [InlineData(PrivacyLevel.Nobody, true, false)]
    public void Visibility_levels_follow_the_owners_contacts(PrivacyLevel level, bool inContacts, bool expected)
    {
        PrivacyRules.Allows(level, inContacts).ShouldBe(expected);
    }

    [Theory]
    [InlineData(PrivacyLevel.Everybody, false, true)]
    [InlineData(PrivacyLevel.Contacts, false, false)]
    [InlineData(PrivacyLevel.Nobody, true, true)]
    [InlineData(PrivacyLevel.Nobody, false, false)]
    public void Messages_and_group_invites_treat_nobody_as_contacts(PrivacyLevel level, bool inContacts, bool expected)
    {
        PrivacyRules.AllowsAtLeastContacts(level, inContacts).ShouldBe(expected);
    }
}
