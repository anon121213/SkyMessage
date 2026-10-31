using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>Limits, concurrency and less common paths of the messaging feature.</summary>
[Collection(ApiCollection.Name)]
public sealed class MessagingEdgeCaseTests(ApiFactory factory)
{
    [Fact]
    public async Task Reactions_list_only_the_most_recent_reactors_of_a_crowded_emoji()
    {
        var alice = await factory.NewUserAsync("Alice");
        var message = await alice.SendAsync(alice.SavedChatId, "popular");
        var start = TestKit.Now().AddMinutes(-10);
        // More reactors than the DTO lists (the loader's capped per-emoji query); rows inserted directly for speed.
        var crowd = await factory.WithDbAsync(async db =>
        {
            var users = Enumerable.Range(0, Limits.MaxReactionUserIds + 5).Select(i =>
            {
                var name = "crowd" + Guid.NewGuid().ToString("N")[..12];
                return new User { Username = name, UsernameNormalized = name, DisplayName = $"Fan {i}", PasswordHash = "x", CreatedAt = start, UpdatedAt = start };
            }).ToList();
            db.Users.AddRange(users);
            for (var i = 0; i < users.Count; i++)
                db.MessageReactions.Add(new MessageReaction { MessageId = message.Id, UserId = users[i].Id, Emoji = "👍", CreatedAt = start.AddSeconds(i) });
            await db.SaveChangesAsync();
            return users.Select(u => u.Id).ToList();
        });
        var hearts = await factory.NewUserAsync("Heart");
        await factory.WithDbAsync(async db =>
        {
            db.MessageReactions.Add(new MessageReaction { MessageId = message.Id, UserId = hearts.Id, Emoji = "❤️", CreatedAt = TestKit.Now() });
            db.MessageReactions.Add(new MessageReaction { MessageId = message.Id, UserId = alice.Id, Emoji = "❤️", CreatedAt = TestKit.Now().AddSeconds(1) });
            await db.SaveChangesAsync();
        });

        var loaded = await alice.GetMessageAsync(alice.SavedChatId, message.Id);
        loaded.Reactions.Select(r => (r.Emoji, r.Count)).ShouldBe([("👍", crowd.Count), ("❤️", 2)]);
        var thumbs = loaded.Reactions[0].UserIds;
        thumbs.Count.ShouldBe(Limits.MaxReactionUserIds);
        thumbs.ShouldBe(Enumerable.Reverse(crowd).Take(Limits.MaxReactionUserIds)); // most recent first
        loaded.Reactions[1].UserIds.ShouldBe([alice.Id, hearts.Id]);

        var page = await alice.PageAsync(alice.SavedChatId);
        page.Messages.Single().Reactions[0].UserIds.Count.ShouldBe(Limits.MaxReactionUserIds);
        page.MyReactions[message.Id].ShouldBe("❤️");
    }

    [Fact]
    public async Task Member_limit_applies_to_adds_and_joins()
    {
        var owner = await factory.NewUserAsync("Owner");
        var first = await factory.NewUserAsync("First");
        var second = await factory.NewUserAsync("Second");
        var joiner = await factory.NewUserAsync("Joiner");
        var chat = await owner.NewGroupAsync();
        var token = await owner.CreateInviteLinkAsync(chat);
        Task SetMemberCount(int count) => factory.WithDbAsync(db => db.Chats.Where(c => c.Id == chat).ExecuteUpdateAsync(s => s.SetProperty(c => c.MemberCount, count)));

        await SetMemberCount(Limits.MaxGroupMembers - 1);
        var result = await owner.AddMembersAsync(chat, first.Id, second.Id);
        result.Added.ShouldBe([first.Id]);
        result.Failed.ShouldBe([new AddMemberFailureDto { UserId = second.Id, Code = ErrorCodes.ChatMemberLimit }]);
        (await owner.ChatAsync(chat)).MemberCount.ShouldBe(Limits.MaxGroupMembers);

        await (await joiner.PostEmptyAsync(ApiRoutes.Chats.JoinPath(token))).ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.ChatMemberLimit);
        (await owner.AddMembersAsync(chat, joiner.Id)).Failed.Single().Code.ShouldBe(ErrorCodes.ChatMemberLimit);

        // Creating a group with more members than fit is rejected up front.
        var tooMany = Enumerable.Range(0, Limits.MaxGroupMembers).Select(_ => Guid.NewGuid()).ToList();
        await (await owner.PostRawAsync(ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = "Huge", MemberIds = tooMany }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Concurrent_pins_post_one_service_message()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);
        var message = await bob.SendAsync(chat, "pin me");

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PinAsync(chat, message.Id)));

        (await bob.PinnedAsync(chat)).Select(m => m.Id).ShouldBe([message.Id]);
        (await bob.PageAsync(chat)).Messages.Count(m => m.SystemEvent?.Type == SystemEventType.MessagePinned).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_invite_link_requests_agree_on_one_token()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? owner : bob).CreateInviteLinkAsync(chat)));

        tokens.Distinct().Count().ShouldBe(1);
        (await owner.DetailsAsync(chat)).InviteToken.ShouldBe(tokens[0]);
    }

    [Fact]
    public async Task Concurrent_joins_and_adds_keep_the_member_count_exact()
    {
        var owner = await factory.NewUserAsync("Owner");
        var admin = await factory.NewUserAsync("Admin");
        var joiner = await factory.NewUserAsync("Joiner");
        var added = await factory.NewUserAsync("Added");
        var chat = await owner.NewGroupAsync(admin);
        await owner.SetRoleAsync(chat, admin.Id, ChatRole.Admin);
        var token = await owner.CreateInviteLinkAsync(chat);

        var joins = Enumerable.Range(0, 6).Select(async _ => await (await joiner.PostEmptyAsync(ApiRoutes.Chats.JoinPath(token))).ReadAsync<ChatSummaryDto>());
        var adds = Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? owner : admin).AddMembersAsync(chat, added.Id));
        await Task.WhenAll(Task.WhenAll(joins), Task.WhenAll(adds));

        var summary = await owner.ChatAsync(chat);
        summary.MemberCount.ShouldBe(4);
        (await owner.DetailsAsync(chat)).Members.Count.ShouldBe(4);
        var service = (await owner.PageAsync(chat)).Messages.Where(m => m.Type == MessageType.System).Select(m => m.SystemEvent!.Type).ToList();
        service.Count(t => t == SystemEventType.MemberJoinedByLink).ShouldBe(1);
        service.Count(t => t == SystemEventType.MembersAdded).ShouldBe(1);
    }

    [Fact]
    public async Task The_last_two_members_leaving_at_once_delete_the_group()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);

        await Task.WhenAll(owner.LeaveAsync(chat), bob.LeaveAsync(chat));

        (await factory.WithDbAsync(db => db.Chats.AnyAsync(c => c.Id == chat))).ShouldBeFalse();
    }

    [Fact]
    public async Task Concurrent_reactions_of_one_user_leave_exactly_one()
    {
        var alice = await factory.NewUserAsync("Alice");
        var message = await alice.SendAsync(alice.SavedChatId, "react");
        var emoji = new[] { "👍", "❤️", "😂", "🔥", "🎉", "😮" };

        await Task.WhenAll(emoji.Select(e => alice.ReactAsync(alice.SavedChatId, message.Id, e)));

        var reactions = (await alice.GetMessageAsync(alice.SavedChatId, message.Id)).Reactions;
        reactions.Count.ShouldBe(1);
        reactions[0].Count.ShouldBe(1);
        reactions[0].UserIds.ShouldBe([alice.Id]);
        emoji.ShouldContain(reactions[0].Emoji);
    }

    [Fact]
    public async Task Concurrent_read_receipts_end_at_the_highest_seq()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.SendManyAsync(chat, 10);

        await Task.WhenAll(Enumerable.Range(1, 10).Reverse().Select(seq => alice.MarkReadAsync(chat, seq)));

        var summary = await alice.ChatAsync(chat);
        summary.MyLastReadSeq.ShouldBe(10);
        summary.UnreadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Clear_history_accepts_the_query_string_without_a_body()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.SendManyAsync(chat, 2);

        await alice.PostEmptyAsync(ApiRoutes.Chats.ClearHistoryPath(chat) + "?forEveryone=true").NoContentAsync();

        (await alice.ChatAsync(chat)).ClearedUpToSeq.ShouldBe(2);
        (await bob.ChatAsync(chat)).ClearedUpToSeq.ShouldBe(2);
        await (await alice.PostEmptyAsync(ApiRoutes.Chats.ClearHistoryPath(alice.SavedChatId))).ShouldSucceedAsync();
    }

    [Fact]
    public async Task Saved_messages_come_back_when_reopened_or_written_to()
    {
        var alice = await factory.NewUserAsync("Alice");
        var saved = alice.SavedChatId;
        await alice.SendAsync(saved, "old note");
        var aliceTablet = await alice.SecondDeviceAsync();
        await using var tabletRt = await aliceTablet.ConnectAsync();

        await alice.DeleteChatAsync(saved, forEveryone: true); // Saved Messages cannot be destroyed: hidden for me
        await tabletRt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == saved);
        (await alice.ChatsAsync()).ShouldNotContain(c => c.Id == saved);

        var reopened = await alice.OpenPrivateAsync(alice.Id);
        reopened.Id.ShouldBe(saved);
        reopened.ClearedUpToSeq.ShouldBe(1);
        await tabletRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == saved);
        (await alice.ChatsAsync()).ShouldContain(c => c.Id == saved);
        (await alice.PageAsync(saved)).Messages.ShouldBeEmpty();

        await alice.DeleteChatAsync(saved, forEveryone: false);
        var note = await aliceTablet.SendAsync(saved, "new note");
        (await alice.ChatsAsync()).Single(c => c.Id == saved).LastMessage.ShouldNotBeNull().Id.ShouldBe(note.Id);
    }

    [Fact]
    public async Task Writing_to_a_deleted_account_is_forbidden()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await alice.SendAsync(chat, "hi");
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == bob.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true)));

        await (await alice.TrySendAsync(chat, MessagingApi.Text("still there?"))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.SendForbidden);
        (await alice.PageAsync(chat)).Messages.Count.ShouldBe(1); // history stays readable
    }

    [Fact]
    public async Task Page_anchors_prefer_around_then_after_then_before()
    {
        var alice = await factory.NewUserAsync("Alice");
        await alice.SendManyAsync(alice.SavedChatId, 30);

        var around = await alice.GetAsync<MessagesPageDto>($"api/chats/{alice.SavedChatId}/messages?limit=4&beforeSeq=10&afterSeq=20&aroundSeq=15");
        around.Messages.Select(m => m.Seq).ShouldBe([13L, 14L, 15L, 16L]);
        var after = await alice.GetAsync<MessagesPageDto>($"api/chats/{alice.SavedChatId}/messages?limit=4&beforeSeq=10&afterSeq=20");
        after.Messages.Select(m => m.Seq).ShouldBe([21L, 22L, 23L, 24L]);
    }

    [Fact]
    public async Task Alternative_http_verbs_reach_the_same_actions()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);
        var message = await owner.SendAsync(chat, "v1");

        (await (await owner.Client.PutAsJsonAsync(ApiRoutes.Messages.ByIdPath(chat, message.Id), new EditMessageRequest { Text = "v2" }, SkyJson.Options, TestKit.Ct))
            .ReadAsync<MessageDto>()).Text.ShouldBe("v2");
        (await (await owner.PatchRawAsync(ApiRoutes.Chats.SettingsPath(chat), new UpdateChatSettingsRequest { IsArchived = true }))
            .ReadAsync<ChatUserSettingsDto>()).IsArchived.ShouldBeTrue();
        await owner.PatchRawAsync(ApiRoutes.Chats.MemberRolePath(chat, bob.Id), new SetMemberRoleRequest { Role = ChatRole.Admin }).NoContentAsync();
        (await bob.ChatAsync(chat)).MyRole.ShouldBe(ChatRole.Admin);
        await owner.PostRawAsync(ApiRoutes.Chats.MemberRolePath(chat, bob.Id), new SetMemberRoleRequest { Role = ChatRole.Member }).NoContentAsync();
        (await bob.ChatAsync(chat)).MyRole.ShouldBe(ChatRole.Member);
    }

    [Fact]
    public async Task Private_chat_details_list_both_sides_without_an_invite_link()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var message = await bob.SendAsync(chat, "pin me");
        await alice.PinAsync(chat, message.Id);

        var details = await bob.DetailsAsync(chat);
        details.Members.Select(m => m.User.Id).ShouldBe([alice.Id, bob.Id], ignoreOrder: true);
        details.Members.ShouldAllBe(m => m.Role == ChatRole.Member);
        details.InviteToken.ShouldBeNull();
        details.CreatedById.ShouldBe(alice.Id);
        details.PinnedMessages.Select(m => m.Id).ShouldBe([message.Id]);
        details.Summary.Peer.ShouldNotBeNull().Id.ShouldBe(alice.Id);
    }

    [Fact]
    public async Task Pinning_a_chat_twice_keeps_its_pin_time()
    {
        var alice = await factory.NewUserAsync("Alice");
        var first = await alice.UpdateSettingsAsync(alice.SavedChatId, new UpdateChatSettingsRequest { IsPinned = true });
        var second = await alice.UpdateSettingsAsync(alice.SavedChatId, new UpdateChatSettingsRequest { IsPinned = true, IsArchived = true });
        second.PinnedAt.ShouldBe(first.PinnedAt);
        second.IsArchived.ShouldBeTrue();
        (await alice.ChatAsync(alice.SavedChatId)).PinnedAt.ShouldBe(first.PinnedAt);
    }
}
