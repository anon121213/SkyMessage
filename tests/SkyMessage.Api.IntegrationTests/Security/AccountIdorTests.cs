using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>Sessions, contacts, phone numbers, privacy, blocks and the user directory of other people.</summary>
[Collection(ApiCollection.Name)]
public sealed class AccountIdorTests(ApiFactory factory)
{
    [Fact]
    public async Task Sessions_of_other_users_cannot_be_listed_or_terminated()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var aliceLaptop = await factory.SignInAgainAsync(alice, "laptop");
        var eve = await factory.RegisterUserAsync("eve");

        await eve.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Sessions.ByIdPath(alice.SessionId), null, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await eve.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Sessions.ByIdPath(aliceLaptop.SessionId), null, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await eve.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Sessions.Others);
        await eve.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Sessions.CurrentPush,
            new RegisterPushRequest { Provider = PushProvider.Fcm, Token = "eve-device-token" });

        var evesSessions = await eve.Client.GetJsonAsync<List<SessionDto>>(ApiRoutes.Sessions.Root);
        evesSessions.Select(s => s.Id).ShouldBe([eve.SessionId]);
        evesSessions.ShouldAllBe(s => s.IpAddress == eve.Ip);

        var alicesSessions = await alice.Client.GetJsonAsync<List<SessionDto>>(ApiRoutes.Sessions.Root);
        alicesSessions.Select(s => s.Id).ShouldBe([alice.SessionId, aliceLaptop.SessionId], ignoreOrder: true);
        alicesSessions.Single(s => s.IsCurrent).Id.ShouldBe(alice.SessionId);
        (await aliceLaptop.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(alice.Id);

        // The owner can terminate her other device.
        await alice.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Sessions.ByIdPath(aliceLaptop.SessionId));
        using var revoked = await aliceLaptop.Client.GetAsync(ApiRoutes.Users.Me, SecurityKit.Ct);
        await revoked.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
    }

    [Fact]
    public async Task Contacts_are_a_private_one_way_address_book()
    {
        var alice = await factory.RegisterUserAsync("alice", "Alice Real");
        var bob = await factory.RegisterUserAsync("bob");
        var eve = await factory.RegisterUserAsync("eve");

        await bob.Client.PostJsonAsync<ContactDto>(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = alice.Id, CustomName = "Secret crush" });

        (await eve.Client.GetJsonAsync<List<ContactDto>>(ApiRoutes.Contacts.Root)).ShouldBeEmpty();
        (await alice.Client.GetJsonAsync<List<ContactDto>>(ApiRoutes.Contacts.Root)).ShouldBeEmpty("contacts are one-directional");
        var asSeenByEve = await eve.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id));
        asSeenByEve.User.ContactName.ShouldBeNull();
        asSeenByEve.User.DisplayName.ShouldBe("Alice Real");

        // eve's edits only ever touch eve's own address book.
        await eve.Client.PatchJsonAsync<ContactDto>(ApiRoutes.Contacts.ByUserIdPath(alice.Id), new UpdateContactRequest { CustomName = "Target" });
        await eve.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Contacts.ByUserIdPath(alice.Id));
        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Contacts.Root, new AddContactRequest { UserId = eve.Id },
            HttpStatusCode.BadRequest, ErrorCodes.CannotTargetSelf);

        var bobsContacts = await bob.Client.GetJsonAsync<List<ContactDto>>(ApiRoutes.Contacts.Root);
        bobsContacts.Single().User.Id.ShouldBe(alice.Id);
        bobsContacts.Single().CustomName.ShouldBe("Secret crush");
        (await bob.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id))).User.ContactName.ShouldBe("Secret crush");
        (await alice.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(bob.Id))).IsMutualContact.ShouldBeFalse();
        (await eve.Client.GetJsonAsync<List<ContactDto>>(ApiRoutes.Contacts.Root)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Phone_numbers_are_shown_and_discoverable_only_as_the_owner_allows()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var eve = await factory.RegisterUserAsync("eve");
        var local = "8 999 " + Random.Shared.Next(100, 999) + "-" + Random.Shared.Next(10, 99) + "-" + Random.Shared.Next(10, 99);
        var me = await alice.Client.PutJsonAsync<MeDto>(ApiRoutes.Users.MePhone,
            new SetPhoneRequest { PhoneNumber = local, DefaultRegion = "RU", Source = PhoneSource.Manual });
        var e164 = me.PhoneNumber!;
        e164.ShouldStartWith("+7999");
        var privacy = await alice.Client.GetJsonAsync<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy);
        privacy.FindByPhone.ShouldBe(PrivacyLevel.Nobody, "discovery is off by default");

        async Task<bool> FindsAsync(TestUser viewer) =>
            (await viewer.Client.PostJsonAsync<List<PhoneMatchDto>>(ApiRoutes.Users.FindByPhones,
                new FindByPhonesRequest { PhoneNumbers = [local], DefaultRegion = "RU" })).Any(m => m.User.Id == alice.Id);
        async Task<string?> PhoneSeenByAsync(TestUser viewer) =>
            (await viewer.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id))).PhoneNumber;

        // Defaults: phone visible to contacts only (bob is not one yet), nobody can discover it.
        (await FindsAsync(bob)).ShouldBeFalse();
        (await FindsAsync(eve)).ShouldBeFalse();
        (await PhoneSeenByAsync(bob)).ShouldBeNull();
        (await PhoneSeenByAsync(eve)).ShouldBeNull();

        await alice.Client.PostJsonAsync<ContactDto>(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = bob.Id });
        await alice.Client.PutJsonAsync<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy, privacy with { FindByPhone = PrivacyLevel.Contacts });
        (await FindsAsync(bob)).ShouldBeTrue();
        (await FindsAsync(eve)).ShouldBeFalse();
        (await PhoneSeenByAsync(bob)).ShouldBe(e164);
        (await PhoneSeenByAsync(eve)).ShouldBeNull();

        // Contacts listing the *viewer* do not matter, only the owner's.
        await eve.Client.PostJsonAsync<ContactDto>(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = alice.Id });
        (await FindsAsync(eve)).ShouldBeFalse();
        (await PhoneSeenByAsync(eve)).ShouldBeNull();

        // Nobody hides it even from contacts; a block hides everything from the blocked user.
        await alice.Client.PutJsonAsync<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy,
            privacy with { FindByPhone = PrivacyLevel.Everybody, PhoneNumber = PrivacyLevel.Nobody });
        (await PhoneSeenByAsync(bob)).ShouldBeNull();
        (await FindsAsync(eve)).ShouldBeTrue();
        await alice.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Blocks.ByUserIdPath(eve.Id));
        (await FindsAsync(eve)).ShouldBeFalse();

        // Taking somebody else's number is refused without saying whose it is.
        var taken = await bob.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Users.MePhone,
            new SetPhoneRequest { PhoneNumber = e164, Source = PhoneSource.Manual }, HttpStatusCode.Conflict, ErrorCodes.PhoneTaken);
        taken.Root.ToString().ShouldNotContain(alice.Id.ToString());
    }

    [Fact]
    public async Task A_block_cuts_every_channel_from_the_blocked_user()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var eve = await factory.RegisterUserAsync("eve");
        var chat = await eve.OpenPrivateChatAsync(alice);
        await eve.SendTextAsync(chat.Id, "hi alice");
        await alice.Client.PatchJsonAsync<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Bio = "Only for friends" });
        var avatar = await MediaKit.UploadPhotoAsync(alice);
        await alice.Client.PutJsonAsync<MeDto>(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = avatar.Id });

        await alice.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Blocks.ByUserIdPath(eve.Id));

        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Messages.RootPath(chat.Id),
            new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "why?" }, HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        await eve.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Messages.ReactionPath(chat.Id, (await eve.PageAsync(chat.Id)).Messages[^1].Id),
            new SetReactionRequest { Emoji = "😢" }, HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Calls.Root, new StartCallRequest { ChatId = chat.Id },
            HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        var group = await eve.Client.PostJsonAsync<CreateGroupResultDto>(ApiRoutes.Chats.Groups,
            new CreateGroupRequest { Title = "Let me in", MemberIds = [alice.Id] });
        group.Failed.ShouldContain(f => f.UserId == alice.Id && f.Code == ErrorCodes.UserBlocked);

        var profile = await eve.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id));
        profile.Bio.ShouldBeNull();
        profile.User.AvatarMediaId.ShouldBeNull();
        profile.CanMessage.ShouldBeFalse();
        profile.CanCall.ShouldBeFalse();
        profile.User.Presence.IsOnline.ShouldBeFalse();
        profile.User.Presence.LastSeenAt.ShouldBeNull();
        (await eve.Client.GetJsonAsync<List<UserSummaryDto>>(ApiRoutes.Users.SearchPath(alice.Username))).ShouldNotContain(u => u.Id == alice.Id);

        // Block lists are private.
        (await eve.Client.GetJsonAsync<List<UserSummaryDto>>(ApiRoutes.Blocks.Root)).ShouldBeEmpty();
        (await alice.Client.GetJsonAsync<List<UserSummaryDto>>(ApiRoutes.Blocks.Root)).Single().Id.ShouldBe(eve.Id);
        // eve cannot undo alice's block (only her own, of which there is none).
        await eve.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Blocks.ByUserIdPath(alice.Id));
        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Messages.RootPath(chat.Id),
            new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "unblocked?" }, HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
    }

    [Fact]
    public async Task Another_users_profile_exposes_no_account_internals_and_respects_last_seen_privacy()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var dave = await factory.RegisterUserAsync("dave");
        var bob = await factory.RegisterUserAsync("bob");
        var privacy = await alice.Client.GetJsonAsync<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy);
        await alice.Client.PutJsonAsync<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy, privacy with { LastSeen = PrivacyLevel.Nobody });
        await using var aliceOnline = await HubProbe.ConnectAsync(factory, alice);
        await using var daveOnline = await HubProbe.ConnectAsync(factory, dave);

        // Online right now, but she hides it: nobody else can tell.
        var hidden = await bob.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id));
        hidden.User.Presence.IsOnline.ShouldBeFalse();
        hidden.User.Presence.LastSeenAt.ShouldBeNull();
        hidden.User.Presence.Approx.ShouldNotBe(LastSeenApprox.Online);
        // Control: dave shares his presence.
        (await bob.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(dave.Id))).User.Presence.IsOnline.ShouldBeTrue();
        // She still sees herself online.
        (await alice.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id))).User.Presence.IsOnline.ShouldBeTrue();

        // Account internals never appear in someone else's view of her.
        using var raw = await bob.Client.GetAsync(ApiRoutes.Users.ByIdPath(alice.Id), SecurityKit.Ct);
        var json = await raw.Content.ReadAsStringAsync(SecurityKit.Ct);
        foreach (var field in new[] { "privacy", "phoneSource", "savedMessagesChatId", "passwordHash", "mediaKey", "refreshToken", "sessions", "email" })
            json.ShouldNotContain($"\"{field}\"", Case.Insensitive);
        await bob.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Users.ByIdPath(Guid.NewGuid()), null, HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
    }

    /// <summary>
    /// Pinning posts a "pinned a message" service message (MessageCreated, push) into the chat, so in a private chat it
    /// is a message channel like reactions: a blocked user must not be able to use it to reach the blocker.
    /// </summary>
    [Fact]
    public async Task A_blocked_user_cannot_reach_the_blocker_through_pins()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var eve = await factory.RegisterUserAsync("eve");
        var chat = await eve.OpenPrivateChatAsync(alice);
        var hello = await eve.SendTextAsync(chat.Id, "hi alice");
        await alice.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Blocks.ByUserIdPath(eve.Id));
        await using var aliceHub = await HubProbe.ConnectAsync(factory, alice);
        var seqBefore = (await alice.Client.GetJsonAsync<ChatSummaryDto>(ApiRoutes.Chats.ByIdPath(chat.Id))).LastMessageSeq;

        using var pin = await eve.Client.SendJsonAsync(HttpMethod.Put, ApiRoutes.Messages.PinPath(chat.Id, hello.Id));
        var pinStatus = pin.StatusCode;

        await aliceHub.FlushAsync(alice);
        var serviceMessages = aliceHub.Received<MessageDto>(nameof(IRealtimeClient.MessageCreated)).Where(m => m.ChatId == chat.Id).ToList();
        var seqAfter = (await alice.Client.GetJsonAsync<ChatSummaryDto>(ApiRoutes.Chats.ByIdPath(chat.Id))).LastMessageSeq;

        serviceMessages.ShouldBeEmpty("eve, blocked by alice, posted into alice's chat by pinning");
        seqAfter.ShouldBe(seqBefore);
        pinStatus.ShouldBe(HttpStatusCode.Forbidden);
        await pin.ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
    }

    [Fact]
    public async Task The_directory_cannot_be_enumerated_by_display_name()
    {
        var hidden = SecurityKit.Unique("Quokka");
        var alice = await factory.RegisterUserAsync("alice", hidden);
        var eve = await factory.RegisterUserAsync("eve");
        var friend = await factory.RegisterUserAsync("friend");
        await friend.OpenPrivateChatAsync(alice);
        await friend.SendTextAsync((await friend.OpenPrivateChatAsync(alice)).Id, "hello");

        // Strangers find people by username prefix (≥ 3 chars), never by display name.
        (await eve.Client.GetJsonAsync<List<UserSummaryDto>>(ApiRoutes.Users.SearchPath(hidden))).ShouldBeEmpty();
        (await eve.Client.GetJsonAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(hidden))).Users.ShouldBeEmpty();
        (await eve.Client.GetJsonAsync<List<UserSummaryDto>>(ApiRoutes.Users.SearchPath(alice.Username[..2]))).ShouldNotContain(u => u.Id == alice.Id);
        (await eve.Client.GetJsonAsync<List<UserSummaryDto>>(ApiRoutes.Users.SearchPath(alice.Username))).ShouldContain(u => u.Id == alice.Id);

        // People who share a chat may search by name.
        (await friend.Client.GetJsonAsync<List<UserSummaryDto>>(ApiRoutes.Users.SearchPath(hidden))).ShouldContain(u => u.Id == alice.Id);

        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Users.Batch,
            new GetUsersRequest { UserIds = Enumerable.Range(0, Limits.MaxBatchUsers + 1).Select(_ => Guid.NewGuid()).ToList() },
            HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }
}
