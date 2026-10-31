using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class ProfileVisibilityTests(ApiFactory factory)
{
    [Theory]
    [InlineData(PrivacyLevel.Everybody, true, true)]
    [InlineData(PrivacyLevel.Contacts, false, true)]
    [InlineData(PrivacyLevel.Nobody, false, false)]
    public async Task Phone_number_follows_its_privacy_level(PrivacyLevel level, bool strangerSees, bool contactSees)
    {
        var (owner, stranger, contact) = await ThreeUsersAsync();
        var phone = TestPhones.Thai();
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164 });
        await owner.SetPrivacyAsync(p => p with { PhoneNumber = level });

        (await Profile(stranger, owner)).PhoneNumber.ShouldBe(strangerSees ? phone.E164 : null);
        (await Profile(contact, owner)).PhoneNumber.ShouldBe(contactSees ? phone.E164 : null);
        (await Profile(owner, owner)).PhoneNumber.ShouldBe(phone.E164);
    }

    [Theory]
    [InlineData(PrivacyLevel.Everybody, true, true)]
    [InlineData(PrivacyLevel.Contacts, false, true)]
    [InlineData(PrivacyLevel.Nobody, false, false)]
    public async Task Profile_photo_follows_its_privacy_level(PrivacyLevel level, bool strangerSees, bool contactSees)
    {
        var (owner, stranger, contact) = await ThreeUsersAsync();
        var photo = await factory.CreateMediaAsync(owner.Id);
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = photo });
        await owner.SetPrivacyAsync(p => p with { ProfilePhoto = level });

        (await Profile(stranger, owner)).User.AvatarMediaId.ShouldBe(strangerSees ? photo : null);
        (await Profile(contact, owner)).User.AvatarMediaId.ShouldBe(contactSees ? photo : null);
        (await Profile(owner, owner)).User.AvatarMediaId.ShouldBe(photo);
        (await Batch(stranger, owner)).AvatarMediaId.ShouldBe(strangerSees ? photo : null);
        (await Batch(contact, owner)).AvatarMediaId.ShouldBe(contactSees ? photo : null);
    }

    [Theory]
    [InlineData(PrivacyLevel.Everybody, true, true)]
    [InlineData(PrivacyLevel.Contacts, false, true)]
    [InlineData(PrivacyLevel.Nobody, false, false)]
    public async Task Last_seen_follows_its_privacy_level(PrivacyLevel level, bool strangerSees, bool contactSees)
    {
        var (owner, stranger, contact) = await ThreeUsersAsync();
        var lastSeen = await SetLastSeenAsync(owner, TimeSpan.FromHours(5));
        await owner.SetPrivacyAsync(p => p with { LastSeen = level });

        var exact = new PresenceDto { LastSeenAt = lastSeen, Approx = LastSeenApprox.Exact };
        var hidden = new PresenceDto { Approx = LastSeenApprox.Recently };
        (await Profile(stranger, owner)).User.Presence.ShouldBe(strangerSees ? exact : hidden);
        (await Profile(contact, owner)).User.Presence.ShouldBe(contactSees ? exact : hidden);
        (await Batch(stranger, owner)).Presence.ShouldBe(strangerSees ? exact : hidden);
        (await Profile(owner, owner)).User.Presence.ShouldBe(exact);
    }

    [Theory]
    [InlineData(1, LastSeenApprox.Recently)]
    [InlineData(5, LastSeenApprox.WithinWeek)]
    [InlineData(20, LastSeenApprox.WithinMonth)]
    [InlineData(45, LastSeenApprox.LongAgo)]
    public async Task Hidden_last_seen_is_only_an_approximation(int daysAgo, LastSeenApprox expected)
    {
        var owner = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();
        await SetLastSeenAsync(owner, TimeSpan.FromDays(daysAgo));
        await owner.SetPrivacyAsync(p => p with { LastSeen = PrivacyLevel.Nobody });

        var presence = (await Profile(viewer, owner)).User.Presence;

        presence.ShouldBe(new PresenceDto { IsOnline = false, LastSeenAt = null, Approx = expected });
    }

    [Fact]
    public async Task Never_seen_users_are_long_ago()
    {
        var owner = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();

        (await Profile(viewer, owner)).User.Presence.ShouldBe(new PresenceDto { Approx = LastSeenApprox.LongAgo });
    }

    [Fact]
    public async Task A_user_who_blocked_the_viewer_shows_only_the_bare_card()
    {
        var owner = await factory.NewUserAsync(displayName: "Blocker");
        var viewer = await factory.NewUserAsync();
        await owner.AddContactAsync(viewer);
        var phone = TestPhones.Russian();
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164 });
        await owner.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Bio = "secret bio" });
        var photo = await factory.CreateMediaAsync(owner.Id);
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = photo });
        await SetLastSeenAsync(owner, TimeSpan.FromMinutes(10));
        await owner.SetPrivacyAsync(p => p with { PhoneNumber = PrivacyLevel.Everybody });
        await using var ownerOnline = await RealtimeProbe.ConnectAsync(owner);

        var before = await Profile(viewer, owner);
        before.PhoneNumber.ShouldBe(phone.E164);
        before.User.AvatarMediaId.ShouldBe(photo);
        before.User.Presence.IsOnline.ShouldBeTrue();
        before.CanMessage.ShouldBeTrue();
        before.CanCall.ShouldBeTrue();

        await owner.BlockAsync(viewer);

        var after = await Profile(viewer, owner);
        after.User.Id.ShouldBe(owner.Id);
        after.User.DisplayName.ShouldBe("Blocker");
        after.User.Username.ShouldBe(owner.Username);
        after.PhoneNumber.ShouldBeNull();
        after.Bio.ShouldBeNull();
        after.User.AvatarMediaId.ShouldBeNull();
        after.User.Presence.ShouldBe(new PresenceDto { Approx = LastSeenApprox.LongAgo });
        after.CanMessage.ShouldBeFalse();
        after.CanCall.ShouldBeFalse();
        after.IsBlockedByMe.ShouldBeFalse();
        (await Batch(viewer, owner)).AvatarMediaId.ShouldBeNull();

        // The blocker still sees the blocked user normally, flagged as blocked.
        var reverse = await Profile(owner, viewer);
        reverse.IsBlockedByMe.ShouldBeTrue();
        reverse.CanMessage.ShouldBeFalse();
        reverse.CanCall.ShouldBeFalse();

        await (await owner.Client.Delete(ApiRoutes.Blocks.ByUserIdPath(viewer.Id))).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        var unblocked = await Profile(viewer, owner);
        unblocked.PhoneNumber.ShouldBe(phone.E164);
        unblocked.Bio.ShouldBe("secret bio");
        unblocked.User.AvatarMediaId.ShouldBe(photo);
        unblocked.User.Presence.IsOnline.ShouldBeTrue();
    }

    [Fact]
    public async Task Profile_reports_relationship_flags()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();

        var strangers = await Profile(alice, bob);
        strangers.IsMutualContact.ShouldBeFalse();
        strangers.PrivateChatId.ShouldBeNull();
        strangers.CommonGroupsCount.ShouldBe(0);
        strangers.User.IsContact.ShouldBeFalse();
        strangers.User.ContactName.ShouldBeNull();

        await alice.AddContactAsync(bob, "Bobby");
        (await Profile(alice, bob)).IsMutualContact.ShouldBeFalse();
        await bob.AddContactAsync(alice);
        var chatId = await alice.CreatePrivateChatWithMessageAsync(bob);
        await alice.CreateGroupAsync(bob);
        await bob.CreateGroupAsync(alice);

        var friends = await Profile(alice, bob);
        friends.IsMutualContact.ShouldBeTrue();
        friends.User.IsContact.ShouldBeTrue();
        friends.User.ContactName.ShouldBe("Bobby");
        friends.PrivateChatId.ShouldBe(chatId);
        friends.CommonGroupsCount.ShouldBe(2);
        (await Profile(bob, alice)).PrivateChatId.ShouldBe(chatId);
    }

    [Fact]
    public async Task Own_profile_points_to_saved_messages()
    {
        var user = await factory.NewUserAsync();

        var self = await Profile(user, user);

        self.PrivateChatId.ShouldBe(user.Auth.User.SavedMessagesChatId);
        self.CanMessage.ShouldBeTrue();
        self.CanCall.ShouldBeFalse();
        self.IsMutualContact.ShouldBeFalse();
        self.IsBlockedByMe.ShouldBeFalse();
        self.User.IsContact.ShouldBeFalse();
    }

    [Fact]
    public async Task Messages_and_calls_privacy_drive_CanMessage_and_CanCall()
    {
        var (owner, stranger, contact) = await ThreeUsersAsync();
        await owner.SetPrivacyAsync(p => p with { Messages = PrivacyLevel.Contacts, Calls = PrivacyLevel.Contacts });

        var asStranger = await Profile(stranger, owner);
        asStranger.CanMessage.ShouldBeFalse();
        asStranger.CanCall.ShouldBeFalse();
        var asContact = await Profile(contact, owner);
        asContact.CanMessage.ShouldBeTrue();
        asContact.CanCall.ShouldBeTrue();

        // Messages = Nobody behaves like Contacts; Calls = Nobody means nobody.
        await owner.SetPrivacyAsync(p => p with { Messages = PrivacyLevel.Nobody, Calls = PrivacyLevel.Nobody });
        (await Profile(contact, owner)).CanMessage.ShouldBeTrue();
        (await Profile(contact, owner)).CanCall.ShouldBeFalse();
        (await Profile(stranger, owner)).CanMessage.ShouldBeFalse();

        // Once the owner wrote in the private chat, replying is always allowed.
        await owner.CreatePrivateChatWithMessageAsync(stranger);
        (await Profile(stranger, owner)).CanMessage.ShouldBeTrue();
    }

    [Fact]
    public async Task Lookup_by_username_ignores_case_and_a_leading_at()
    {
        var owner = await factory.NewUserAsync(NewUsername("Look"));
        var viewer = await factory.NewUserAsync();

        (await viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByUsernamePath("@" + owner.Username.ToUpperInvariant()))).User.Id.ShouldBe(owner.Id);
        (await viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByUsernamePath(owner.Username.ToLowerInvariant()))).User.Id.ShouldBe(owner.Id);
        await (await viewer.Client.Get(ApiRoutes.Users.ByUsernamePath(NewUsername("missing")))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
        await (await viewer.Client.Get(ApiRoutes.Users.ByUsernamePath(new string('a', 40)))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
        await (await viewer.Client.Get(ApiRoutes.Users.ByIdPath(Guid.CreateVersion7()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
    }

    private async Task<(TestUser Owner, TestUser Stranger, TestUser Contact)> ThreeUsersAsync()
    {
        var owner = await factory.NewUserAsync();
        var stranger = await factory.NewUserAsync();
        var contact = await factory.NewUserAsync();
        await owner.AddContactAsync(contact);
        return (owner, stranger, contact);
    }

    private static Task<UserProfileDto> Profile(TestUser viewer, TestUser owner) =>
        viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(owner.Id));

    private static async Task<UserSummaryDto> Batch(TestUser viewer, TestUser owner) =>
        (await viewer.Client.PostAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Users.Batch, new GetUsersRequest { UserIds = [owner.Id] })).Single();

    /// <summary>Stores a last-seen time (microsecond precision, like the server) and returns it.</summary>
    private async Task<DateTimeOffset> SetLastSeenAsync(TestUser user, TimeSpan ago)
    {
        var now = DateTimeOffset.UtcNow - ago;
        var at = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, at), Ct));
        return at;
    }
}
