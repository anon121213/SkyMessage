using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// Chats, messages, members and invite links of other people: anything the caller cannot see is 404 (SPEC §3 "never
/// leak existence"), anything they can see but may not change is 403, and the victim's data stays untouched.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ChatIdorTests(ApiFactory factory)
{
    [Fact]
    public async Task Outsider_gets_404_on_every_endpoint_of_a_foreign_private_chat()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var eve = await factory.RegisterUserAsync("eve");
        var secret = SecurityKit.Unique("our secret plan");
        var chat = await alice.OpenPrivateChatAsync(bob);
        var m1 = await alice.SendTextAsync(chat.Id, secret);
        var m2 = await bob.SendTextAsync(chat.Id, "ok", replyTo: m1.Id);
        await alice.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Messages.PinPath(chat.Id, m1.Id));
        var evesPhoto = await MediaKit.UploadPhotoAsync(eve);
        var evesCursor = (await eve.PageAsync(eve.SavedChatId)).SyncCursor;

        var requests = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, ApiRoutes.Chats.ByIdPath(chat.Id), null),
            (HttpMethod.Get, ApiRoutes.Chats.DetailsPath(chat.Id), null),
            (HttpMethod.Get, ApiRoutes.Chats.PinnedPath(chat.Id), null),
            (HttpMethod.Patch, ApiRoutes.Chats.ByIdPath(chat.Id), new UpdateChatRequest { Title = "pwned" }),
            (HttpMethod.Put, ApiRoutes.Chats.AvatarPath(chat.Id), new SetChatAvatarRequest { MediaId = evesPhoto.Id }),
            (HttpMethod.Delete, ApiRoutes.Chats.AvatarPath(chat.Id), null),
            (HttpMethod.Post, ApiRoutes.Chats.MembersPath(chat.Id), new AddMembersRequest { UserIds = [eve.Id] }),
            (HttpMethod.Delete, ApiRoutes.Chats.MemberPath(chat.Id, bob.Id), null),
            (HttpMethod.Put, ApiRoutes.Chats.MemberRolePath(chat.Id, eve.Id), new SetMemberRoleRequest { Role = ChatRole.Owner }),
            (HttpMethod.Post, ApiRoutes.Chats.LeavePath(chat.Id), null),
            (HttpMethod.Post, ApiRoutes.Chats.InviteLinkPath(chat.Id), null),
            (HttpMethod.Delete, ApiRoutes.Chats.InviteLinkPath(chat.Id), null),
            (HttpMethod.Put, ApiRoutes.Chats.SettingsPath(chat.Id), new UpdateChatSettingsRequest { IsPinned = true, MuteForSeconds = -1 }),
            (HttpMethod.Put, ApiRoutes.Chats.SettingsPath(chat.Id), new UpdateChatSettingsRequest { ContactBarDismissed = true }),
            (HttpMethod.Put, ApiRoutes.Chats.ContentProtectionPath(chat.Id), new SetContentProtectionRequest { Enabled = true }),
            (HttpMethod.Post, ApiRoutes.Chats.ReadPath(chat.Id), new MarkReadRequest { UpToSeq = 100 }),
            (HttpMethod.Post, ApiRoutes.Chats.ClearHistoryPath(chat.Id), new ClearHistoryRequest { ForEveryone = true }),
            (HttpMethod.Delete, ApiRoutes.Chats.DeletePath(chat.Id, forEveryone: true), null),
            (HttpMethod.Delete, ApiRoutes.Chats.DeletePath(chat.Id, forEveryone: false), null),
            (HttpMethod.Get, ApiRoutes.Messages.PagePath(chat.Id), null),
            (HttpMethod.Get, ApiRoutes.Messages.PagePath(chat.Id, aroundSeq: m1.Seq), null),
            (HttpMethod.Get, ApiRoutes.Messages.ChangesPath(chat.Id, evesCursor), null),
            (HttpMethod.Get, ApiRoutes.Messages.MentionsPath(chat.Id), null),
            (HttpMethod.Get, ApiRoutes.Messages.SearchPath(chat.Id, "secret"), null),
            (HttpMethod.Get, ApiRoutes.Messages.SharedMediaPath(chat.Id, SharedMediaFilter.Media), null),
            (HttpMethod.Post, ApiRoutes.Messages.RootPath(chat.Id), new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "hi from eve" }),
            (HttpMethod.Get, ApiRoutes.Messages.ByIdPath(chat.Id, m1.Id), null),
            (HttpMethod.Patch, ApiRoutes.Messages.ByIdPath(chat.Id, m1.Id), new EditMessageRequest { Text = "edited by eve" }),
            (HttpMethod.Put, ApiRoutes.Messages.ReactionPath(chat.Id, m1.Id), new SetReactionRequest { Emoji = "👍" }),
            (HttpMethod.Delete, ApiRoutes.Messages.ReactionPath(chat.Id, m1.Id), null),
            (HttpMethod.Put, ApiRoutes.Messages.PinPath(chat.Id, m2.Id), null),
            (HttpMethod.Delete, ApiRoutes.Messages.PinPath(chat.Id, m1.Id), null),
            (HttpMethod.Post, ApiRoutes.Messages.DeletePath(chat.Id), new DeleteMessagesRequest { MessageIds = [m1.Id, m2.Id], ForEveryone = true }),
            (HttpMethod.Post, ApiRoutes.Messages.DeletePath(chat.Id), new DeleteMessagesRequest { MessageIds = [m1.Id], ForEveryone = false }),
            // Forwarding out of the chat and into it.
            (HttpMethod.Post, ApiRoutes.Messages.ForwardPath(eve.SavedChatId), new ForwardMessagesRequest { FromChatId = chat.Id, MessageIds = [m1.Id], ClientMessageIds = [Guid.NewGuid()] }),
            (HttpMethod.Post, ApiRoutes.Messages.ForwardPath(chat.Id), new ForwardMessagesRequest { FromChatId = eve.SavedChatId, MessageIds = [m1.Id], ClientMessageIds = [Guid.NewGuid()] }),
            // Calls in the chat.
            (HttpMethod.Post, ApiRoutes.Calls.Root, new StartCallRequest { ChatId = chat.Id }),
            (HttpMethod.Get, ApiRoutes.Calls.ActivePath(chat.Id), null),
        };

        foreach (var (method, path, body) in requests)
            await eve.Client.ProblemAsync(method, path, body, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);

        // Lists and searches do not mention the chat either.
        (await eve.Client.GetJsonAsync<List<ChatSummaryDto>>(ApiRoutes.Chats.Root)).ShouldNotContain(c => c.Id == chat.Id);
        var word = secret.Split(' ')[^1];
        (await eve.Client.GetJsonAsync<List<MessageSearchHitDto>>(ApiRoutes.Search.MessagesPath(word))).ShouldBeEmpty();
        var global = await eve.Client.GetJsonAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(word));
        global.Messages.ShouldBeEmpty();
        global.Chats.ShouldNotContain(c => c.Id == chat.Id);

        // The victims see their chat exactly as before.
        var page = await alice.PageAsync(chat.Id);
        page.Messages.Select(m => m.Id).ShouldContain(m1.Id);
        var original = page.Messages.Single(m => m.Id == m1.Id);
        original.Text.ShouldBe(secret);
        original.IsDeleted.ShouldBeFalse();
        original.EditedAt.ShouldBeNull();
        original.Reactions.ShouldBeEmpty();
        original.IsPinned.ShouldBeTrue();
        page.Messages.ShouldNotContain(m => m.Text == "hi from eve");
        var details = await bob.Client.GetJsonAsync<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(chat.Id));
        details.Members.Select(m => m.User.Id).ShouldBe([alice.Id, bob.Id], ignoreOrder: true);
        details.Summary.IsPinned.ShouldBeFalse();
        details.Summary.ClearedUpToSeq.ShouldBe(0);
    }

    [Fact]
    public async Task Message_ids_of_another_chat_are_useless_through_a_chat_the_attacker_is_in()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var eve = await factory.RegisterUserAsync("eve");
        var victimChat = await alice.OpenPrivateChatAsync(bob);
        var target = await alice.SendTextAsync(victimChat.Id, SecurityKit.Unique("private note"));
        await eve.SendTextAsync(eve.SavedChatId, "eve's own note");
        var own = eve.SavedChatId;

        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Messages.ByIdPath(own, target.Id), null, HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Patch, ApiRoutes.Messages.ByIdPath(own, target.Id), new EditMessageRequest { Text = "changed" },
            HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Messages.ReactionPath(own, target.Id), new SetReactionRequest { Emoji = "😈" },
            HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Messages.ReactionPath(own, target.Id), null, HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Messages.PinPath(own, target.Id), null, HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Messages.PinPath(own, target.Id), null, HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Messages.RootPath(own),
            new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "quoting you", ReplyToMessageId = target.Id },
            HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);

        // Deletes of unknown ids are idempotent no-ops (204) — and must stay no-ops for ids of other chats.
        await eve.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Messages.DeletePath(own), new DeleteMessagesRequest { MessageIds = [target.Id], ForEveryone = true });
        await eve.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Messages.DeletePath(own), new DeleteMessagesRequest { MessageIds = [target.Id], ForEveryone = false });

        // "Forward from my own chat" with a foreign message id copies nothing.
        var forwarded = await eve.Client.PostJsonAsync<List<MessageDto>>(ApiRoutes.Messages.ForwardPath(own),
            new ForwardMessagesRequest { FromChatId = own, MessageIds = [target.Id], ClientMessageIds = [Guid.NewGuid()] });
        forwarded.ShouldBeEmpty();
        (await eve.PageAsync(own)).Messages.ShouldNotContain(m => m.Text == target.Text);

        foreach (var viewer in new[] { alice, bob })
        {
            var message = await viewer.Client.GetJsonAsync<MessageDto>(ApiRoutes.Messages.ByIdPath(victimChat.Id, target.Id));
            message.IsDeleted.ShouldBeFalse();
            message.Text.ShouldBe(target.Text);
            message.Reactions.ShouldBeEmpty();
            message.IsPinned.ShouldBeFalse();
            message.EditedAt.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Saved_messages_of_another_user_are_invisible()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var eve = await factory.RegisterUserAsync("eve");
        var note = await alice.SendTextAsync(alice.SavedChatId, SecurityKit.Unique("password hint"));

        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Chats.ByIdPath(alice.SavedChatId), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Messages.PagePath(alice.SavedChatId), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Messages.ByIdPath(alice.SavedChatId, note.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Messages.RootPath(alice.SavedChatId),
            new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "planted" }, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);

        // "Open a chat with alice" creates eve's own private chat with her, never a door into alice's Saved Messages.
        var opened = await eve.OpenPrivateChatAsync(alice);
        opened.Id.ShouldNotBe(alice.SavedChatId);
        opened.Type.ShouldBe(ChatType.Private);
        (await eve.PageAsync(opened.Id)).Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plain_members_cannot_administer_a_group_and_admins_cannot_touch_the_owner()
    {
        var olga = await factory.RegisterUserAsync("olga");
        var max = await factory.RegisterUserAsync("max");
        var nina = await factory.RegisterUserAsync("nina");
        var group = await olga.CreateGroupAsync("Book club", max, nina);
        var announcement = await olga.SendTextAsync(group.Id, "Meeting on Friday");
        var maxPhoto = await MediaKit.UploadPhotoAsync(max);

        var maxDetails = await max.Client.GetJsonAsync<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(group.Id));
        maxDetails.InviteToken.ShouldBeNull("only admins see the invite token");

        await max.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Chats.MemberPath(group.Id, nina.Id), null, HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await max.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Chats.MemberPath(group.Id, olga.Id), null, HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await max.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Chats.MemberRolePath(group.Id, nina.Id), new SetMemberRoleRequest { Role = ChatRole.Admin },
            HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await max.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Chats.MemberRolePath(group.Id, max.Id), new SetMemberRoleRequest { Role = ChatRole.Owner },
            HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await max.Client.ProblemAsync(HttpMethod.Patch, ApiRoutes.Chats.ByIdPath(group.Id), new UpdateChatRequest { Title = "Max's club" },
            HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await max.Client.ProblemAsync(HttpMethod.Patch, ApiRoutes.Chats.ByIdPath(group.Id),
            new UpdateChatRequest { Permissions = new ChatPermissionsDto { SendMessages = false } }, HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await max.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Chats.AvatarPath(group.Id), new SetChatAvatarRequest { MediaId = maxPhoto.Id },
            HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await max.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Chats.DeletePath(group.Id, forEveryone: true), null, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await max.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Messages.DeletePath(group.Id),
            new DeleteMessagesRequest { MessageIds = [announcement.Id], ForEveryone = true }, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await max.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Messages.PinPath(group.Id, announcement.Id), null, HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await max.Client.ProblemAsync(HttpMethod.Patch, ApiRoutes.Messages.ByIdPath(group.Id, announcement.Id), new EditMessageRequest { Text = "Cancelled!" },
            HttpStatusCode.Forbidden, ErrorCodes.MessageNotEditable);
        await max.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Chats.ClearHistoryPath(group.Id), new ClearHistoryRequest { ForEveryone = true },
            HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Promote nina: admins still cannot touch the owner or hand out roles.
        await olga.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Chats.MemberRolePath(group.Id, nina.Id), new SetMemberRoleRequest { Role = ChatRole.Admin });
        await nina.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Chats.MemberPath(group.Id, olga.Id), null, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await nina.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Chats.MemberRolePath(group.Id, max.Id), new SetMemberRoleRequest { Role = ChatRole.Admin },
            HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await nina.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Chats.MemberRolePath(group.Id, nina.Id), new SetMemberRoleRequest { Role = ChatRole.Owner },
            HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await nina.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Chats.DeletePath(group.Id, forEveryone: true), null, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        (await nina.Client.GetJsonAsync<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(group.Id))).InviteToken.ShouldBeNull("no link was created yet");

        var details = await olga.Client.GetJsonAsync<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(group.Id));
        details.Summary.Title.ShouldBe("Book club");
        details.Summary.AvatarMediaId.ShouldBeNull();
        details.Summary.Permissions.SendMessages.ShouldBeTrue();
        details.Members.Select(m => (m.User.Id, m.Role)).ShouldBe(
            [(olga.Id, ChatRole.Owner), (nina.Id, ChatRole.Admin), (max.Id, ChatRole.Member)], ignoreOrder: true);
        var message = await olga.Client.GetJsonAsync<MessageDto>(ApiRoutes.Messages.ByIdPath(group.Id, announcement.Id));
        message.Text.ShouldBe("Meeting on Friday");
        message.IsDeleted.ShouldBeFalse();
        message.IsPinned.ShouldBeFalse();
    }

    [Fact]
    public async Task Invite_links_follow_permissions_bans_and_revocation()
    {
        var owner = await factory.RegisterUserAsync("owner");
        var max = await factory.RegisterUserAsync("max");
        var eve = await factory.RegisterUserAsync("eve");
        var frank = await factory.RegisterUserAsync("frank");
        var group = await owner.CreateGroupAsync("Family", max);
        await owner.SendTextAsync(group.Id, "history before eve joined");

        // Outsiders cannot create or revoke links.
        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Chats.InviteLinkPath(group.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Chats.InviteLinkPath(group.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);

        // With AddMembers off, plain members cannot either.
        await owner.Client.PatchJsonAsync<ChatInfoDto>(ApiRoutes.Chats.ByIdPath(group.Id),
            new UpdateChatRequest { Permissions = new ChatPermissionsDto { SendMessages = true, SendMedia = true, AddMembers = false } });
        await max.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Chats.InviteLinkPath(group.Id), null, HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await max.Client.ProblemAsync(HttpMethod.Delete, ApiRoutes.Chats.InviteLinkPath(group.Id), null, HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await max.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Chats.MembersPath(group.Id), new AddMembersRequest { UserIds = [eve.Id] },
            HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);

        var link = await owner.Client.PostJsonAsync<InviteLinkDto>(ApiRoutes.Chats.InviteLinkPath(group.Id), null);
        (await owner.Client.GetJsonAsync<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(group.Id))).InviteToken.ShouldBe(link.Token);
        (await max.Client.GetJsonAsync<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(group.Id))).InviteToken.ShouldBeNull();

        // The preview shows the group card only.
        var preview = await eve.Client.GetJsonAsync<ChatInvitePreviewDto>(ApiRoutes.Chats.JoinPath(link.Token));
        preview.ChatId.ShouldBe(group.Id);
        preview.IsMember.ShouldBeFalse();
        preview.MemberCount.ShouldBe(2);

        var joined = await eve.Client.PostJsonAsync<ChatSummaryDto>(ApiRoutes.Chats.JoinPath(link.Token), null);
        joined.Id.ShouldBe(group.Id);
        joined.MyRole.ShouldBe(ChatRole.Member);
        // Joining twice is idempotent and does not inflate the member count.
        (await eve.Client.PostJsonAsync<ChatSummaryDto>(ApiRoutes.Chats.JoinPath(link.Token), null)).MemberCount.ShouldBe(3);

        // Removed = banned: the link no longer works for eve, and a plain member cannot bring her back.
        await owner.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Chats.MemberPath(group.Id, eve.Id));
        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Chats.JoinPath(link.Token), null, HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);
        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Chats.JoinPath(link.Token), null, HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);
        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Messages.PagePath(group.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await owner.Client.PatchJsonAsync<ChatInfoDto>(ApiRoutes.Chats.ByIdPath(group.Id),
            new UpdateChatRequest { Permissions = new ChatPermissionsDto { SendMessages = true, SendMedia = true, AddMembers = true } });
        var readd = await max.Client.PostJsonAsync<AddMembersResultDto>(ApiRoutes.Chats.MembersPath(group.Id), new AddMembersRequest { UserIds = [eve.Id] });
        readd.Added.ShouldBeEmpty();
        readd.Failed.ShouldContain(f => f.UserId == eve.Id && f.Code == ErrorCodes.NotChatAdmin);
        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Chats.ByIdPath(group.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);

        // Revoked tokens are dead for everybody; malformed tokens look exactly like unknown ones.
        await owner.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Chats.InviteLinkPath(group.Id));
        await frank.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Chats.JoinPath(link.Token), null, HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);
        await frank.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Chats.JoinPath(link.Token), null, HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);
        foreach (var bogus in new[] { "AAAAAAAAAAAAAAAAAAAAAA", "..%2F..%2Fadmin", new string('a', 200), "' OR 1=1 --" })
            await frank.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Chats.JoinPath(bogus), null, HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);

        var final = await owner.Client.GetJsonAsync<ChatDetailsDto>(ApiRoutes.Chats.DetailsPath(group.Id));
        final.Members.Select(m => m.User.Id).ShouldBe([owner.Id, max.Id], ignoreOrder: true);
        final.Summary.MemberCount.ShouldBe(2);
        final.InviteToken.ShouldBeNull();
    }

    [Fact]
    public async Task Removed_member_loses_history_media_and_sending()
    {
        var owner = await factory.RegisterUserAsync("owner");
        var max = await factory.RegisterUserAsync("max");
        var group = await owner.CreateGroupAsync("Trip", max);
        var photo = await MediaKit.UploadPhotoAsync(owner, "tickets.jpg");
        var message = await owner.SendMediaAsync(group.Id, "tickets", photo.Id);

        await owner.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Chats.MemberPath(group.Id, max.Id));

        await max.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Messages.PagePath(group.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await max.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Messages.ByIdPath(group.Id, message.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await max.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Messages.RootPath(group.Id),
            new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "still here?" }, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await max.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Media.ByIdPath(photo.Id), null, HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var content = await max.Client.GetAsync(ApiRoutes.Media.ContentPath(photo.Id, null), SecurityKit.Ct))
            await content.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var signed = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, max.Sign(photo.Id))))
            await signed.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        (await max.Client.GetJsonAsync<List<ChatSummaryDto>>(ApiRoutes.Chats.Root)).ShouldNotContain(c => c.Id == group.Id);
        (await max.Client.GetJsonAsync<List<MessageSearchHitDto>>(ApiRoutes.Search.MessagesPath("tickets"))).ShouldBeEmpty();
    }
}
