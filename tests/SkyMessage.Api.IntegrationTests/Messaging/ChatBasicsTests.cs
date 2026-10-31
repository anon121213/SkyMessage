using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>
/// Chat basics: my own order of pinned chats (synced to my devices), the "add to contacts / block" bar of a private
/// chat, and content protection (no forwarding out of a protected chat; who may switch it).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ChatBasicsTests(ApiFactory factory)
{
    private static Task<ChatUserSettingsDto> PinAsync(TestUser user, Guid chatId, bool pinned = true) =>
        user.UpdateSettingsAsync(chatId, new UpdateChatSettingsRequest { IsPinned = pinned });

    private static async Task<List<Guid>> PinnedOrderAsync(TestUser user) =>
        (await user.ChatsAsync()).Where(c => c.IsPinned).Select(c => c.Id).ToList();

    private static ForwardMessagesRequest Forward(Guid fromChatId, params Guid[] messageIds) => new()
    {
        FromChatId = fromChatId,
        MessageIds = messageIds,
        ClientMessageIds = messageIds.Select(_ => Guid.NewGuid()).ToList(),
    };

    // ---- Pinned chats order ----

    [Fact]
    public async Task Pinned_chats_keep_my_order_new_pins_go_on_top_and_my_devices_follow()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var a = await alice.NewGroupAsync(bob);
        var b = await alice.NewGroupAsync(bob);
        var c = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var d = await alice.NewGroupAsync(bob);
        var tablet = await alice.SecondDeviceAsync();
        await using var tabletRt = await tablet.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        (await PinAsync(alice, a)).PinOrder.ShouldBe(1);
        (await PinAsync(alice, b)).PinOrder.ShouldBe(2);
        var pinC = await PinAsync(alice, c);
        pinC.PinOrder.ShouldBe(3);
        (await tabletRt.WaitAsync<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, e => e.ChatId == c)).Settings.PinOrder.ShouldBe(3);
        (await PinnedOrderAsync(alice)).ShouldBe([c, b, a]);

        var order = await alice.ReorderPinnedAsync(a, c, b);
        order.Chats.Select(x => x.ChatId).ShouldBe([a, c, b]);
        order.Chats.Select(x => x.PinOrder).ShouldBe([3L, 2L, 1L]);
        var synced = await tabletRt.WaitAsync<PinnedChatsOrderDto>(Ev.PinnedChatsReordered);
        synced.Chats.ShouldBe(order.Chats);

        // Stored on the server: any device (and any later start) reads the same order.
        (await PinnedOrderAsync(tablet)).ShouldBe([a, c, b]);
        var list = await alice.ChatsAsync();
        list.Take(3).Select(x => x.Id).ShouldBe([a, c, b]);
        list.Single(x => x.Id == c).PinOrder.ShouldBe(2);
        list.Single(x => x.Id == d).PinOrder.ShouldBe(0);

        // A new pin goes on top of the rearranged ones; unpinning leaves a hole the others keep around.
        (await PinAsync(alice, d)).PinOrder.ShouldBe(4);
        (await PinnedOrderAsync(alice)).ShouldBe([d, a, c, b]);
        var unpinned = await PinAsync(alice, c, pinned: false);
        unpinned.PinOrder.ShouldBe(0);
        (await PinnedOrderAsync(alice)).ShouldBe([d, a, b]);
        (await alice.ChatAsync(c)).PinOrder.ShouldBe(0);

        // My order is mine: Bob neither sees it nor hears about it.
        await bobRt.BarrierAsync();
        bobRt.All<PinnedChatsOrderDto>(Ev.PinnedChatsReordered).ShouldBeEmpty();
        (await bob.ChatsAsync()).ShouldAllBe(x => !x.IsPinned && x.PinOrder == 0);
    }

    [Fact]
    public async Task Reordering_moves_only_the_listed_pinned_chats_and_a_repeat_is_silent()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var a = await alice.NewGroupAsync(bob);
        var b = await alice.NewGroupAsync(bob);
        var c = await alice.NewGroupAsync(bob);
        var d = await alice.NewGroupAsync(bob);
        var notPinned = await alice.NewGroupAsync(bob);
        var bobsOwn = await bob.NewGroupAsync();
        foreach (var chat in new[] { a, b, c, d }) await PinAsync(alice, chat);
        await PinAsync(bob, bobsOwn);
        (await PinnedOrderAsync(alice)).ShouldBe([d, c, b, a]);

        // Only a and b are listed (c and d were, say, pinned on another device meanwhile): they swap the places they
        // held; ids that are not my pinned chats are ignored.
        var order = await alice.ReorderPinnedAsync(a, notPinned, bobsOwn, Guid.NewGuid(), b);
        order.Chats.Select(x => x.ChatId).ShouldBe([d, c, a, b]);
        (await PinnedOrderAsync(alice)).ShouldBe([d, c, a, b]);
        (await bob.ChatAsync(bobsOwn)).PinOrder.ShouldBe(1);
        (await alice.ChatAsync(notPinned)).IsPinned.ShouldBeFalse();

        await using var aliceRt = await alice.ConnectAsync();
        (await alice.ReorderPinnedAsync(d, c, a, b)).Chats.Select(x => x.ChatId).ShouldBe([d, c, a, b]);
        await aliceRt.BarrierAsync();
        aliceRt.All<PinnedChatsOrderDto>(Ev.PinnedChatsReordered).ShouldBeEmpty();

        await (await alice.TryReorderPinnedAsync()).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TryReorderPinnedAsync(a, a)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TryReorderPinnedAsync(Guid.Empty)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Deleting_a_chat_for_me_drops_its_pin_order()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.SendAsync(chat, "hi");
        await PinAsync(alice, chat);

        await alice.DeleteChatAsync(chat, forEveryone: false);
        await bob.SendAsync(chat, "back again");

        var back = await alice.ChatAsync(chat);
        back.IsPinned.ShouldBeFalse();
        back.PinOrder.ShouldBe(0);
    }

    // ---- New contact bar ----

    [Fact]
    public async Task Closing_the_contact_bar_is_remembered_per_chat_and_reaches_my_other_devices()
    {
        var alice = await factory.NewUserAsync("Alice");
        var stranger = await factory.NewUserAsync("Stranger");
        var chat = (await stranger.OpenPrivateAsync(alice.Id)).Id;
        await stranger.SendAsync(chat, "hello, it is me");
        var tablet = await alice.SecondDeviceAsync();
        await using var tabletRt = await tablet.ConnectAsync();

        (await alice.ChatAsync(chat)).ContactBarDismissed.ShouldBeFalse();
        var settings = await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { ContactBarDismissed = true });
        settings.ContactBarDismissed.ShouldBeTrue();
        var e = await tabletRt.WaitAsync<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, x => x.ChatId == chat);
        e.Settings.ContactBarDismissed.ShouldBeTrue();
        (await tablet.ChatAsync(chat)).ContactBarDismissed.ShouldBeTrue();
        (await alice.ChatsAsync()).Single(x => x.Id == chat).ContactBarDismissed.ShouldBeTrue();

        // Per user: the other side still has their own bar.
        (await stranger.ChatAsync(chat)).ContactBarDismissed.ShouldBeFalse();

        // Other settings leave it alone.
        (await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { MuteForSeconds = 60 })).ContactBarDismissed.ShouldBeTrue();

        var group = await alice.NewGroupAsync(stranger);
        await (await alice.PutRawAsync(ApiRoutes.Chats.SettingsPath(group), new UpdateChatSettingsRequest { ContactBarDismissed = true }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    // ---- Content protection ----

    [Fact]
    public async Task Only_group_admins_switch_protection_and_nothing_can_be_forwarded_out_while_it_is_on()
    {
        var owner = await factory.NewUserAsync("Owner");
        var admin = await factory.NewUserAsync("Admin");
        var member = await factory.NewUserAsync("Member");
        var group = await owner.NewGroupAsync(admin, member);
        await owner.SetRoleAsync(group, admin.Id, ChatRole.Admin);
        var secret = await member.SendAsync(group, "family recipe");
        var other = (await member.OpenPrivateAsync(owner.Id)).Id;
        var outside = await member.SendAsync(other, "a message from elsewhere");
        await using var memberRt = await member.ConnectAsync();

        await (await member.TrySetContentProtectionAsync(group, true)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);

        var info = await admin.SetContentProtectionAsync(group, true);
        info.ProtectContent.ShouldBeTrue();
        info.ProtectContentBy.ShouldBeEmpty();
        (await memberRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, x => x.Id == group && x.ProtectContent)).ProtectContent.ShouldBeTrue();
        var notice = await memberRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == group && m.Type == MessageType.System
                                                                                     && m.SystemEvent!.Type == SystemEventType.ContentProtectionEnabled);
        notice.SystemEvent!.ActorId.ShouldBe(admin.Id);
        (await member.ChatAsync(group)).ProtectContent.ShouldBeTrue();
        (await member.DetailsAsync(group)).Summary.ProtectContent.ShouldBeTrue();

        // Nobody forwards out of it — not members, not admins, not even into Saved Messages or the group itself.
        await (await member.TryForwardAsync(member.SavedChatId, Forward(group, secret.Id))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.ForwardRestricted);
        await (await member.TryForwardAsync(other, Forward(group, secret.Id))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.ForwardRestricted);
        await (await owner.TryForwardAsync(owner.SavedChatId, Forward(group, secret.Id))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.ForwardRestricted);
        await (await owner.TryForwardAsync(group, Forward(group, secret.Id))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.ForwardRestricted);
        // Forwarding into a protected chat is fine.
        (await member.ForwardAsync(group, Forward(other, outside.Id))).Count.ShouldBe(1);

        // Switching it on again changes nothing and posts nothing.
        await owner.SetContentProtectionAsync(group, true);
        await memberRt.BarrierAsync();
        memberRt.All<MessageDto>(Ev.MessageCreated, m => m.ChatId == group && m.Type == MessageType.System).Count.ShouldBe(1);

        var off = await owner.SetContentProtectionAsync(group, false);
        off.ProtectContent.ShouldBeFalse();
        await memberRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == group && m.SystemEvent?.Type == SystemEventType.ContentProtectionDisabled);
        (await member.ForwardAsync(member.SavedChatId, Forward(group, secret.Id))).Single().Text.ShouldBe("family recipe");
    }

    [Fact]
    public async Task Either_side_protects_a_private_chat_and_each_can_only_take_back_their_own_part()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var photo = await alice.SendAsync(chat, "photo of the kids");
        await using var aliceRt = await alice.ConnectAsync();

        var on = await bob.SetContentProtectionAsync(chat, true);
        on.ProtectContent.ShouldBeTrue();
        on.ProtectContentBy.ShouldBe([bob.Id]);
        await aliceRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, x => x.Id == chat && x.ProtectContent);
        (await aliceRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.ContentProtectionEnabled))
            .SystemEvent!.ActorId.ShouldBe(bob.Id);
        var mine = await alice.ChatAsync(chat);
        mine.ProtectContent.ShouldBeTrue();
        mine.ProtectContentBy.ShouldBe([bob.Id]);

        // It protects both sides.
        await (await alice.TryForwardAsync(alice.SavedChatId, Forward(chat, photo.Id))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.ForwardRestricted);
        await (await bob.TryForwardAsync(bob.SavedChatId, Forward(chat, photo.Id))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.ForwardRestricted);

        // Alice cannot switch off Bob's protection; she can add her own (no new notice: nothing changes for anyone).
        (await alice.SetContentProtectionAsync(chat, false)).ProtectContentBy.ShouldBe([bob.Id]);
        (await alice.SetContentProtectionAsync(chat, true)).ProtectContentBy.ShouldBe([bob.Id, alice.Id]);
        var stillOn = await bob.SetContentProtectionAsync(chat, false);
        stillOn.ProtectContent.ShouldBeTrue();
        stillOn.ProtectContentBy.ShouldBe([alice.Id]);
        await (await bob.TryForwardAsync(bob.SavedChatId, Forward(chat, photo.Id))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.ForwardRestricted);

        var off = await alice.SetContentProtectionAsync(chat, false);
        off.ProtectContent.ShouldBeFalse();
        off.ProtectContentBy.ShouldBeEmpty();
        await aliceRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.ContentProtectionDisabled);
        aliceRt.All<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.Type == MessageType.System).Count.ShouldBe(2);
        (await bob.ForwardAsync(bob.SavedChatId, Forward(chat, photo.Id))).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_forward_made_before_protection_returns_its_copies_when_retried()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var message = await bob.SendAsync(chat, "forward me");
        var request = Forward(chat, message.Id);
        var copies = await alice.ForwardAsync(alice.SavedChatId, request);

        await bob.SetContentProtectionAsync(chat, true);

        (await alice.ForwardAsync(alice.SavedChatId, request)).Select(m => m.Id).ShouldBe(copies.Select(m => m.Id));
        await (await alice.TryForwardAsync(alice.SavedChatId, request with { ClientMessageIds = [Guid.NewGuid()] }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.ForwardRestricted);
    }

    /// <summary>
    /// The on/off notice is a message into the other side's chat, so in a private chat it is a channel the block closes:
    /// it must neither reach the blocker nor bring back a chat they deleted.
    /// </summary>
    [Fact]
    public async Task A_block_stops_protection_being_switched_on_and_taking_a_part_back_posts_nothing()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await bob.OpenPrivateAsync(alice.Id)).Id;
        await bob.SendAsync(chat, "hi");
        await bob.SetContentProtectionAsync(chat, true);
        await alice.DeleteChatAsync(chat, forEveryone: false);
        await alice.BlockAsync(bob.Id);
        var seq = (await bob.ChatAsync(chat)).LastMessageSeq;
        await using var aliceRt = await alice.ConnectAsync();

        // Neither the blocked side nor the blocker switches it on.
        await (await bob.TrySetContentProtectionAsync(chat, true)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        await (await alice.TrySetContentProtectionAsync(chat, true)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        (await bob.ChatAsync(chat)).ProtectContentBy.ShouldBe([bob.Id]);

        // Bob still takes his own part back; the chat is no longer protected, and nobody hears of it through a notice.
        var off = await bob.SetContentProtectionAsync(chat, false);
        off.ProtectContent.ShouldBeFalse();
        off.ProtectContentBy.ShouldBeEmpty();
        await aliceRt.BarrierAsync();
        aliceRt.All<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat).ShouldBeEmpty();
        aliceRt.All<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat).ShouldBeEmpty();
        (await alice.ChatsAsync()).ShouldNotContain(c => c.Id == chat);
        (await bob.ChatAsync(chat)).LastMessageSeq.ShouldBe(seq);
    }

    [Fact]
    public async Task Saved_messages_cannot_be_protected_and_strangers_see_nothing()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var eve = await factory.NewUserAsync("Eve");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var group = await alice.NewGroupAsync(bob);

        await (await alice.TrySetContentProtectionAsync(alice.SavedChatId, true)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await eve.TrySetContentProtectionAsync(chat, true)).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await eve.TrySetContentProtectionAsync(group, true)).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        (await alice.ChatAsync(chat)).ProtectContent.ShouldBeFalse();
        (await alice.ChatAsync(group)).ProtectContent.ShouldBeFalse();
    }
}
