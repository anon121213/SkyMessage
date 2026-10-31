using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>Chat list summaries (unread, mentions, read receipts, last message) and per-user chat settings (SPEC §6.3).</summary>
[Collection(ApiCollection.Name)]
public sealed class ChatListTests(ApiFactory factory)
{
    [Fact]
    public async Task Unread_counts_and_read_receipts_move_forward_only()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.SendManyAsync(chat, 3);
        var aliceTablet = await alice.SecondDeviceAsync();
        await using var tabletRt = await aliceTablet.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        var mine = await alice.ChatAsync(chat);
        mine.UnreadCount.ShouldBe(3);
        mine.MyLastReadSeq.ShouldBe(0);
        mine.LastMessageSeq.ShouldBe(3);
        mine.OthersLastReadSeq.ShouldBe(3); // Bob's own messages count as read by him
        var his = await bob.ChatAsync(chat);
        his.UnreadCount.ShouldBe(0);
        his.MyLastReadSeq.ShouldBe(3);
        his.OthersLastReadSeq.ShouldBe(0);

        await alice.MarkReadAsync(chat, 2);
        foreach (var rt in new[] { tabletRt, bobRt })
        {
            var read = await rt.WaitAsync<MessagesReadEvent>(Ev.MessagesRead, e => e.ChatId == chat);
            read.UserId.ShouldBe(alice.Id);
            read.UpToSeq.ShouldBe(2);
        }
        (await alice.ChatAsync(chat)).UnreadCount.ShouldBe(1);
        (await aliceTablet.ChatAsync(chat)).MyLastReadSeq.ShouldBe(2);
        (await bob.ChatAsync(chat)).OthersLastReadSeq.ShouldBe(2);

        // Going back is ignored (and silent).
        await alice.MarkReadAsync(chat, 1);
        await alice.MarkReadAsync(chat, 2);
        await bobRt.BarrierAsync();
        bobRt.All<MessagesReadEvent>(Ev.MessagesRead).Count.ShouldBe(1);
        (await alice.ChatAsync(chat)).MyLastReadSeq.ShouldBe(2);

        // Beyond the end is clamped to the last message.
        await alice.MarkReadAsync(chat, 1_000);
        (await bobRt.WaitAsync<MessagesReadEvent>(Ev.MessagesRead, e => e.UpToSeq == 3)).UserId.ShouldBe(alice.Id);
        var after = await alice.ChatAsync(chat);
        after.UnreadCount.ShouldBe(0);
        after.MyLastReadSeq.ShouldBe(3);
        (await bob.DetailsAsync(chat)).Members.Single(m => m.User.Id == alice.Id).LastReadSeq.ShouldBe(3);

        await (await alice.PostRawAsync(ApiRoutes.Chats.ReadPath(chat), new MarkReadRequest { UpToSeq = -1 }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var outsider = await factory.NewUserAsync("Outsider");
        await (await outsider.PostRawAsync(ApiRoutes.Chats.ReadPath(chat), new MarkReadRequest { UpToSeq = 1 }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }

    [Fact]
    public async Task Sending_marks_my_own_history_as_read()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.SendManyAsync(chat, 4);
        (await alice.ChatAsync(chat)).UnreadCount.ShouldBe(4);

        var reply = await alice.SendAsync(chat, "answer");
        var mine = await alice.ChatAsync(chat);
        mine.MyLastReadSeq.ShouldBe(reply.Seq);
        mine.UnreadCount.ShouldBe(0);
        (await bob.ChatAsync(chat)).OthersLastReadSeq.ShouldBe(reply.Seq);
        (await bob.ChatAsync(chat)).UnreadCount.ShouldBe(1);
    }

    [Fact]
    public async Task Unread_and_last_message_skip_deleted_hidden_and_cleared_messages()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var m = await bob.SendManyAsync(chat, 5);

        await bob.DeleteMessagesAsync(chat, forEveryone: true, m[1].Id);
        await alice.DeleteMessagesAsync(chat, forEveryone: false, m[2].Id);
        var summary = await alice.ChatAsync(chat);
        summary.UnreadCount.ShouldBe(3);
        summary.LastMessage.ShouldNotBeNull().Id.ShouldBe(m[4].Id);
        summary.LastMessageSeq.ShouldBe(5);

        await bob.DeleteMessagesAsync(chat, forEveryone: true, m[4].Id);
        summary = await alice.ChatAsync(chat);
        summary.LastMessage.ShouldNotBeNull().Id.ShouldBe(m[3].Id);
        summary.LastMessageSender.ShouldNotBeNull().Id.ShouldBe(bob.Id);
        summary.UnreadCount.ShouldBe(2);

        await alice.DeleteMessagesAsync(chat, forEveryone: false, m[3].Id);
        summary = await alice.ChatAsync(chat);
        summary.LastMessage.ShouldNotBeNull().Id.ShouldBe(m[0].Id);
        summary.UnreadCount.ShouldBe(1);
        // Bob still sees his message that Alice hid for herself.
        (await bob.ChatAsync(chat)).LastMessage.ShouldNotBeNull().Id.ShouldBe(m[3].Id);

        await alice.ClearHistoryAsync(chat);
        summary = await alice.ChatAsync(chat);
        summary.LastMessage.ShouldBeNull();
        summary.LastMessageSender.ShouldBeNull();
        summary.UnreadCount.ShouldBe(0);
        summary.ClearedUpToSeq.ShouldBe(5);

        var next = await bob.SendAsync(chat, "after clearing");
        summary = await alice.ChatAsync(chat);
        summary.UnreadCount.ShouldBe(1);
        summary.LastMessage.ShouldNotBeNull().Id.ShouldBe(next.Id);
    }

    [Fact]
    public async Task Mentions_and_replies_count_as_unread_mentions()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var dave = await factory.NewUserAsync("Dave");
        var stranger = await factory.NewUserAsync("Stranger");
        var chat = await alice.NewGroupAsync(bob, carol, dave);
        var mine = await alice.SendAsync(chat, "my message");
        // Mentioning myself is not a mention (and sending marks my history read, so it goes first).
        await alice.SendAsync(chat, $"talking to myself @{alice.Username}");

        var byEntity = await bob.SendAsync(chat, "hey Alice", entities:
            [new MessageEntityDto { Type = MessageEntityType.Mention, Offset = 4, Length = 5, UserId = alice.Id }]);
        var byUsername = await carol.SendAsync(chat, $"look @{alice.Username.ToUpperInvariant()}!");
        var byReply = await dave.SendAsync(chat, "replying", replyTo: mine.Id);

        // None of these mention Alice: code spans, e-mail-like text, a non-member.
        var codeText = $"`@{alice.Username}`";
        await bob.SendAsync(chat, codeText, entities: [new MessageEntityDto { Type = MessageEntityType.Code, Offset = 0, Length = codeText.Length }]);
        await bob.SendAsync(chat, $"mail me at bob@{alice.Username}");
        await bob.SendAsync(chat, "stranger", entities:
            [new MessageEntityDto { Type = MessageEntityType.Mention, Offset = 0, Length = 8, UserId = stranger.Id }]);
        (await factory.WithDbAsync(db => db.MessageMentions.Where(x => x.ChatId == chat).Select(x => x.UserId).ToListAsync()))
            .ShouldBe([alice.Id, alice.Id, alice.Id]);

        var summary = await alice.ChatAsync(chat);
        summary.UnreadMentionCount.ShouldBe(3);
        summary.FirstUnreadMentionSeq.ShouldBe(byEntity.Seq);
        (await alice.MentionsAsync(chat)).ShouldBe([byEntity.Seq, byUsername.Seq, byReply.Seq]);
        (await bob.ChatAsync(chat)).UnreadMentionCount.ShouldBe(0);
        (await stranger.GetRawAsync(ApiRoutes.Messages.MentionsPath(chat))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Reading moves the window.
        await alice.MarkReadAsync(chat, byUsername.Seq);
        summary = await alice.ChatAsync(chat);
        summary.UnreadMentionCount.ShouldBe(1);
        summary.FirstUnreadMentionSeq.ShouldBe(byReply.Seq);
        (await alice.MentionsAsync(chat)).ShouldBe([byReply.Seq]);

        // Deleting the mentioning message for everyone removes the mention; hiding it for me too.
        await dave.DeleteMessagesAsync(chat, forEveryone: true, byReply.Id);
        (await alice.ChatAsync(chat)).UnreadMentionCount.ShouldBe(0);
        (await alice.ChatAsync(chat)).FirstUnreadMentionSeq.ShouldBeNull();
        (await alice.MentionsAsync(chat)).ShouldBeEmpty();

        var another = await carol.SendAsync(chat, $"@{alice.Username} again");
        (await alice.MentionsAsync(chat)).ShouldBe([another.Seq]);
        await alice.DeleteMessagesAsync(chat, forEveryone: false, another.Id);
        (await alice.MentionsAsync(chat)).ShouldBeEmpty();
        (await alice.ChatAsync(chat)).UnreadMentionCount.ShouldBe(0);
    }

    [Fact]
    public async Task Editing_reindexes_mentions()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = await alice.NewGroupAsync(bob, carol);
        await alice.MarkReadAsync(chat, 1);

        var message = await bob.SendAsync(chat, "hello everyone");
        (await alice.ChatAsync(chat)).UnreadMentionCount.ShouldBe(0);

        await bob.EditAsync(chat, message.Id, $"hello @{alice.Username}");
        (await alice.MentionsAsync(chat)).ShouldBe([message.Seq]);
        (await carol.MentionsAsync(chat)).ShouldBeEmpty();

        await bob.EditAsync(chat, message.Id, $"hello @{carol.Username}");
        (await alice.MentionsAsync(chat)).ShouldBeEmpty();
        (await carol.MentionsAsync(chat)).ShouldBe([message.Seq]);
        (await carol.ChatAsync(chat)).UnreadMentionCount.ShouldBe(1);
    }

    [Fact]
    public async Task Group_summaries_report_the_furthest_reader_and_the_last_sender()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = await alice.NewGroupAsync(bob, carol);

        var a = await alice.SendAsync(chat, "from Alice");
        var b = await bob.SendAsync(chat, "from Bob");
        var c = await carol.SendAsync(chat, "from Carol");
        await bob.MarkReadAsync(chat, c.Seq);

        var summary = await alice.ChatAsync(chat);
        summary.OthersLastReadSeq.ShouldBe(c.Seq);
        summary.MyLastReadSeq.ShouldBe(a.Seq);
        summary.UnreadCount.ShouldBe(2);
        summary.LastMessage.ShouldNotBeNull().Id.ShouldBe(c.Id);
        summary.LastMessageSender.ShouldNotBeNull().Id.ShouldBe(carol.Id);
        summary.LastMessageSender.DisplayName.ShouldBe("Carol");
        (await carol.ChatAsync(chat)).OthersLastReadSeq.ShouldBe(c.Seq); // Bob read up to Carol's message
        var details = await alice.DetailsAsync(chat);
        details.Members.Single(m => m.User.Id == bob.Id).LastReadSeq.ShouldBe(c.Seq);
        details.Members.Single(m => m.User.Id == alice.Id).LastReadSeq.ShouldBe(a.Seq);
        b.Seq.ShouldBeLessThan(c.Seq);

        // A service message has no sender.
        await alice.UpdateChatAsync(chat, new UpdateChatRequest { Title = "Renamed" });
        summary = await bob.ChatAsync(chat);
        summary.LastMessage.ShouldNotBeNull().Type.ShouldBe(MessageType.System);
        summary.LastMessageSender.ShouldBeNull();
        summary.Title.ShouldBe("Renamed");
    }

    [Fact]
    public async Task Private_chat_title_uses_my_contact_name_for_the_peer()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await alice.AddContactAsync(bob.Id, "Bobby (work)");

        var summary = (await alice.ChatsAsync()).Single(c => c.Id == chat);
        summary.Title.ShouldBe("Bobby (work)");
        summary.Peer.ShouldNotBeNull().ContactName.ShouldBe("Bobby (work)");
        summary.Peer.IsContact.ShouldBeTrue();
    }

    [Fact]
    public async Task Private_chat_avatar_follows_the_peers_profile_photo_privacy()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var photo = await factory.CreateMediaAsync(bob, MediaKind.Photo);
        await (await bob.PutRawAsync(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = photo })).ShouldSucceedAsync();

        var visible = await alice.ChatAsync(chat);
        visible.AvatarMediaId.ShouldBe(photo);
        visible.Peer.ShouldNotBeNull().AvatarMediaId.ShouldBe(photo);

        await bob.SetPrivacyAsync(p => p with { ProfilePhoto = PrivacyLevel.Nobody });
        var hidden = (await alice.ChatsAsync()).Single(c => c.Id == chat);
        hidden.AvatarMediaId.ShouldBeNull();
        hidden.Peer.ShouldNotBeNull().AvatarMediaId.ShouldBeNull();
    }

    [Fact]
    public async Task Pinned_chats_come_first_then_the_most_recent_activity()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var withBob = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var withCarol = (await alice.OpenPrivateAsync(carol.Id)).Id;
        var group = await alice.NewGroupAsync(bob, carol);
        await bob.SendAsync(withBob, "1");
        await carol.SendAsync(withCarol, "2");
        await bob.SendAsync(group, "3");

        async Task<List<Guid>> Order() => (await alice.ChatsAsync()).Select(c => c.Id).ToList();

        (await Order()).ShouldBe([group, withCarol, withBob, alice.SavedChatId]);

        var pinned = await alice.UpdateSettingsAsync(withBob, new UpdateChatSettingsRequest { IsPinned = true });
        pinned.IsPinned.ShouldBeTrue();
        pinned.PinnedAt.ShouldNotBeNull();
        (await Order()).ShouldBe([withBob, group, withCarol, alice.SavedChatId]);

        // The most recently pinned chat goes on top.
        await alice.UpdateSettingsAsync(withCarol, new UpdateChatSettingsRequest { IsPinned = true });
        (await Order()).ShouldBe([withCarol, withBob, group, alice.SavedChatId]);

        // New activity does not move unpinned chats above pinned ones.
        await bob.SendAsync(group, "4");
        (await Order()).ShouldBe([withCarol, withBob, group, alice.SavedChatId]);

        var unpinned = await alice.UpdateSettingsAsync(withCarol, new UpdateChatSettingsRequest { IsPinned = false });
        unpinned.IsPinned.ShouldBeFalse();
        unpinned.PinnedAt.ShouldBeNull();
        (await Order()).ShouldBe([withBob, group, withCarol, alice.SavedChatId]);

        // Archived chats stay in the list (flagged); the client files them away.
        await alice.UpdateSettingsAsync(group, new UpdateChatSettingsRequest { IsArchived = true });
        var list = await alice.ChatsAsync();
        list.Select(c => c.Id).ShouldBe([withBob, group, withCarol, alice.SavedChatId]);
        list.Single(c => c.Id == group).IsArchived.ShouldBeTrue();
        list.Single(c => c.Id == withBob).IsPinned.ShouldBeTrue();

        // Settings are per user.
        var bobsView = await bob.ChatAsync(group);
        bobsView.IsArchived.ShouldBeFalse();
        (await bob.ChatAsync(withBob)).IsPinned.ShouldBeFalse();
    }

    [Fact]
    public async Task Mute_settings_and_their_events_reach_only_my_devices()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.SendAsync(chat, "hi");
        var aliceTablet = await alice.SecondDeviceAsync();
        await using var tabletRt = await aliceTablet.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        var forever = await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { MuteForSeconds = -1 });
        forever.MutedUntil.ShouldBe(MuteRules.Forever);
        var e = await tabletRt.WaitAsync<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, x => x.ChatId == chat);
        e.Settings.MutedUntil.ShouldBe(MuteRules.Forever);
        (await aliceTablet.ChatAsync(chat)).MutedUntil.ShouldBe(MuteRules.Forever);

        var before = TestKit.Now();
        var hour = await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { MuteForSeconds = 3600 });
        hour.MutedUntil.ShouldNotBeNull().ShouldBeInRange(before.AddSeconds(3600), TestKit.Now().AddSeconds(3600));
        await tabletRt.WaitAsync<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, x => x.ChatId == chat && x.Settings.MutedUntil == hour.MutedUntil);

        (await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { MuteForSeconds = 0 })).MutedUntil.ShouldBeNull();
        await tabletRt.WaitAsync<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, x => x.ChatId == chat && x.Settings.MutedUntil == null);
        (await alice.ChatAsync(chat)).MutedUntil.ShouldBeNull();

        // A no-op change is silent.
        await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { MuteForSeconds = 0, IsArchived = false, IsPinned = false });
        await tabletRt.BarrierAsync();
        tabletRt.All<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, x => x.ChatId == chat).Count.ShouldBe(3);

        await bobRt.BarrierAsync();
        bobRt.All<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, x => x.ChatId == chat).ShouldBeEmpty();
        (await bob.ChatAsync(chat)).MutedUntil.ShouldBeNull();

        await (await alice.PutRawAsync(ApiRoutes.Chats.SettingsPath(chat), new UpdateChatSettingsRequest { MuteForSeconds = -2 }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var outsider = await factory.NewUserAsync("Outsider");
        await (await outsider.PutRawAsync(ApiRoutes.Chats.SettingsPath(chat), new UpdateChatSettingsRequest { IsPinned = true }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }
}
