using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.Identity;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class PresenceTests(ApiFactory factory)
{
    private static readonly TimeSpan OfflineTimeout = PresenceTracker.OfflineDelay + TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Related_users_see_someone_come_online_and_go_offline_after_the_debounce()
    {
        var subject = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();
        await viewer.AddContactAsync(subject);
        await using var viewerHub = await RealtimeProbe.ConnectAsync(viewer);

        var subjectHub = await RealtimeProbe.ConnectAsync(subject);
        var online = await viewerHub.WaitForAsync<PresenceChangedEvent>(e => e.UserId == subject.Id);
        online.Presence.ShouldBe(new PresenceDto { IsOnline = true, Approx = LastSeenApprox.Online });
        (await viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id))).User.Presence.IsOnline.ShouldBeTrue();

        var stoppedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        await subjectHub.DisposeAsync();
        var offline = await viewerHub.WaitForAsync<PresenceChangedEvent>(e => e.UserId == subject.Id && !e.Presence.IsOnline, OfflineTimeout);
        clock.Elapsed.ShouldBeGreaterThan(PresenceTracker.OfflineDelay - TimeSpan.FromSeconds(2));

        offline.Presence.Approx.ShouldBe(LastSeenApprox.Exact);
        offline.Presence.LastSeenAt.ShouldNotBeNull();
        (offline.Presence.LastSeenAt.Value - stoppedAt).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(5));
        var profile = await viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id));
        profile.User.Presence.ShouldBe(offline.Presence);
        (await factory.LoadUserAsync(subject.Id)).LastSeenAt.ShouldBe(offline.Presence.LastSeenAt);
    }

    [Fact]
    public async Task Going_to_the_background_counts_as_offline_and_coming_back_as_online()
    {
        var subject = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();
        await subject.CreatePrivateChatWithMessageAsync(viewer);
        await using var viewerHub = await RealtimeProbe.ConnectAsync(viewer);
        await using var subjectHub = await RealtimeProbe.ConnectAsync(subject);
        var firstOnline = await viewerHub.WaitForAsync<PresenceChangedEvent>(e => e.UserId == subject.Id && e.Presence.IsOnline);

        await subjectHub.SetActiveAsync(false);
        var offline = await viewerHub.WaitForAsync<PresenceChangedEvent>(e => e.UserId == subject.Id && !e.Presence.IsOnline, OfflineTimeout);
        offline.Presence.Approx.ShouldBe(LastSeenApprox.Exact);
        (await viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id))).User.Presence.IsOnline.ShouldBeFalse();

        await subjectHub.SetActiveAsync(true);
        var backOnline = await viewerHub.WaitForAsync<PresenceChangedEvent>(e =>
            e.UserId == subject.Id && e.Presence.IsOnline && !ReferenceEquals(e, firstOnline));
        backOnline.Presence.Approx.ShouldBe(LastSeenApprox.Online);
        (await viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id))).User.Presence.IsOnline.ShouldBeTrue();
    }

    [Fact]
    public async Task Hidden_last_seen_reveals_only_a_bucket_change()
    {
        var subject = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();
        await viewer.AddContactAsync(subject);
        await subject.SetPrivacyAsync(p => p with { LastSeen = PrivacyLevel.Nobody });
        await using var viewerHub = await RealtimeProbe.ConnectAsync(viewer);

        // Never seen before: going online moves the bucket from LongAgo to Recently.
        await using var subjectHub = await RealtimeProbe.ConnectAsync(subject);
        var e = await viewerHub.WaitForAsync<PresenceChangedEvent>(x => x.UserId == subject.Id);
        e.Presence.ShouldBe(new PresenceDto { IsOnline = false, LastSeenAt = null, Approx = LastSeenApprox.Recently });

        var profile = await viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id));
        profile.User.Presence.ShouldBe(new PresenceDto { IsOnline = false, LastSeenAt = null, Approx = LastSeenApprox.Recently });
    }

    [Fact]
    public async Task Recently_seen_hidden_users_coming_online_send_nothing()
    {
        var subject = await factory.NewUserAsync();
        var hiddenViewer = await factory.NewUserAsync();
        var visibleViewer = await factory.NewUserAsync();
        await hiddenViewer.AddContactAsync(subject);
        await visibleViewer.AddContactAsync(subject);
        await subject.AddContactAsync(visibleViewer);
        await subject.SetPrivacyAsync(p => p with { LastSeen = PrivacyLevel.Contacts });
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == subject.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, DateTimeOffset.UtcNow.AddHours(-1)), Ct));
        await using var hiddenHub = await RealtimeProbe.ConnectAsync(hiddenViewer);
        await using var visibleHub = await RealtimeProbe.ConnectAsync(visibleViewer);

        await using var subjectHub = await RealtimeProbe.ConnectAsync(subject);

        (await visibleHub.WaitForAsync<PresenceChangedEvent>(x => x.UserId == subject.Id)).Presence.IsOnline.ShouldBeTrue();
        await hiddenHub.FlushAsync();
        hiddenHub.Received<PresenceChangedEvent>().ShouldBeEmpty();
        (await hiddenViewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id))).User.Presence
            .ShouldBe(new PresenceDto { Approx = LastSeenApprox.Recently });
    }

    [Fact]
    public async Task Contacts_level_shows_contacts_the_real_status_hides_it_from_others_and_skips_blocked_viewers()
    {
        var subject = await factory.NewUserAsync();
        var contact = await factory.NewUserAsync();
        var groupMate = await factory.NewUserAsync();
        var blocked = await factory.NewUserAsync();
        var unrelated = await factory.NewUserAsync();
        // Being in the subject's contacts makes the status visible; saving the subject back makes them related.
        await subject.AddContactAsync(contact);
        await contact.AddContactAsync(subject);
        await subject.CreateGroupAsync(groupMate);
        await blocked.AddContactAsync(subject);
        await subject.BlockAsync(blocked);
        await subject.SetPrivacyAsync(p => p with { LastSeen = PrivacyLevel.Contacts });
        await using var contactHub = await RealtimeProbe.ConnectAsync(contact);
        await using var groupHub = await RealtimeProbe.ConnectAsync(groupMate);
        await using var blockedHub = await RealtimeProbe.ConnectAsync(blocked);
        await using var unrelatedHub = await RealtimeProbe.ConnectAsync(unrelated);

        await using var subjectHub = await RealtimeProbe.ConnectAsync(subject);

        (await contactHub.WaitForAsync<PresenceChangedEvent>(x => x.UserId == subject.Id)).Presence
            .ShouldBe(new PresenceDto { IsOnline = true, Approx = LastSeenApprox.Online });
        (await groupHub.WaitForAsync<PresenceChangedEvent>(x => x.UserId == subject.Id)).Presence
            .ShouldBe(new PresenceDto { IsOnline = false, LastSeenAt = null, Approx = LastSeenApprox.Recently });
        await blockedHub.FlushAsync();
        await unrelatedHub.FlushAsync();
        blockedHub.Received<PresenceChangedEvent>().ShouldBeEmpty();
        unrelatedHub.Received<PresenceChangedEvent>().ShouldBeEmpty();

        (await groupMate.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id))).User.Presence.IsOnline.ShouldBeFalse();
        (await contact.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id))).User.Presence.IsOnline.ShouldBeTrue();
        (await blocked.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id))).User.Presence
            .ShouldBe(new PresenceDto { Approx = LastSeenApprox.LongAgo });
    }

    [Fact]
    public async Task A_second_device_does_not_flap_presence()
    {
        var subject = await factory.NewUserAsync();
        var tablet = await subject.SignInAgainAsync();
        var viewer = await factory.NewUserAsync();
        await viewer.AddContactAsync(subject);
        await using var viewerHub = await RealtimeProbe.ConnectAsync(viewer);
        await using var phoneHub = await RealtimeProbe.ConnectAsync(subject);
        await viewerHub.WaitForAsync<PresenceChangedEvent>(e => e.UserId == subject.Id && e.Presence.IsOnline);

        var tabletHub = await RealtimeProbe.ConnectAsync(tablet);
        await tabletHub.DisposeAsync();
        await phoneHub.SetActiveAsync(true);

        await viewerHub.FlushAsync();
        viewerHub.Received<PresenceChangedEvent>(e => e.UserId == subject.Id).Count.ShouldBe(1);
        (await viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(subject.Id))).User.Presence.IsOnline.ShouldBeTrue();
    }
}
