using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class TypingTests(ApiFactory factory)
{
    [Fact]
    public async Task Typing_reaches_the_other_members_but_not_the_senders_own_devices()
    {
        var (sender, b, c) = (await factory.NewUserAsync(), await factory.NewUserAsync(), await factory.NewUserAsync());
        var senderTablet = await sender.SignInAgainAsync();
        var chatId = await sender.CreateGroupAsync(b, c);
        await using var senderHub = await RealtimeProbe.ConnectAsync(sender);
        await using var tabletHub = await RealtimeProbe.ConnectAsync(senderTablet);
        await using var bHub = await RealtimeProbe.ConnectAsync(b);
        await using var cHub = await RealtimeProbe.ConnectAsync(c);

        await senderHub.SendTypingAsync(chatId);

        var expected = new TypingEvent { ChatId = chatId, UserId = sender.Id, Action = TypingAction.Typing };
        (await bHub.WaitForAsync<TypingEvent>()).ShouldBe(expected);
        (await cHub.WaitForAsync<TypingEvent>()).ShouldBe(expected);
        await tabletHub.FlushAsync();
        await senderHub.FlushAsync();
        tabletHub.Received<TypingEvent>().ShouldBeEmpty();
        senderHub.Received<TypingEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Typing_in_a_private_chat_reaches_the_peer_with_the_action()
    {
        var sender = await factory.NewUserAsync();
        var peer = await factory.NewUserAsync();
        var chatId = await sender.CreatePrivateChatWithMessageAsync(peer);
        await using var senderHub = await RealtimeProbe.ConnectAsync(sender);
        await using var peerHub = await RealtimeProbe.ConnectAsync(peer);

        await senderHub.SendTypingAsync(chatId, TypingAction.RecordingVoice);

        (await peerHub.WaitForAsync<TypingEvent>()).ShouldBe(new TypingEvent { ChatId = chatId, UserId = sender.Id, Action = TypingAction.RecordingVoice });
    }

    [Fact]
    public async Task Typing_is_not_sent_to_members_who_blocked_the_sender()
    {
        var (sender, blocker, other) = (await factory.NewUserAsync(), await factory.NewUserAsync(), await factory.NewUserAsync());
        var chatId = await sender.CreateGroupAsync(blocker, other);
        await blocker.BlockAsync(sender);
        await using var senderHub = await RealtimeProbe.ConnectAsync(sender);
        await using var blockerHub = await RealtimeProbe.ConnectAsync(blocker);
        await using var otherHub = await RealtimeProbe.ConnectAsync(other);

        await senderHub.SendTypingAsync(chatId);

        (await otherHub.WaitForAsync<TypingEvent>()).UserId.ShouldBe(sender.Id);
        await blockerHub.FlushAsync();
        blockerHub.Received<TypingEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Users_blocked_by_the_sender_never_receive_the_blockers_typing()
    {
        var (blocker, blocked, other) = (await factory.NewUserAsync(), await factory.NewUserAsync(), await factory.NewUserAsync());
        var chatId = await blocker.CreateGroupAsync(blocked, other);
        await blocker.BlockAsync(blocked);
        await using var blockerHub = await RealtimeProbe.ConnectAsync(blocker);
        await using var blockedHub = await RealtimeProbe.ConnectAsync(blocked);
        await using var otherHub = await RealtimeProbe.ConnectAsync(other);

        await blockerHub.SendTypingAsync(chatId);

        (await otherHub.WaitForAsync<TypingEvent>()).UserId.ShouldBe(blocker.Id);
        await blockedHub.FlushAsync();
        blockedHub.Received<TypingEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_block_applies_to_typing_right_away_and_unblocking_restores_it()
    {
        var (sender, member) = (await factory.NewUserAsync(), await factory.NewUserAsync());
        var chatId = await sender.CreateGroupAsync(member);
        await using var memberHub = await RealtimeProbe.ConnectAsync(member);
        // One connection per step: the 300 ms typing throttle is per connection.
        await using var first = await RealtimeProbe.ConnectAsync(sender);
        await using var second = await RealtimeProbe.ConnectAsync(sender);
        await using var third = await RealtimeProbe.ConnectAsync(sender);

        // Warms the membership and blocker caches for this chat and sender.
        await first.SendTypingAsync(chatId);
        await memberHub.WaitForAsync<TypingEvent>();

        await member.BlockAsync(sender);
        await second.SendTypingAsync(chatId, TypingAction.UploadingPhoto);
        await memberHub.FlushAsync();
        memberHub.Received<TypingEvent>().Count.ShouldBe(1);

        await (await member.Client.Delete(ApiRoutes.Blocks.ByUserIdPath(sender.Id))).ShouldBeStatusAsync(System.Net.HttpStatusCode.NoContent);
        await third.SendTypingAsync(chatId, TypingAction.UploadingVideo);
        await memberHub.WaitForAsync<TypingEvent>(e => e.Action == TypingAction.UploadingVideo);
    }

    [Fact]
    public async Task Blocking_someone_stops_ones_own_typing_to_them_right_away()
    {
        var (blocker, member) = (await factory.NewUserAsync(), await factory.NewUserAsync());
        var chatId = await blocker.CreateGroupAsync(member);
        await using var memberHub = await RealtimeProbe.ConnectAsync(member);
        await using var first = await RealtimeProbe.ConnectAsync(blocker);
        await using var second = await RealtimeProbe.ConnectAsync(blocker);
        await using var third = await RealtimeProbe.ConnectAsync(blocker);

        // Warm cache: the blocker had no blocks yet.
        await first.SendTypingAsync(chatId);
        await memberHub.WaitForAsync<TypingEvent>();

        await blocker.BlockAsync(member);
        await second.SendTypingAsync(chatId, TypingAction.RecordingVideoNote);
        await memberHub.FlushAsync();
        memberHub.Received<TypingEvent>().Count.ShouldBe(1);

        await (await blocker.Client.Delete(ApiRoutes.Blocks.ByUserIdPath(member.Id))).ShouldBeStatusAsync(System.Net.HttpStatusCode.NoContent);
        await third.SendTypingAsync(chatId, TypingAction.UploadingVideo);
        await memberHub.WaitForAsync<TypingEvent>(e => e.Action == TypingAction.UploadingVideo);
    }

    [Fact]
    public async Task Non_members_cannot_send_typing_into_a_chat()
    {
        var (owner, member, outsider) = (await factory.NewUserAsync(), await factory.NewUserAsync(), await factory.NewUserAsync());
        var chatId = await owner.CreateGroupAsync(member);
        await using var memberHub = await RealtimeProbe.ConnectAsync(member);
        await using var ownerHub = await RealtimeProbe.ConnectAsync(owner);
        await using var outsiderHub = await RealtimeProbe.ConnectAsync(outsider);

        await outsiderHub.SendTypingAsync(chatId);
        await outsiderHub.SendTypingAsync(Guid.CreateVersion7());

        await memberHub.FlushAsync();
        await ownerHub.FlushAsync();
        memberHub.Received<TypingEvent>().ShouldBeEmpty();
        ownerHub.Received<TypingEvent>().ShouldBeEmpty();
        outsiderHub.Hub.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Typing_in_a_chat_the_peer_cannot_see_yet_or_in_saved_messages_goes_nowhere()
    {
        var sender = await factory.NewUserAsync();
        var peer = await factory.NewUserAsync();
        var hidden = await sender.Client.PostAs<ChatSummaryDto>(ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = peer.Id });
        var senderTablet = await sender.SignInAgainAsync();
        await using var senderHub = await RealtimeProbe.ConnectAsync(sender);
        await using var tabletHub = await RealtimeProbe.ConnectAsync(senderTablet);
        await using var peerHub = await RealtimeProbe.ConnectAsync(peer);

        await senderHub.SendTypingAsync(hidden.Id);
        await tabletHub.SendTypingAsync(sender.Auth.User.SavedMessagesChatId);

        await peerHub.FlushAsync();
        await senderHub.FlushAsync();
        await tabletHub.FlushAsync();
        peerHub.Received<TypingEvent>().ShouldBeEmpty();
        senderHub.Received<TypingEvent>().ShouldBeEmpty();
        tabletHub.Received<TypingEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Typing_is_throttled_per_connection_but_a_cancel_always_gets_through()
    {
        var (sender, member) = (await factory.NewUserAsync(), await factory.NewUserAsync());
        var chatId = await sender.CreateGroupAsync(member);
        await using var senderHub = await RealtimeProbe.ConnectAsync(sender);
        await using var warmUp = await RealtimeProbe.ConnectAsync(sender);
        await using var memberHub = await RealtimeProbe.ConnectAsync(member);
        // Another connection fills the shared membership/block caches, so the burst below needs no database.
        await warmUp.SendTypingAsync(chatId, TypingAction.UploadingFile);
        await memberHub.WaitForAsync<TypingEvent>(e => e.Action == TypingAction.UploadingFile);

        // Back to back (fire and forget): the server handles one connection's invocations in order, far within 300 ms.
        foreach (var action in new[] { TypingAction.Typing, TypingAction.Typing, TypingAction.Typing, TypingAction.Cancel, TypingAction.Cancel })
            await senderHub.Hub.SendAsync(nameof(IRealtimeServer.SendTyping), chatId, action, Ct);
        // Completes after all of the above were handled.
        await senderHub.SetActiveAsync(true);

        await memberHub.WaitForAsync<TypingEvent>(e => e.Action == TypingAction.Cancel);
        await memberHub.FlushAsync();
        memberHub.Received<TypingEvent>().Select(e => e.Action).ShouldBe([TypingAction.UploadingFile, TypingAction.Typing, TypingAction.Cancel]);
    }

    [Fact]
    public async Task Undefined_typing_actions_are_ignored()
    {
        var (sender, member) = (await factory.NewUserAsync(), await factory.NewUserAsync());
        var chatId = await sender.CreateGroupAsync(member);
        await using var senderHub = await RealtimeProbe.ConnectAsync(sender);
        await using var memberHub = await RealtimeProbe.ConnectAsync(member);

        await senderHub.SendTypingAsync(chatId, (TypingAction)99);
        // Ignored before the throttle: a valid action right after is still delivered.
        await senderHub.SendTypingAsync(chatId, TypingAction.UploadingFile);

        (await memberHub.WaitForAsync<TypingEvent>()).Action.ShouldBe(TypingAction.UploadingFile);
        await memberHub.FlushAsync();
        memberHub.Received<TypingEvent>().ShouldHaveSingleItem();
    }
}
