using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Application.Messaging.Services;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>Private chats (hidden for the peer until the first message), privacy and Saved Messages (SPEC §6.3).</summary>
[Collection(ApiCollection.Name)]
public sealed class PrivateChatTests(ApiFactory factory)
{
    [Fact]
    public async Task Private_chat_is_hidden_for_the_peer_until_the_first_message()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        await using var aliceRt = await alice.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        var chat = await alice.OpenPrivateAsync(bob.Id);
        chat.Type.ShouldBe(ChatType.Private);
        chat.Peer.ShouldNotBeNull().Id.ShouldBe(bob.Id);
        chat.Title.ShouldBe("Bob");
        chat.MemberCount.ShouldBe(2);
        chat.MyRole.ShouldBe(ChatRole.Member);
        chat.LastMessage.ShouldBeNull();
        chat.LastMessageSeq.ShouldBe(0);
        await aliceRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat.Id);

        (await alice.ChatsAsync()).ShouldContain(c => c.Id == chat.Id);
        (await bob.ChatsAsync()).ShouldNotContain(c => c.Id == chat.Id);
        await bobRt.BarrierAsync();
        bobRt.EventsFor(chat.Id).ShouldBeEmpty();

        var first = await alice.SendAsync(chat.Id, "hi Bob");
        first.Seq.ShouldBe(1);
        first.SenderId.ShouldBe(alice.Id);
        first.Type.ShouldBe(MessageType.Text);
        (await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == first.Id)).Text.ShouldBe("hi Bob");
        await bobRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat.Id);
        await aliceRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == first.Id);

        var bobChat = (await bob.ChatsAsync()).Single(c => c.Id == chat.Id);
        bobChat.Title.ShouldBe("Alice");
        bobChat.Peer.ShouldNotBeNull().Id.ShouldBe(alice.Id);
        bobChat.UnreadCount.ShouldBe(1);
        bobChat.LastMessage.ShouldNotBeNull().Id.ShouldBe(first.Id);
        bobChat.LastMessageSender.ShouldNotBeNull().Id.ShouldBe(alice.Id);
        bobChat.LastMessageSeq.ShouldBe(1);

        // Only the first message unhides: later ones are plain MessageCreated.
        var second = await alice.SendAsync(chat.Id, "again");
        await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == second.Id);
        await bobRt.BarrierAsync();
        await aliceRt.BarrierAsync();
        bobRt.All<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat.Id).Count.ShouldBe(1);
        aliceRt.All<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat.Id).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Opening_again_returns_the_same_chat_and_unhides_it_only_for_the_caller()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        await using var bobRt = await bob.ConnectAsync();

        var chat = await alice.OpenPrivateAsync(bob.Id);
        (await alice.OpenPrivateAsync(bob.Id)).Id.ShouldBe(chat.Id);

        // Bob opens the conversation from Alice's profile before anything was written: same chat, now visible to him.
        var fromBob = await bob.OpenPrivateAsync(alice.Id);
        fromBob.Id.ShouldBe(chat.Id);
        fromBob.Peer.ShouldNotBeNull().Id.ShouldBe(alice.Id);
        await bobRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat.Id);
        (await bob.ChatsAsync()).ShouldContain(c => c.Id == chat.Id);

        // Already visible: no second ChatAdded.
        (await bob.OpenPrivateAsync(alice.Id)).Id.ShouldBe(chat.Id);
        await bobRt.BarrierAsync();
        bobRt.All<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat.Id).Count.ShouldBe(1);

        var rows = await factory.WithDbAsync(db => db.Chats.CountAsync(c => c.PrivateKey == Chat.PrivateKeyFor(alice.Id, bob.Id)));
        rows.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_opens_from_both_sides_create_exactly_one_chat()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");

        var results = await Task.WhenAll(
            alice.OpenPrivateAsync(bob.Id), bob.OpenPrivateAsync(alice.Id),
            alice.OpenPrivateAsync(bob.Id), bob.OpenPrivateAsync(alice.Id),
            alice.OpenPrivateAsync(bob.Id), bob.OpenPrivateAsync(alice.Id));

        var chatId = results[0].Id;
        results.ShouldAllBe(r => r.Id == chatId);
        var (chats, members) = await factory.WithDbAsync(async db => (
            await db.Chats.CountAsync(c => c.PrivateKey == Chat.PrivateKeyFor(alice.Id, bob.Id)),
            await db.ChatMembers.CountAsync(m => m.ChatId == chatId)));
        chats.ShouldBe(1);
        members.ShouldBe(2);
        (await alice.ChatAsync(chatId)).MemberCount.ShouldBe(2);

        // Both opened it, so both see it.
        (await alice.ChatsAsync()).ShouldContain(c => c.Id == chatId);
        (await bob.ChatsAsync()).ShouldContain(c => c.Id == chatId);
    }

    [Fact]
    public async Task Opening_a_chat_with_myself_returns_saved_messages()
    {
        var alice = await factory.NewUserAsync("Alice");

        var saved = await alice.OpenPrivateAsync(alice.Id);
        saved.Id.ShouldBe(alice.SavedChatId);
        saved.Type.ShouldBe(ChatType.Saved);
        saved.MemberCount.ShouldBe(1);
        saved.MyRole.ShouldBe(ChatRole.Owner);
        saved.Peer.ShouldBeNull();
        saved.Title.ShouldBe(ChatSummaryBuilder.SavedMessagesTitle);

        var list = await alice.ChatsAsync();
        list.Count(c => c.Type == ChatType.Saved).ShouldBe(1);
        list.Single(c => c.Type == ChatType.Saved).Id.ShouldBe(alice.SavedChatId);
        (await alice.GetAsync<MeDto>(ApiRoutes.Users.Me)).SavedMessagesChatId.ShouldBe(alice.SavedChatId);
    }

    [Fact]
    public async Task Saved_messages_are_always_read_and_never_mention_anyone()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var saved = alice.SavedChatId;

        var note = await alice.SendAsync(saved, $"note to self @{alice.Username} and @{bob.Username}", entities:
        [
            new MessageEntityDto { Type = MessageEntityType.Mention, Offset = 0, Length = 4, UserId = bob.Id },
        ]);
        await alice.PinAsync(saved, note.Id);

        var summary = await alice.ChatAsync(saved);
        summary.UnreadCount.ShouldBe(0);
        summary.UnreadMentionCount.ShouldBe(0);
        summary.LastMessageSeq.ShouldBe(2); // note + "pinned" service message
        summary.OthersLastReadSeq.ShouldBe(summary.LastMessageSeq);
        summary.MyLastReadSeq.ShouldBe(2);
        (await alice.MentionsAsync(saved)).ShouldBeEmpty();
        (await factory.WithDbAsync(db => db.MessageMentions.CountAsync(x => x.ChatId == saved))).ShouldBe(0);

        // Deleting my own notes for everyone works in Saved Messages.
        await alice.DeleteMessagesAsync(saved, forEveryone: true, note.Id);
        (await alice.GetMessageAsync(saved, note.Id)).IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Private_chat_with_unknown_or_empty_user_is_rejected()
    {
        var alice = await factory.NewUserAsync("Alice");
        await (await alice.TryOpenPrivateAsync(Guid.NewGuid())).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
        await (await alice.TryOpenPrivateAsync(Guid.Empty)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var ghost = await factory.NewUserAsync("Ghost");
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == ghost.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true)));
        await (await alice.TryOpenPrivateAsync(ghost.Id)).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Messages_privacy_restricts_new_private_chats_to_contacts()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        await bob.SetPrivacyAsync(p => p with { Messages = PrivacyLevel.Contacts });

        await (await alice.TryOpenPrivateAsync(bob.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.PrivacyRestricted);

        await bob.AddContactAsync(alice.Id);
        var chat = await alice.OpenPrivateAsync(bob.Id);
        (await alice.SendAsync(chat.Id, "hello friend")).Seq.ShouldBe(1);

        // Nobody behaves like Contacts.
        var carol = await factory.NewUserAsync("Carol");
        await carol.SetPrivacyAsync(p => p with { Messages = PrivacyLevel.Nobody });
        await (await alice.TryOpenPrivateAsync(carol.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.PrivacyRestricted);
        await carol.AddContactAsync(alice.Id);
        (await alice.OpenPrivateAsync(carol.Id)).Type.ShouldBe(ChatType.Private);
    }

    [Fact]
    public async Task Blocks_either_way_forbid_opening_and_sending()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");

        var chat = await alice.OpenPrivateAsync(bob.Id);
        await alice.SendAsync(chat.Id, "before the block");
        await bob.BlockAsync(alice.Id);

        await (await alice.TrySendAsync(chat.Id, MessagingApi.Text("after"))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        await (await bob.TrySendAsync(chat.Id, MessagingApi.Text("after"))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        (await alice.ChatAsync(chat.Id)).IsBlocked.ShouldBeTrue();
        (await bob.ChatAsync(chat.Id)).IsBlocked.ShouldBeTrue();

        // New chats: the blocker and the blocked user both get user_blocked.
        await carol.BlockAsync(alice.Id);
        await (await alice.TryOpenPrivateAsync(carol.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        await (await carol.TryOpenPrivateAsync(alice.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
    }

    [Fact]
    public async Task Tightened_privacy_still_lets_me_answer_someone_who_wrote_first()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await alice.OpenPrivateAsync(bob.Id);
        await alice.SendAsync(chat.Id, "hi");

        // Bob closes his messages: Alice (not his contact, Bob never wrote) can no longer write...
        await bob.SetPrivacyAsync(p => p with { Messages = PrivacyLevel.Nobody });
        await (await alice.TrySendAsync(chat.Id, MessagingApi.Text("still there?"))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.PrivacyRestricted);

        // ...but once Bob writes himself, replying is allowed again.
        await bob.SendAsync(chat.Id, "yes");
        (await alice.SendAsync(chat.Id, "great")).Seq.ShouldBe(3);
    }

    [Fact]
    public async Task Delete_chat_for_me_hides_history_until_the_next_message()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.OpenPrivateAsync(alice.Id);
        await bob.SendManyAsync(chat, 3);
        await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { IsPinned = true, IsArchived = true });
        await using var aliceRt = await alice.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        await alice.DeleteChatAsync(chat, forEveryone: false);

        await aliceRt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);
        (await alice.ChatsAsync()).ShouldNotContain(c => c.Id == chat);
        var hidden = await alice.ChatAsync(chat);
        hidden.ClearedUpToSeq.ShouldBe(3);
        hidden.UnreadCount.ShouldBe(0);
        hidden.LastMessage.ShouldBeNull();
        hidden.IsPinned.ShouldBeFalse();
        hidden.IsArchived.ShouldBeFalse();
        (await alice.PageAsync(chat)).Messages.ShouldBeEmpty();

        // Bob keeps everything and is not told.
        (await bob.PageAsync(chat)).Messages.Count.ShouldBe(3);
        await bobRt.BarrierAsync();
        bobRt.All<ChatRefEvent>(Ev.ChatRemoved).ShouldBeEmpty();

        // Bob's next message brings the chat back for Alice with only the new message.
        var fresh = await bob.SendAsync(chat, "are you there?");
        await aliceRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat);
        var back = (await alice.ChatsAsync()).Single(c => c.Id == chat);
        back.UnreadCount.ShouldBe(1);
        back.ClearedUpToSeq.ShouldBe(3);
        back.LastMessage.ShouldNotBeNull().Id.ShouldBe(fresh.Id);
        (await alice.PageAsync(chat)).Messages.Select(m => m.Id).ShouldBe([fresh.Id]);
    }

    [Fact]
    public async Task Delete_chat_for_everyone_removes_it_for_both_sides()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await alice.SendAsync(chat, "soon gone");
        await using var aliceRt = await alice.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        await bob.DeleteChatAsync(chat, forEveryone: true);

        await aliceRt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);
        await bobRt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);
        await (await alice.GetRawAsync(ApiRoutes.Chats.ByIdPath(chat))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await bob.GetRawAsync(ApiRoutes.Messages.PagePath(chat))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);

        // A new conversation starts from scratch.
        var again = await alice.OpenPrivateAsync(bob.Id);
        again.Id.ShouldNotBe(chat);
        again.LastMessageSeq.ShouldBe(0);
    }

    [Fact]
    public async Task Clear_history_for_me_and_for_both_sides()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.SendManyAsync(chat, 2);
        await alice.SendAsync(chat, "three");
        await using var aliceRt = await alice.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        await alice.ClearHistoryAsync(chat);
        var cleared = await aliceRt.WaitAsync<HistoryClearedEvent>(Ev.HistoryCleared, e => e.ChatId == chat);
        cleared.UpToSeq.ShouldBe(3);
        cleared.ForEveryone.ShouldBeFalse();
        await bobRt.BarrierAsync();
        bobRt.All<HistoryClearedEvent>(Ev.HistoryCleared).ShouldBeEmpty();

        var mine = await alice.ChatAsync(chat);
        mine.ClearedUpToSeq.ShouldBe(3);
        mine.LastMessage.ShouldBeNull();
        mine.UnreadCount.ShouldBe(0);
        (await alice.ChatsAsync()).ShouldContain(c => c.Id == chat); // clearing does not hide the chat
        (await alice.PageAsync(chat)).Messages.ShouldBeEmpty();
        (await bob.PageAsync(chat)).Messages.Count.ShouldBe(3);
        (await bob.ChatAsync(chat)).ClearedUpToSeq.ShouldBe(0);

        var fourth = await bob.SendAsync(chat, "four");
        await bob.ClearHistoryAsync(chat, forEveryone: true);
        (await aliceRt.WaitAsync<HistoryClearedEvent>(Ev.HistoryCleared, e => e.ForEveryone)).UpToSeq.ShouldBe(fourth.Seq);
        (await bobRt.WaitAsync<HistoryClearedEvent>(Ev.HistoryCleared, e => e.ForEveryone)).UpToSeq.ShouldBe(fourth.Seq);
        (await alice.PageAsync(chat)).Messages.ShouldBeEmpty();
        (await bob.PageAsync(chat)).Messages.ShouldBeEmpty();
        (await alice.ChatAsync(chat)).ClearedUpToSeq.ShouldBe(fourth.Seq);
        (await bob.ChatAsync(chat)).ClearedUpToSeq.ShouldBe(fourth.Seq);
    }

    [Fact]
    public async Task Group_only_operations_are_rejected_in_private_chats()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;

        await (await alice.PatchRawAsync(ApiRoutes.Chats.ByIdPath(chat), new UpdateChatRequest { Title = "renamed" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.NotAGroup);
        await (await alice.TryAddMembersAsync(chat, carol.Id)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.NotAGroup);
        await (await alice.PostEmptyAsync(ApiRoutes.Chats.LeavePath(chat))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.NotAGroup);
        await (await alice.PostEmptyAsync(ApiRoutes.Chats.InviteLinkPath(chat))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.NotAGroup);
        await (await alice.TrySetRoleAsync(chat, bob.Id, ChatRole.Admin)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.NotAGroup);
        await (await alice.ClearHistoryRawAsync(alice.SavedChatId, forEveryone: true)).ShouldSucceedAsync(); // Saved: only mine

        // Outsiders learn nothing about the chat.
        await (await carol.GetRawAsync(ApiRoutes.Chats.ByIdPath(chat))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await carol.TrySendAsync(chat, MessagingApi.Text("let me in"))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await carol.GetRawAsync(ApiRoutes.Chats.ByIdPath(Guid.NewGuid()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }
}

internal static class ChatTestExtensions
{
    public static Task<HttpResponseMessage> ClearHistoryRawAsync(this TestUser user, Guid chatId, bool forEveryone) =>
        user.PostRawAsync(ApiRoutes.Chats.ClearHistoryPath(chatId), new ClearHistoryRequest { ForEveryone = forEveryone });
}
