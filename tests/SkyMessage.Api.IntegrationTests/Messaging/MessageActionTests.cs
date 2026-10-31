using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>Edit, delete, reactions, pins and forwarding (SPEC §6.3, §7).</summary>
[Collection(ApiCollection.Name)]
public sealed class MessageActionTests(ApiFactory factory)
{
    private Task AgeMessageAsync(Guid messageId, TimeSpan age) =>
        factory.WithDbAsync(db => db.Messages.Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, TestKit.Now() - age)));

    // ---- edit ----

    [Fact]
    public async Task Editing_my_message_marks_it_edited_and_notifies_the_chat()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var message = await alice.SendAsync(chat, "frist");
        await using var bobRt = await bob.ConnectAsync();

        var edited = await alice.EditAsync(chat, message.Id, "first", [new MessageEntityDto { Type = MessageEntityType.Italic, Offset = 0, Length = 5 }]);
        edited.Text.ShouldBe("first");
        edited.Entities.ShouldNotBeNull().Single().Type.ShouldBe(MessageEntityType.Italic);
        edited.EditedAt.ShouldNotBeNull().ShouldBeGreaterThan(message.CreatedAt);
        edited.UpdatedAt.ShouldBe(edited.EditedAt.Value);
        edited.CreatedAt.ShouldBe(message.CreatedAt);
        edited.Seq.ShouldBe(message.Seq);
        (await bobRt.WaitAsync<MessageDto>(Ev.MessageUpdated, m => m.Id == message.Id)).Text.ShouldBe("first");

        // Saving the same content again is not an edit.
        var same = await alice.EditAsync(chat, message.Id, "first", [new MessageEntityDto { Type = MessageEntityType.Italic, Offset = 0, Length = 5 }]);
        same.EditedAt.ShouldBe(edited.EditedAt);
        same.UpdatedAt.ShouldBe(edited.UpdatedAt);
        await bobRt.BarrierAsync();
        bobRt.All<MessageDto>(Ev.MessageUpdated, m => m.Id == message.Id).Count.ShouldBe(1);

        // Formatting alone is a change.
        var plain = await alice.EditAsync(chat, message.Id, "first");
        plain.Entities.ShouldBeNull();
        plain.EditedAt.ShouldNotBe(edited.EditedAt);
    }

    [Fact]
    public async Task Only_my_own_user_content_is_editable()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await alice.NewGroupAsync(bob);
        var created = (await alice.PageAsync(chat)).Messages.Single();
        var bobs = await bob.SendAsync(chat, "bob's");
        var forwarded = (await alice.ForwardAsync(chat, new ForwardMessagesRequest
        {
            FromChatId = chat,
            MessageIds = [bobs.Id],
            ClientMessageIds = [Guid.NewGuid()],
        })).Single();

        await (await alice.TryEditAsync(chat, bobs.Id, "hijacked")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.MessageNotEditable);
        await (await alice.TryEditAsync(chat, created.Id, "renamed")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.MessageNotEditable);
        await (await alice.TryEditAsync(chat, forwarded.Id, "rewritten")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.MessageNotEditable);
        await (await alice.TryEditAsync(chat, Guid.NewGuid(), "ghost")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);

        await bob.DeleteMessagesAsync(chat, forEveryone: true, bobs.Id);
        await (await bob.TryEditAsync(chat, bobs.Id, "resurrected")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);

        var outsider = await factory.NewUserAsync("Outsider");
        await (await outsider.TryEditAsync(chat, created.Id, "x")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }

    [Fact]
    public async Task Edits_are_allowed_within_the_edit_window_only()
    {
        var alice = await factory.NewUserAsync("Alice");
        var recent = await alice.SendAsync(alice.SavedChatId, "recent");
        var old = await alice.SendAsync(alice.SavedChatId, "old");
        await AgeMessageAsync(recent.Id, TimeSpan.FromHours(Limits.EditWindowHours) - TimeSpan.FromMinutes(5));
        await AgeMessageAsync(old.Id, TimeSpan.FromHours(Limits.EditWindowHours) + TimeSpan.FromMinutes(5));

        (await alice.EditAsync(alice.SavedChatId, recent.Id, "recent, edited")).Text.ShouldBe("recent, edited");
        await (await alice.TryEditAsync(alice.SavedChatId, old.Id, "too late")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.MessageEditExpired);
        (await alice.GetMessageAsync(alice.SavedChatId, old.Id)).Text.ShouldBe("old");
    }

    [Fact]
    public async Task Edits_cannot_empty_a_message_without_attachments()
    {
        var alice = await factory.NewUserAsync("Alice");
        var text = await alice.SendAsync(alice.SavedChatId, "words");
        var photo = await alice.SendAsync(alice.SavedChatId, new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = "caption",
            MediaIds = [await factory.CreateMediaAsync(alice, MediaKind.Photo)],
        });

        await (await alice.TryEditAsync(alice.SavedChatId, text.Id, "  ")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MessageEmpty);
        await (await alice.TryEditAsync(alice.SavedChatId, text.Id, null)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MessageEmpty);
        await (await alice.TryEditAsync(alice.SavedChatId, text.Id, new string('x', Limits.MessageTextMaxLength + 1)))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var uncaptioned = await alice.EditAsync(alice.SavedChatId, photo.Id, " ");
        uncaptioned.Text.ShouldBeNull();
        uncaptioned.Attachments.Count.ShouldBe(1);
        uncaptioned.Type.ShouldBe(MessageType.Photo);
    }

    [Fact]
    public async Task Edits_follow_the_same_send_rules_as_new_messages()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var group = await owner.NewGroupAsync(bob);
        var inGroup = await bob.SendAsync(group, "draft");
        await owner.SetPermissionsAsync(group, new ChatPermissionsDto { SendMessages = false });
        await (await bob.TryEditAsync(group, inGroup.Id, "final")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.SendForbidden);

        var carol = await factory.NewUserAsync("Carol");
        var chat = (await bob.OpenPrivateAsync(carol.Id)).Id;
        var inPrivate = await bob.SendAsync(chat, "hi");
        await carol.BlockAsync(bob.Id);
        await (await bob.TryEditAsync(chat, inPrivate.Id, "hello")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
    }

    // ---- delete ----

    [Fact]
    public async Task In_private_chats_either_side_deletes_any_message_for_everyone()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var mine = await alice.SendAsync(chat, "mine");
        var his = await bob.SendAsync(chat, "his");
        await using var aliceRt = await alice.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        await alice.DeleteMessagesAsync(chat, forEveryone: true, his.Id, mine.Id);

        foreach (var rt in new[] { aliceRt, bobRt })
        {
            var deleted = await rt.WaitAsync<MessagesDeletedEvent>(Ev.MessagesDeleted, e => e.ChatId == chat);
            deleted.ForEveryone.ShouldBeTrue();
            deleted.MessageIds.ShouldBe([mine.Id, his.Id], ignoreOrder: true);
        }
        var tombstone = await bob.GetMessageAsync(chat, his.Id);
        tombstone.IsDeleted.ShouldBeTrue();
        tombstone.Text.ShouldBeNull();
        tombstone.SenderId.ShouldBe(bob.Id);
        tombstone.UpdatedAt.ShouldBeGreaterThan(his.UpdatedAt);
        (await bob.PageAsync(chat)).Messages.ShouldBeEmpty();
        (await bob.ChatAsync(chat)).LastMessage.ShouldBeNull();

        // Deleting again (or unknown ids) is a silent no-op.
        await alice.DeleteMessagesAsync(chat, forEveryone: true, his.Id, Guid.NewGuid());
        await bobRt.BarrierAsync();
        bobRt.All<MessagesDeletedEvent>(Ev.MessagesDeleted).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Chat_last_message_pointer_survives_deletions()
    {
        var alice = await factory.NewUserAsync("Alice");
        var chat = alice.SavedChatId;
        var m = await alice.SendManyAsync(chat, 3);
        Task<Guid?> LastMessageId() => factory.WithDbAsync(db => db.Chats.Where(c => c.Id == chat).Select(c => c.LastMessageId).SingleAsync());

        (await LastMessageId()).ShouldBe(m[2].Id);
        await alice.DeleteMessagesAsync(chat, forEveryone: true, m[0].Id);
        (await LastMessageId()).ShouldBe(m[2].Id);
        await alice.DeleteMessagesAsync(chat, forEveryone: true, m[2].Id);
        (await LastMessageId()).ShouldBe(m[1].Id);
        await alice.DeleteMessagesAsync(chat, forEveryone: true, m[1].Id);
        (await LastMessageId()).ShouldBeNull();
        var next = await alice.SendAsync(chat, "fresh");
        (await LastMessageId()).ShouldBe(next.Id);
        next.Seq.ShouldBe(4); // seqs are never reused
    }

    [Fact]
    public async Task In_groups_only_admins_delete_other_peoples_messages()
    {
        var owner = await factory.NewUserAsync("Owner");
        var admin = await factory.NewUserAsync("Admin");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = await owner.NewGroupAsync(admin, bob, carol);
        await owner.SetRoleAsync(chat, admin.Id, ChatRole.Admin);
        var service = (await owner.PageAsync(chat)).Messages.Single(m => m.Type == MessageType.System);
        var bobs = await bob.SendAsync(chat, "bob 1");
        var bobs2 = await bob.SendAsync(chat, "bob 2");
        var carols = await carol.SendAsync(chat, "carol");
        var admins = await admin.SendAsync(chat, "admin");

        await (await carol.TryDeleteMessagesAsync(chat, true, bobs.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        // All or nothing: one foreign message fails the whole batch.
        await (await carol.TryDeleteMessagesAsync(chat, true, carols.Id, bobs.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        (await carol.GetMessageAsync(chat, carols.Id)).IsDeleted.ShouldBeFalse();
        await (await carol.TryDeleteMessagesAsync(chat, true, service.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await (await bob.TryDeleteMessagesAsync(chat, true, admins.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        await carol.DeleteMessagesAsync(chat, forEveryone: true, carols.Id);
        await admin.DeleteMessagesAsync(chat, forEveryone: true, bobs.Id);
        await owner.DeleteMessagesAsync(chat, forEveryone: true, bobs2.Id, service.Id);

        var page = await bob.PageAsync(chat);
        page.Messages.Select(m => m.Id).ShouldBe([admins.Id]);
        // Deleting for me works on anything I can see.
        await carol.DeleteMessagesAsync(chat, forEveryone: false, admins.Id);
        (await carol.PageAsync(chat)).Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_for_me_hides_messages_only_on_my_devices()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var m = await bob.SendManyAsync(chat, 3);
        var aliceTablet = await alice.SecondDeviceAsync();
        await using var tabletRt = await aliceTablet.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        await alice.DeleteMessagesAsync(chat, forEveryone: false, m[0].Id, m[1].Id);

        var e = await tabletRt.WaitAsync<MessagesDeletedEvent>(Ev.MessagesDeleted, x => x.ChatId == chat);
        e.ForEveryone.ShouldBeFalse();
        e.MessageIds.ShouldBe([m[0].Id, m[1].Id], ignoreOrder: true);
        await bobRt.BarrierAsync();
        bobRt.All<MessagesDeletedEvent>(Ev.MessagesDeleted).ShouldBeEmpty();

        (await aliceTablet.PageAsync(chat)).Messages.Select(x => x.Id).ShouldBe([m[2].Id]);
        (await bob.PageAsync(chat)).Messages.Count.ShouldBe(3);
        (await alice.ChatAsync(chat)).UnreadCount.ShouldBe(1);
        await (await alice.GetRawAsync(ApiRoutes.Messages.ByIdPath(chat, m[0].Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);

        // Already hidden: nothing to do, no event.
        await alice.DeleteMessagesAsync(chat, forEveryone: false, m[0].Id);
        await tabletRt.BarrierAsync();
        tabletRt.All<MessagesDeletedEvent>(Ev.MessagesDeleted).Count.ShouldBe(1);
        // Hidden for me also means I cannot delete it for everyone any more.
        await alice.DeleteMessagesAsync(chat, forEveryone: true, m[0].Id);
        (await bob.GetMessageAsync(chat, m[0].Id)).IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_requests_are_validated()
    {
        var alice = await factory.NewUserAsync("Alice");
        var saved = alice.SavedChatId;
        await (await alice.TryDeleteMessagesAsync(saved, true)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.TryDeleteMessagesAsync(saved, true, Guid.Empty)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var tooMany = Enumerable.Range(0, Limits.MaxMessagesPerBatch + 1).Select(_ => Guid.NewGuid()).ToArray();
        await (await alice.TryDeleteMessagesAsync(saved, false, tooMany)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var outsider = await factory.NewUserAsync("Outsider");
        await (await outsider.TryDeleteMessagesAsync(saved, false, Guid.NewGuid())).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }

    [Fact]
    public async Task Tombstones_drop_attachments_reactions_mentions_pins_and_reply_links()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);
        var target = await owner.SendAsync(chat, "original");
        var message = await bob.SendAsync(chat, new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = $"hey @{owner.Username}",
            MediaIds = [await factory.CreateMediaAsync(bob, MediaKind.Photo)],
            ReplyToMessageId = target.Id,
        });
        await owner.ReactAsync(chat, message.Id, "👍");
        await owner.PinAsync(chat, message.Id);
        (await owner.ChatAsync(chat)).UnreadMentionCount.ShouldBe(0); // pinning (a service message by me) read everything
        await using var bobRt = await bob.ConnectAsync();

        await bob.DeleteMessagesAsync(chat, forEveryone: true, message.Id);

        await bobRt.WaitAsync<ChatRefEvent>(Ev.PinnedMessagesChanged, e => e.ChatId == chat);
        var tombstone = await owner.GetMessageAsync(chat, message.Id);
        tombstone.IsDeleted.ShouldBeTrue();
        tombstone.Type.ShouldBe(MessageType.Photo);
        tombstone.Text.ShouldBeNull();
        tombstone.Attachments.ShouldBeEmpty();
        tombstone.Reactions.ShouldBeEmpty();
        tombstone.ReplyTo.ShouldBeNull();
        tombstone.IsPinned.ShouldBeFalse();
        (await owner.PinnedAsync(chat)).ShouldBeEmpty();
        var rows = await factory.WithDbAsync(async db => new
        {
            Attachments = await db.MessageAttachments.CountAsync(a => a.MessageId == message.Id),
            Reactions = await db.MessageReactions.CountAsync(r => r.MessageId == message.Id),
            Mentions = await db.MessageMentions.CountAsync(x => x.MessageId == message.Id),
            Pins = await db.PinnedMessages.CountAsync(p => p.MessageId == message.Id),
            Stored = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == message.Id),
        });
        rows.Attachments.ShouldBe(0);
        rows.Reactions.ShouldBe(0);
        rows.Mentions.ShouldBe(0);
        rows.Pins.ShouldBe(0);
        rows.Stored.Text.ShouldBeNull();
        rows.Stored.ReplyToMessageId.ShouldBeNull();
        rows.Stored.Entities.ShouldBeEmpty();
        rows.Stored.DeletedAt.ShouldNotBeNull();
    }

    // ---- reactions ----

    [Fact]
    public async Task One_reaction_per_user_aggregated_per_emoji()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = await alice.NewGroupAsync(bob, carol);
        var message = await carol.SendAsync(chat, "react to me");
        await using var carolRt = await carol.ConnectAsync();

        var first = await alice.ReactAsync(chat, message.Id, "👍");
        var reaction = first.Reactions.Single();
        reaction.Emoji.ShouldBe("👍");
        reaction.Count.ShouldBe(1);
        reaction.UserIds.ShouldBe([alice.Id]);
        first.UpdatedAt.ShouldBeGreaterThan(message.UpdatedAt);
        (await carolRt.WaitAsync<MessageDto>(Ev.MessageUpdated, m => m.Id == message.Id)).Reactions.Single().Count.ShouldBe(1);

        var second = await bob.ReactAsync(chat, message.Id, "👍");
        second.Reactions.Single().Count.ShouldBe(2);
        second.Reactions.Single().UserIds.ShouldBe([bob.Id, alice.Id]); // most recent first

        // A new emoji replaces mine; ties in count keep the older emoji first.
        var switched = await alice.ReactAsync(chat, message.Id, "❤️");
        switched.Reactions.Select(r => (r.Emoji, r.Count)).ShouldBe([("👍", 1), ("❤️", 1)]);
        switched.Reactions[0].UserIds.ShouldBe([bob.Id]);
        switched.Reactions[1].UserIds.ShouldBe([alice.Id]);

        // Same emoji again changes nothing.
        var unchanged = await alice.ReactAsync(chat, message.Id, "❤️");
        unchanged.UpdatedAt.ShouldBe(switched.UpdatedAt);

        var removed = await alice.UnreactAsync(chat, message.Id);
        removed.Reactions.Select(r => r.Emoji).ShouldBe(["👍"]);
        var removedAgain = await alice.UnreactAsync(chat, message.Id);
        removedAgain.UpdatedAt.ShouldBe(removed.UpdatedAt);

        await carolRt.BarrierAsync();
        carolRt.All<MessageDto>(Ev.MessageUpdated, m => m.Id == message.Id).Count.ShouldBe(4); // 👍, 👍, ❤️, removal
        (await carol.PageAsync(chat)).Messages.Single(m => m.Id == message.Id).Reactions.Single().UserIds.ShouldBe([bob.Id]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData(":)")]
    [InlineData("👍 👍")]
    [InlineData("😀😀😀😀😀😀😀😀😀")]
    [InlineData("\u0007")]
    public async Task Reactions_must_be_a_short_emoji(string emoji)
    {
        var alice = await factory.NewUserAsync("Alice");
        var message = await alice.SendAsync(alice.SavedChatId, "note");
        await (await alice.TryReactAsync(alice.SavedChatId, message.Id, emoji)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Theory]
    [InlineData("👍")]
    [InlineData("❤️")]
    [InlineData("👍🏽")]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("🇹🇭")]
    public async Task Reactions_accept_emoji_sequences(string emoji)
    {
        var alice = await factory.NewUserAsync("Alice");
        var message = await alice.SendAsync(alice.SavedChatId, "note");
        (await alice.ReactAsync(alice.SavedChatId, message.Id, emoji)).Reactions.Single().Emoji.ShouldBe(emoji);
    }

    [Fact]
    public async Task Reactions_need_a_visible_live_user_message()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var group = await alice.NewGroupAsync(bob);
        var service = (await alice.PageAsync(group)).Messages.Single();
        var deleted = await bob.SendAsync(group, "gone");
        await bob.DeleteMessagesAsync(group, forEveryone: true, deleted.Id);
        var hidden = await bob.SendAsync(group, "hidden");
        await alice.DeleteMessagesAsync(group, forEveryone: false, hidden.Id);

        await (await alice.TryReactAsync(group, service.Id, "👍")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MessageNotEditable);
        await (await alice.TryReactAsync(group, deleted.Id, "👍")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await (await alice.TryReactAsync(group, hidden.Id, "👍")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await (await alice.TryReactAsync(group, Guid.NewGuid(), "👍")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        var outsider = await factory.NewUserAsync("Outsider");
        await (await outsider.TryReactAsync(group, hidden.Id, "👍")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);

        // A message from another chat cannot be reached through mine.
        var elsewhere = await bob.SendAsync(bob.SavedChatId, "bob's note");
        await (await alice.TryReactAsync(group, elsewhere.Id, "👍")).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
    }

    [Fact]
    public async Task Blocked_private_chats_take_no_new_reactions()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var message = await alice.SendAsync(chat, "hi");
        await alice.BlockAsync(bob.Id);
        await (await bob.TryReactAsync(chat, message.Id, "👍")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        await (await alice.TryReactAsync(chat, message.Id, "👍")).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
    }

    // ---- pins ----

    [Fact]
    public async Task Pinning_posts_a_service_message_and_updates_everyone()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);
        var first = await bob.SendAsync(chat, "first");
        var second = await bob.SendAsync(chat, "second");
        await using var bobRt = await bob.ConnectAsync();

        await (await bob.TryPinAsync(chat, first.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);

        await owner.PinAsync(chat, first.Id);
        (await bobRt.WaitAsync<MessageDto>(Ev.MessageUpdated, m => m.Id == first.Id)).IsPinned.ShouldBeTrue();
        await bobRt.WaitAsync<ChatRefEvent>(Ev.PinnedMessagesChanged, e => e.ChatId == chat);
        var pinnedNotice = await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.MessagePinned);
        pinnedNotice.SystemEvent!.MessageId.ShouldBe(first.Id);
        pinnedNotice.SystemEvent.ActorId.ShouldBe(owner.Id);
        pinnedNotice.Id.ShouldNotBe(first.Id);
        (await bob.GetMessageAsync(chat, first.Id)).IsPinned.ShouldBeTrue();

        // Pinning again changes nothing.
        await owner.PinAsync(chat, first.Id);
        await owner.PinAsync(chat, second.Id);
        (await bob.PinnedAsync(chat)).Select(m => m.Id).ShouldBe([second.Id, first.Id]);
        (await bob.DetailsAsync(chat)).PinnedMessages.Select(m => m.Id).ShouldBe([second.Id, first.Id]);
        (await owner.PageAsync(chat)).Messages.Count(m => m.SystemEvent?.Type == SystemEventType.MessagePinned).ShouldBe(2);

        await owner.UnpinAsync(chat, first.Id);
        (await bobRt.WaitAsync<MessageDto>(Ev.MessageUpdated, m => m.Id == first.Id && !m.IsPinned)).UpdatedAt.ShouldBeGreaterThan(first.UpdatedAt);
        (await bob.PinnedAsync(chat)).Select(m => m.Id).ShouldBe([second.Id]);
        await owner.UnpinAsync(chat, first.Id); // not pinned: no-op
        await bobRt.BarrierAsync();
        bobRt.All<ChatRefEvent>(Ev.PinnedMessagesChanged, e => e.ChatId == chat).Count.ShouldBe(3);
        (await owner.PageAsync(chat)).Messages.Count(m => m.SystemEvent?.Type == SystemEventType.MessagePinned).ShouldBe(2);

        // With PinMessages members may pin too.
        await owner.SetPermissionsAsync(chat, new ChatPermissionsDto { PinMessages = true });
        await bob.PinAsync(chat, first.Id);
        (await owner.PinnedAsync(chat)).Select(m => m.Id).ShouldBe([first.Id, second.Id]);
    }

    [Fact]
    public async Task Blocked_private_chats_take_no_new_pins()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var message = await alice.SendAsync(chat, "hi");
        var pinnedBefore = await bob.SendAsync(chat, "pinned before the block");
        await alice.PinAsync(chat, pinnedBefore.Id);
        // Alice blocks Bob and removes the chat from her list.
        await alice.BlockAsync(bob.Id);
        await alice.DeleteChatAsync(chat, forEveryone: false);
        await using var aliceRt = await alice.ConnectAsync();

        // A pin posts a service message: like sending or reacting, it is a way into the chat, so it is refused...
        await (await bob.TryPinAsync(chat, message.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        await (await alice.TryPinAsync(chat, message.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        // ...and cannot bring the hidden chat back into Alice's list.
        await aliceRt.BarrierAsync();
        aliceRt.EventsFor(chat).ShouldBeEmpty();
        (await alice.ChatsAsync()).ShouldNotContain(c => c.Id == chat);
        (await bob.PageAsync(chat)).Messages.Count(m => m.Type == MessageType.System).ShouldBe(1);

        // Taking things away stays possible (like removing a reaction).
        await bob.UnpinAsync(chat, pinnedBefore.Id);
        (await bob.PinnedAsync(chat)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Pins_need_a_visible_live_user_message()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var group = await alice.NewGroupAsync(bob);
        var service = (await alice.PageAsync(group)).Messages.Single();
        var deleted = await bob.SendAsync(group, "gone");
        await bob.DeleteMessagesAsync(group, forEveryone: true, deleted.Id);

        await (await alice.TryPinAsync(group, service.Id)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MessageNotEditable);
        await (await alice.TryPinAsync(group, deleted.Id)).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await (await alice.TryPinAsync(group, Guid.NewGuid())).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await (await alice.DeleteRawAsync(ApiRoutes.Messages.PinPath(group, Guid.NewGuid()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
    }

    [Fact]
    public async Task Anyone_in_a_private_chat_may_pin_and_hidden_pins_are_mine_to_skip()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var message = await alice.SendAsync(chat, "remember this");
        var other = await alice.SendAsync(chat, "and this");

        await bob.PinAsync(chat, message.Id);
        await alice.PinAsync(chat, other.Id);
        var notices = (await alice.PageAsync(chat)).Messages.Where(m => m.Type == MessageType.System).ToList();
        notices.Select(m => m.SystemEvent!.ActorId).ShouldBe([bob.Id, alice.Id]);

        await bob.DeleteMessagesAsync(chat, forEveryone: false, other.Id);
        (await bob.PinnedAsync(chat)).Select(m => m.Id).ShouldBe([message.Id]);
        (await alice.PinnedAsync(chat)).Select(m => m.Id).ShouldBe([other.Id, message.Id]);
    }

    // ---- forward ----

    [Fact]
    public async Task Forwarding_copies_content_with_forward_info_idempotently()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var source = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var target = (await alice.OpenPrivateAsync(carol.Id)).Id;
        var one = await bob.SendAsync(source, "one");
        var two = await bob.SendAsync(source, "two bold", entities: [new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 4, Length = 4 }]);
        await using var carolRt = await carol.ConnectAsync();
        var request = new ForwardMessagesRequest
        {
            FromChatId = source,
            MessageIds = [two.Id, one.Id],
            ClientMessageIds = [Guid.NewGuid(), Guid.NewGuid()],
        };

        var copies = await alice.ForwardAsync(target, request);

        copies.Select(c => c.Text).ShouldBe(["one", "two bold"]); // source order
        copies.Select(c => c.Seq).ShouldBe([1L, 2L]);
        copies[0].ClientMessageId.ShouldBe(request.ClientMessageIds[1]);
        copies[1].ClientMessageId.ShouldBe(request.ClientMessageIds[0]);
        copies[1].Entities.ShouldNotBeNull().Single().ShouldBe(two.Entities!.Single());
        foreach (var (copy, original) in copies.Zip([one, two]))
        {
            copy.ChatId.ShouldBe(target);
            copy.SenderId.ShouldBe(alice.Id);
            copy.Type.ShouldBe(MessageType.Text);
            var forward = copy.Forward.ShouldNotBeNull();
            forward.FromUserId.ShouldBe(bob.Id);
            forward.FromChatId.ShouldBe(source);
            forward.SenderName.ShouldBe("Bob");
            forward.OriginalDate.ShouldBe(original.CreatedAt);
        }
        foreach (var copy in copies)
            (await carolRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.Id == copy.Id)).Forward.ShouldNotBeNull();
        await carolRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == target);

        // A retry returns the same copies and publishes nothing new.
        var retry = await alice.ForwardAsync(target, request);
        retry.Select(c => c.Id).ShouldBe(copies.Select(c => c.Id));
        await carolRt.BarrierAsync();
        carolRt.All<MessageDto>(Ev.MessageCreated, m => m.ChatId == target).Count.ShouldBe(2);
        (await carol.ChatAsync(target)).LastMessageSeq.ShouldBe(2);

        // Forwarding a forward keeps the original author and date.
        var again = (await carol.ForwardAsync(carol.SavedChatId, new ForwardMessagesRequest
        {
            FromChatId = target,
            MessageIds = [copies[0].Id],
            ClientMessageIds = [Guid.NewGuid()],
        })).Single();
        again.SenderId.ShouldBe(carol.Id);
        again.Forward.ShouldNotBeNull().FromUserId.ShouldBe(bob.Id);
        again.Forward.FromChatId.ShouldBe(source);
        again.Forward.SenderName.ShouldBe("Bob");
        again.Forward.OriginalDate.ShouldBe(one.CreatedAt);
    }

    [Fact]
    public async Task Forward_retries_with_more_messages_only_add_the_new_ones()
    {
        var alice = await factory.NewUserAsync("Alice");
        var m = await alice.SendManyAsync(alice.SavedChatId, 3);
        var group = await alice.NewGroupAsync();
        var ids = m.Select(x => x.Id).ToList();
        var clientIds = ids.Select(_ => Guid.NewGuid()).ToList();

        var partial = await alice.ForwardAsync(group, new ForwardMessagesRequest { FromChatId = alice.SavedChatId, MessageIds = [ids[0]], ClientMessageIds = [clientIds[0]] });
        var full = await alice.ForwardAsync(group, new ForwardMessagesRequest { FromChatId = alice.SavedChatId, MessageIds = ids, ClientMessageIds = clientIds });

        full.Count.ShouldBe(3);
        full[0].Id.ShouldBe(partial.Single().Id);
        full.Select(x => x.Text).ShouldBe(["msg 1", "msg 2", "msg 3"]);
        (await alice.PageAsync(group)).Messages.Count(x => x.Forward is not null).ShouldBe(3);
    }

    [Fact]
    public async Task Forward_skips_service_and_deleted_messages_and_copies_attachments()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var group = await alice.NewGroupAsync(bob);
        var service = (await alice.PageAsync(group)).Messages.Single();
        var photo = await factory.CreateMediaAsync(bob, MediaKind.Photo);
        var withPhoto = await bob.SendAsync(group, new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "look", MediaIds = [photo] });
        var deleted = await bob.SendAsync(group, "deleted");
        await bob.DeleteMessagesAsync(group, forEveryone: true, deleted.Id);

        var copies = await alice.ForwardAsync(alice.SavedChatId, new ForwardMessagesRequest
        {
            FromChatId = group,
            MessageIds = [service.Id, withPhoto.Id, deleted.Id],
            ClientMessageIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
        });

        var copy = copies.Single();
        copy.Type.ShouldBe(MessageType.Photo);
        copy.Text.ShouldBe("look");
        copy.Attachments.Select(a => a.Id).ShouldBe([photo]);
        copy.Forward.ShouldNotBeNull().FromUserId.ShouldBe(bob.Id);
        // Deleting the forwarded copy does not touch the original's attachment.
        await alice.DeleteMessagesAsync(alice.SavedChatId, forEveryone: true, copy.Id);
        (await bob.GetMessageAsync(group, withPhoto.Id)).Attachments.Select(a => a.Id).ShouldBe([photo]);
    }

    [Fact]
    public async Task Forward_requests_are_validated_and_authorized()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var owner = await factory.NewUserAsync("Owner");
        var note = await alice.SendAsync(alice.SavedChatId, "note");
        var bobsNote = await bob.SendAsync(bob.SavedChatId, "bob's note");
        var saved = alice.SavedChatId;

        Task<HttpResponseMessage> Forward(Guid target, Guid from, IReadOnlyList<Guid> ids, IReadOnlyList<Guid> clientIds) =>
            alice.TryForwardAsync(target, new ForwardMessagesRequest { FromChatId = from, MessageIds = ids, ClientMessageIds = clientIds });

        await (await Forward(saved, saved, [note.Id], [])).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await Forward(saved, saved, [], [])).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await Forward(saved, Guid.Empty, [note.Id], [Guid.NewGuid()])).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await Forward(saved, saved, [note.Id, note.Id], [Guid.NewGuid(), Guid.NewGuid()])).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var dup = Guid.NewGuid();
        await (await Forward(saved, saved, [note.Id, Guid.NewGuid()], [dup, dup])).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await Forward(saved, saved, [note.Id], [Guid.Empty])).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var tooMany = Enumerable.Range(0, Limits.MaxMessagesPerBatch + 1).Select(_ => Guid.NewGuid()).ToList();
        await (await Forward(saved, saved, tooMany, tooMany.Select(_ => Guid.NewGuid()).ToList())).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Both chats must be mine.
        await (await Forward(saved, bob.SavedChatId, [bobsNote.Id], [Guid.NewGuid()])).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await Forward(bob.SavedChatId, saved, [note.Id], [Guid.NewGuid()])).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        // Messages of other chats are not reachable through a chat I am in.
        (await alice.ForwardAsync(saved, new ForwardMessagesRequest { FromChatId = saved, MessageIds = [bobsNote.Id], ClientMessageIds = [Guid.NewGuid()] }))
            .ShouldBeEmpty();

        // An idempotency key already used in another chat.
        var used = Guid.NewGuid();
        await alice.SendAsync(saved, new SendMessageRequest { ClientMessageId = used, Text = "taken" });
        var group = await owner.NewGroupAsync(alice);
        await (await Forward(group, saved, [note.Id], [used])).ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.Conflict);

        // Group permissions apply to forwarded copies.
        await owner.SetPermissionsAsync(group, new ChatPermissionsDto { SendMessages = false });
        await (await Forward(group, saved, [note.Id], [Guid.NewGuid()])).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.SendForbidden);
        await owner.SetPermissionsAsync(group, new ChatPermissionsDto { SendMedia = false });
        var photoNote = await alice.SendMediaAsync(saved, await factory.CreateMediaAsync(alice, MediaKind.Photo));
        await (await Forward(group, saved, [photoNote.Id], [Guid.NewGuid()])).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.SendForbidden);
        (await alice.ForwardAsync(group, new ForwardMessagesRequest { FromChatId = saved, MessageIds = [note.Id], ClientMessageIds = [Guid.NewGuid()] }))
            .Single().Text.ShouldBe("note");
    }
}
