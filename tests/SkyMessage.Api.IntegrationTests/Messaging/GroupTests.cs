using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>Groups: creation, info, avatar, members, roles, leaving, bans and invite links (SPEC §6.3, §7).</summary>
[Collection(ApiCollection.Name)]
public sealed class GroupTests(ApiFactory factory)
{
    [Fact]
    public async Task Creating_a_group_adds_everyone_and_posts_chat_created()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var outsider = await factory.NewUserAsync("Outsider");
        await using var ownerRt = await owner.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();
        await using var carolRt = await carol.ConnectAsync();
        await using var outsiderRt = await outsider.ConnectAsync();

        var result = await owner.CreateGroupAsync(new CreateGroupRequest
        {
            Title = "  Family  ",
            Description = " Our chat ",
            MemberIds = [bob.Id, carol.Id, bob.Id, owner.Id],
        });

        result.Failed.ShouldBeEmpty();
        var chat = result.Chat;
        chat.Type.ShouldBe(ChatType.Group);
        chat.Title.ShouldBe("Family");
        chat.MemberCount.ShouldBe(3);
        chat.MyRole.ShouldBe(ChatRole.Owner);
        chat.Permissions.ShouldBe(new ChatPermissionsDto());

        foreach (var rt in new[] { ownerRt, bobRt, carolRt })
        {
            await rt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat.Id);
            var created = await rt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat.Id);
            created.Type.ShouldBe(MessageType.System);
            created.SenderId.ShouldBeNull();
            created.Seq.ShouldBe(1);
            var e = created.SystemEvent.ShouldNotBeNull();
            e.Type.ShouldBe(SystemEventType.ChatCreated);
            e.ActorId.ShouldBe(owner.Id);
            e.Text.ShouldBe("Family");
            e.TargetUserIds.ShouldBe([bob.Id, carol.Id], ignoreOrder: true);
        }
        await outsiderRt.BarrierAsync();
        outsiderRt.EventsFor(chat.Id).ShouldBeEmpty();

        var details = await bob.DetailsAsync(chat.Id);
        details.Description.ShouldBe("Our chat");
        details.CreatedById.ShouldBe(owner.Id);
        details.Members.Select(m => (m.User.Id, m.Role)).ShouldBe(
            [(owner.Id, ChatRole.Owner), (bob.Id, ChatRole.Member), (carol.Id, ChatRole.Member)], ignoreOrder: true);
        details.Members[0].User.Id.ShouldBe(owner.Id); // owner first
        details.InviteToken.ShouldBeNull();
        details.Summary.MyRole.ShouldBe(ChatRole.Member);

        // The creator caused the service message, so it is not unread for them; it is for the others.
        (await owner.ChatAsync(chat.Id)).UnreadCount.ShouldBe(0);
        (await bob.ChatAsync(chat.Id)).UnreadCount.ShouldBe(1);
        (await outsider.GetRawAsync(ApiRoutes.Chats.DetailsPath(chat.Id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Members_that_cannot_be_added_are_reported_as_failures()
    {
        var owner = await factory.NewUserAsync("Owner");
        var friend = await factory.NewUserAsync("Friend");
        var picky = await factory.NewUserAsync("Picky");
        var blocker = await factory.NewUserAsync("Blocker");
        var blockedByMe = await factory.NewUserAsync("Blocked");
        var gone = await factory.NewUserAsync("Gone");
        var unknown = Guid.NewGuid();
        await picky.SetPrivacyAsync(p => p with { GroupInvites = PrivacyLevel.Contacts });
        await blocker.BlockAsync(owner.Id);
        await owner.BlockAsync(blockedByMe.Id);
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == gone.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true)));
        await using var pickyRt = await picky.ConnectAsync();

        var result = await owner.CreateGroupAsync(new CreateGroupRequest
        {
            Title = "Partly",
            MemberIds = [friend.Id, picky.Id, blocker.Id, blockedByMe.Id, gone.Id, unknown],
        });

        result.Chat.MemberCount.ShouldBe(2);
        result.Failed.Select(f => (f.UserId, f.Code)).ShouldBe(
        [
            (picky.Id, ErrorCodes.PrivacyRestricted),
            (blocker.Id, ErrorCodes.UserBlocked),
            (blockedByMe.Id, ErrorCodes.UserBlocked),
            (gone.Id, ErrorCodes.UserNotFound),
            (unknown, ErrorCodes.UserNotFound),
        ], ignoreOrder: true);
        var members = (await owner.DetailsAsync(result.Chat.Id)).Members.Select(m => m.User.Id).ToList();
        members.ShouldBe([owner.Id, friend.Id], ignoreOrder: true);
        var created = (await owner.PageAsync(result.Chat.Id)).Messages.Single();
        created.SystemEvent.ShouldNotBeNull().TargetUserIds.ShouldBe([friend.Id]);
        await pickyRt.BarrierAsync();
        pickyRt.EventsFor(result.Chat.Id).ShouldBeEmpty();

        // Contacts may add a "contacts only" user.
        await picky.AddContactAsync(owner.Id);
        (await owner.AddMembersAsync(result.Chat.Id, picky.Id)).Added.ShouldBe([picky.Id]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Group_title_is_required(string? title)
    {
        var owner = await factory.NewUserAsync("Owner");
        await (await owner.PostRawAsync(ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = title! }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Group_title_and_description_lengths_are_limited()
    {
        var owner = await factory.NewUserAsync("Owner");
        (await owner.CreateGroupAsync(new string('t', Limits.GroupTitleMaxLength))).Chat.Title.Length.ShouldBe(Limits.GroupTitleMaxLength);
        await (await owner.PostRawAsync(ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = new string('t', Limits.GroupTitleMaxLength + 1) }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await owner.PostRawAsync(ApiRoutes.Chats.Groups, new CreateGroupRequest
            {
                Title = "ok",
                Description = new string('d', Limits.GroupDescriptionMaxLength + 1),
            }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await owner.PostRawAsync(ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = "bad\0title" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Group_avatar_must_be_my_own_ready_photo()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var bobsPhoto = await factory.CreateMediaAsync(bob, MediaKind.Photo);
        var processing = await factory.CreateMediaAsync(owner, MediaKind.Photo, MediaStatus.Processing);
        var failed = await factory.CreateMediaAsync(owner, MediaKind.Photo, MediaStatus.Failed);
        var file = await factory.CreateMediaAsync(owner, MediaKind.File);
        var photo = await factory.CreateMediaAsync(owner, MediaKind.Photo);

        async Task<HttpResponseMessage> Create(Guid avatar) =>
            await owner.PostRawAsync(ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = "Pics", AvatarMediaId = avatar, MemberIds = [bob.Id] });

        await (await Create(bobsPhoto)).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await (await Create(Guid.NewGuid())).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await (await Create(processing)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MediaNotReady);
        await (await Create(failed)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MediaUnsupported);
        await (await Create(file)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MediaUnsupported);
        // Nothing was created by the failed attempts.
        (await bob.ChatsAsync()).ShouldNotContain(c => c.Type == ChatType.Group);

        var group = await (await Create(photo)).ReadAsync<CreateGroupResultDto>();
        group.Chat.AvatarMediaId.ShouldBe(photo);
        (await bob.ChatAsync(group.Chat.Id)).AvatarMediaId.ShouldBe(photo);
    }

    [Fact]
    public async Task Setting_and_removing_the_avatar_follows_the_ownership_and_permission_rules()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);
        var ownerPhoto = await factory.CreateMediaAsync(owner, MediaKind.Photo);
        var bobPhoto = await factory.CreateMediaAsync(bob, MediaKind.Photo);
        await using var bobRt = await bob.ConnectAsync();

        // A plain member may not change the photo...
        await (await bob.PutRawAsync(ApiRoutes.Chats.AvatarPath(chat), new SetChatAvatarRequest { MediaId = bobPhoto }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        // ...and the owner may not use someone else's photo.
        await (await owner.PutRawAsync(ApiRoutes.Chats.AvatarPath(chat), new SetChatAvatarRequest { MediaId = bobPhoto }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await (await owner.PutRawAsync(ApiRoutes.Chats.AvatarPath(chat), new SetChatAvatarRequest { MediaId = Guid.Empty }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var info = await (await owner.PutRawAsync(ApiRoutes.Chats.AvatarPath(chat), new SetChatAvatarRequest { MediaId = ownerPhoto })).ReadAsync<ChatInfoDto>();
        info.AvatarMediaId.ShouldBe(ownerPhoto);
        (await bobRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat)).AvatarMediaId.ShouldBe(ownerPhoto);
        (await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.PhotoChanged))
            .SystemEvent!.ActorId.ShouldBe(owner.Id);

        // Same photo again: nothing changes, no service message.
        await (await owner.PutRawAsync(ApiRoutes.Chats.AvatarPath(chat), new SetChatAvatarRequest { MediaId = ownerPhoto })).ShouldSucceedAsync();

        // With ChangeInfo a member may set their own photo.
        await owner.SetPermissionsAsync(chat, new ChatPermissionsDto { ChangeInfo = true });
        (await (await bob.PutRawAsync(ApiRoutes.Chats.AvatarPath(chat), new SetChatAvatarRequest { MediaId = bobPhoto })).ReadAsync<ChatInfoDto>())
            .AvatarMediaId.ShouldBe(bobPhoto);

        var removed = await (await owner.DeleteRawAsync(ApiRoutes.Chats.AvatarPath(chat))).ReadAsync<ChatInfoDto>();
        removed.AvatarMediaId.ShouldBeNull();
        await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.PhotoRemoved);
        await (await owner.DeleteRawAsync(ApiRoutes.Chats.AvatarPath(chat))).ShouldSucceedAsync();

        var serviceMessages = (await owner.PageAsync(chat)).Messages.Where(m => m.Type == MessageType.System)
            .Select(m => m.SystemEvent!.Type).ToList();
        serviceMessages.ShouldBe([SystemEventType.ChatCreated, SystemEventType.PhotoChanged, SystemEventType.PhotoChanged, SystemEventType.PhotoRemoved]);
        (await owner.ChatAsync(chat)).AvatarMediaId.ShouldBeNull();
    }

    [Fact]
    public async Task Updating_title_description_and_permissions()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);
        await using var bobRt = await bob.ConnectAsync();

        var renamed = await owner.UpdateChatAsync(chat, new UpdateChatRequest { Title = "  New title " });
        renamed.Title.ShouldBe("New title");
        (await bobRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat)).Title.ShouldBe("New title");
        var titleChanged = await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.TitleChanged);
        titleChanged.SystemEvent!.Text.ShouldBe("New title");
        titleChanged.SystemEvent.ActorId.ShouldBe(owner.Id);
        (await bob.ChatAsync(chat)).Title.ShouldBe("New title");

        // No change → no event.
        (await owner.UpdateChatAsync(chat, new UpdateChatRequest { Title = "New title" })).Title.ShouldBe("New title");
        await bobRt.BarrierAsync();
        bobRt.All<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat).Count.ShouldBe(1);

        await (await bob.PatchRawAsync(ApiRoutes.Chats.ByIdPath(chat), new UpdateChatRequest { Title = "Bob's" }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await (await owner.PatchRawAsync(ApiRoutes.Chats.ByIdPath(chat), new UpdateChatRequest { Title = new string('x', 129) }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await owner.PatchRawAsync(ApiRoutes.Chats.ByIdPath(chat), new UpdateChatRequest { Title = " " }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var permissions = new ChatPermissionsDto { SendMessages = true, SendMedia = false, AddMembers = false, PinMessages = true, ChangeInfo = true };
        (await owner.SetPermissionsAsync(chat, permissions)).Permissions.ShouldBe(permissions);
        (await bobRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat && c.Permissions == permissions)).Title.ShouldBe("New title");
        (await bob.ChatAsync(chat)).Permissions.ShouldBe(permissions);

        // ChangeInfo lets a member edit title/description, never the permissions.
        (await bob.UpdateChatAsync(chat, new UpdateChatRequest { Description = " About us " })).Description.ShouldBe("About us");
        var described = await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.DescriptionChanged);
        described.SystemEvent!.Text.ShouldBe("About us");
        described.SystemEvent.ActorId.ShouldBe(bob.Id);
        await (await bob.PatchRawAsync(ApiRoutes.Chats.ByIdPath(chat), new UpdateChatRequest { Permissions = new ChatPermissionsDto() }))
            .ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);

        // A blank description clears it.
        (await owner.UpdateChatAsync(chat, new UpdateChatRequest { Description = "   " })).Description.ShouldBeNull();
        (await owner.DetailsAsync(chat)).Description.ShouldBeNull();
        var cleared = await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated,
            m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.DescriptionChanged && m.SystemEvent.ActorId == owner.Id);
        cleared.SystemEvent!.Text.ShouldBeNull();
    }

    [Fact]
    public async Task Adding_members_notifies_everyone_and_keeps_history_read()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var dave = await factory.NewUserAsync("Dave");
        var chat = await owner.NewGroupAsync(bob);
        await bob.SendManyAsync(chat, 3, "before Dave");
        await using var bobRt = await bob.ConnectAsync();
        await using var daveRt = await dave.ConnectAsync();

        var result = await owner.AddMembersAsync(chat, dave.Id, bob.Id, owner.Id);
        result.Added.ShouldBe([dave.Id]);
        result.Failed.ShouldBeEmpty();

        await daveRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat);
        await daveRt.WaitAsync<ChatRefEvent>(Ev.ChatMembersChanged, e => e.ChatId == chat);
        await bobRt.WaitAsync<ChatRefEvent>(Ev.ChatMembersChanged, e => e.ChatId == chat);
        (await bobRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat)).MemberCount.ShouldBe(3);
        var added = await daveRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat);
        added.SystemEvent.ShouldNotBeNull().Type.ShouldBe(SystemEventType.MembersAdded);
        added.SystemEvent.TargetUserIds.ShouldBe([dave.Id]);
        added.SystemEvent.ActorId.ShouldBe(owner.Id);
        await bobRt.BarrierAsync();
        bobRt.All<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat).ShouldBeEmpty();

        // History is readable but only the "added" service message is new for Dave.
        var page = await dave.PageAsync(chat);
        page.Messages.Count.ShouldBe(5);
        var daveChat = await dave.ChatAsync(chat);
        daveChat.UnreadCount.ShouldBe(1);
        daveChat.MemberCount.ShouldBe(3);
        daveChat.MyRole.ShouldBe(ChatRole.Member);

        // Idempotent retry; unknown users are failures.
        var again = await owner.AddMembersAsync(chat, dave.Id);
        again.Added.ShouldBeEmpty();
        again.Failed.ShouldBeEmpty();
        var unknown = Guid.NewGuid();
        (await owner.AddMembersAsync(chat, unknown)).Failed.ShouldBe([new AddMemberFailureDto { UserId = unknown, Code = ErrorCodes.UserNotFound }]);
        (await owner.ChatAsync(chat)).MemberCount.ShouldBe(3);
        await (await owner.TryAddMembersAsync(chat)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Members_may_add_people_only_while_add_members_is_allowed()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var erin = await factory.NewUserAsync("Erin");
        var frank = await factory.NewUserAsync("Frank");
        var chat = await owner.NewGroupAsync(bob);

        (await bob.AddMembersAsync(chat, erin.Id)).Added.ShouldBe([erin.Id]);
        (await bob.DetailsAsync(chat)).Members.Single(m => m.User.Id == erin.Id).Role.ShouldBe(ChatRole.Member);

        await owner.SetPermissionsAsync(chat, new ChatPermissionsDto { AddMembers = false });
        await (await bob.TryAddMembersAsync(chat, frank.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);

        // Admins bypass group permissions.
        await owner.SetRoleAsync(chat, bob.Id, ChatRole.Admin);
        (await bob.AddMembersAsync(chat, frank.Id)).Added.ShouldBe([frank.Id]);
        (await owner.ChatAsync(chat)).MemberCount.ShouldBe(4);
    }

    [Fact]
    public async Task Removing_a_member_bans_them_until_an_admin_adds_them_back()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var dave = await factory.NewUserAsync("Dave");
        var chat = await owner.NewGroupAsync(bob, dave);
        var token = await owner.CreateInviteLinkAsync(chat);
        await using var bobRt = await bob.ConnectAsync();
        await using var daveRt = await dave.ConnectAsync();

        await owner.RemoveMemberAsync(chat, dave.Id);

        await daveRt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);
        await bobRt.WaitAsync<ChatRefEvent>(Ev.ChatMembersChanged, e => e.ChatId == chat);
        (await bobRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat)).MemberCount.ShouldBe(2);
        var removed = await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.MemberRemoved);
        removed.SystemEvent!.TargetUserIds.ShouldBe([dave.Id]);
        removed.SystemEvent.ActorId.ShouldBe(owner.Id);
        // Dave is gone: he does not even get the service message about it.
        await daveRt.BarrierAsync();
        daveRt.All<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat).ShouldBeEmpty();
        daveRt.All<ChatRefEvent>(Ev.ChatMembersChanged, e => e.ChatId == chat).ShouldBeEmpty();

        await (await dave.GetRawAsync(ApiRoutes.Chats.ByIdPath(chat))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await dave.GetRawAsync(ApiRoutes.Messages.PagePath(chat))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await dave.TrySendAsync(chat, MessagingApi.Text("let me back"))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        (await dave.ChatsAsync()).ShouldNotContain(c => c.Id == chat);

        // Banned: invite links do not work and ordinary members cannot re-add him.
        await (await dave.GetRawAsync(ApiRoutes.Chats.JoinPath(token))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);
        await (await dave.PostEmptyAsync(ApiRoutes.Chats.JoinPath(token))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);
        var byMember = await bob.AddMembersAsync(chat, dave.Id);
        byMember.Added.ShouldBeEmpty();
        byMember.Failed.Single().UserId.ShouldBe(dave.Id);
        byMember.Failed.Single().Code.ShouldBe(ErrorCodes.NotChatAdmin);

        // An admin adding him lifts the ban: after leaving he may come back through the link.
        (await owner.AddMembersAsync(chat, dave.Id)).Added.ShouldBe([dave.Id]);
        (await factory.WithDbAsync(db => db.ChatBans.CountAsync(b => b.ChatId == chat && b.UserId == dave.Id))).ShouldBe(0);
        await dave.LeaveAsync(chat);
        (await (await dave.PostEmptyAsync(ApiRoutes.Chats.JoinPath(token))).ReadAsync<ChatSummaryDto>()).Id.ShouldBe(chat);
    }

    [Fact]
    public async Task Removal_permissions_follow_the_role_hierarchy()
    {
        var owner = await factory.NewUserAsync("Owner");
        var admin = await factory.NewUserAsync("Admin");
        var admin2 = await factory.NewUserAsync("Admin2");
        var member = await factory.NewUserAsync("Member");
        var member2 = await factory.NewUserAsync("Member2");
        var outsider = await factory.NewUserAsync("Outsider");
        var chat = await owner.NewGroupAsync(admin, admin2, member, member2);
        await owner.SetRoleAsync(chat, admin.Id, ChatRole.Admin);
        await owner.SetRoleAsync(chat, admin2.Id, ChatRole.Admin);

        await (await member.TryRemoveMemberAsync(chat, member2.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await (await admin.TryRemoveMemberAsync(chat, admin2.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await (await admin.TryRemoveMemberAsync(chat, owner.Id)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await (await admin.TryRemoveMemberAsync(chat, outsider.Id)).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.NotChatMember);
        await (await outsider.TryRemoveMemberAsync(chat, member.Id)).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);

        await admin.RemoveMemberAsync(chat, member2.Id);
        await owner.RemoveMemberAsync(chat, admin2.Id);

        var members = (await owner.DetailsAsync(chat)).Members.Select(m => m.User.Id).ToList();
        members.ShouldBe([owner.Id, admin.Id, member.Id], ignoreOrder: true);
        (await owner.ChatAsync(chat)).MemberCount.ShouldBe(3);

        // "Removing" myself is leaving.
        await member.RemoveMemberAsync(chat, member.Id);
        (await owner.ChatAsync(chat)).MemberCount.ShouldBe(2);
        (await factory.WithDbAsync(db => db.ChatBans.CountAsync(b => b.ChatId == chat && b.UserId == member.Id))).ShouldBe(0);
    }

    [Fact]
    public async Task Only_the_owner_manages_roles_and_can_transfer_ownership()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var outsider = await factory.NewUserAsync("Outsider");
        var chat = await owner.NewGroupAsync(bob, carol);
        await using var carolRt = await carol.ConnectAsync();

        await owner.SetRoleAsync(chat, bob.Id, ChatRole.Admin);
        await carolRt.WaitAsync<ChatRefEvent>(Ev.ChatMembersChanged, e => e.ChatId == chat);
        (await bob.ChatAsync(chat)).MyRole.ShouldBe(ChatRole.Admin);

        await (await bob.TrySetRoleAsync(chat, carol.Id, ChatRole.Admin)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await (await carol.TrySetRoleAsync(chat, bob.Id, ChatRole.Member)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await (await owner.TrySetRoleAsync(chat, outsider.Id, ChatRole.Admin)).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.NotChatMember);
        await (await owner.TrySetRoleAsync(chat, owner.Id, ChatRole.Member)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.CannotTargetSelf);
        await (await owner.TrySetRoleAsync(chat, bob.Id, (ChatRole)42)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await owner.SetRoleAsync(chat, owner.Id, ChatRole.Owner); // no-op

        // Demote and promote again.
        await owner.SetRoleAsync(chat, bob.Id, ChatRole.Member);
        (await bob.ChatAsync(chat)).MyRole.ShouldBe(ChatRole.Member);

        // Transfer: Carol becomes the owner, the old owner an admin.
        await owner.SetRoleAsync(chat, carol.Id, ChatRole.Owner);
        var roles = (await carol.DetailsAsync(chat)).Members.ToDictionary(m => m.User.Id, m => m.Role);
        roles[carol.Id].ShouldBe(ChatRole.Owner);
        roles[owner.Id].ShouldBe(ChatRole.Admin);
        roles[bob.Id].ShouldBe(ChatRole.Member);
        await (await owner.TrySetRoleAsync(chat, bob.Id, ChatRole.Admin)).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await carol.SetRoleAsync(chat, bob.Id, ChatRole.Admin);
    }

    [Fact]
    public async Task Leaving_notifies_the_rest_and_the_last_one_out_deletes_the_group()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(bob);
        await using var ownerRt = await owner.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        await bob.LeaveAsync(chat);
        await bobRt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);
        await ownerRt.WaitAsync<ChatRefEvent>(Ev.ChatMembersChanged, e => e.ChatId == chat);
        (await ownerRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat)).MemberCount.ShouldBe(1);
        var left = await ownerRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.MemberLeft);
        left.SystemEvent!.ActorId.ShouldBe(bob.Id);
        await (await bob.GetRawAsync(ApiRoutes.Chats.ByIdPath(chat))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await bob.PostEmptyAsync(ApiRoutes.Chats.LeavePath(chat))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        // Leaving is not a ban.
        (await factory.WithDbAsync(db => db.ChatBans.CountAsync(b => b.ChatId == chat))).ShouldBe(0);

        await owner.LeaveAsync(chat);
        await ownerRt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);
        (await factory.WithDbAsync(db => db.Chats.AnyAsync(c => c.Id == chat))).ShouldBeFalse();
        (await factory.WithDbAsync(db => db.Messages.AnyAsync(m => m.ChatId == chat))).ShouldBeFalse();
    }

    [Fact]
    public async Task Ownership_passes_to_the_oldest_admin_when_the_owner_leaves()
    {
        var owner = await factory.NewUserAsync("Owner");
        var oldMember = await factory.NewUserAsync("OldMember");
        var olderAdmin = await factory.NewUserAsync("OlderAdmin");
        var newerAdmin = await factory.NewUserAsync("NewerAdmin");
        var chat = await owner.NewGroupAsync(oldMember);
        (await owner.AddMembersAsync(chat, olderAdmin.Id)).Added.ShouldBe([olderAdmin.Id]);
        (await owner.AddMembersAsync(chat, newerAdmin.Id)).Added.ShouldBe([newerAdmin.Id]);
        // Promote the newer one first: seniority is by join time, not by promotion order.
        await owner.SetRoleAsync(chat, newerAdmin.Id, ChatRole.Admin);
        await owner.SetRoleAsync(chat, olderAdmin.Id, ChatRole.Admin);

        await owner.LeaveAsync(chat);

        var roles = (await oldMember.DetailsAsync(chat)).Members.ToDictionary(m => m.User.Id, m => m.Role);
        roles.Count.ShouldBe(3);
        roles[olderAdmin.Id].ShouldBe(ChatRole.Owner);
        roles[newerAdmin.Id].ShouldBe(ChatRole.Admin);
        roles[oldMember.Id].ShouldBe(ChatRole.Member);
        (await olderAdmin.ChatAsync(chat)).MyRole.ShouldBe(ChatRole.Owner);
    }

    [Fact]
    public async Task Ownership_passes_to_the_oldest_member_when_there_is_no_admin()
    {
        var owner = await factory.NewUserAsync("Owner");
        var oldest = await factory.NewUserAsync("Oldest");
        var newer = await factory.NewUserAsync("Newer");
        var chat = await owner.NewGroupAsync(oldest);
        (await owner.AddMembersAsync(chat, newer.Id)).Added.ShouldBe([newer.Id]);

        await owner.DeleteChatAsync(chat, forEveryone: false); // "delete" a group for me = leave

        var roles = (await newer.DetailsAsync(chat)).Members.ToDictionary(m => m.User.Id, m => m.Role);
        roles.Keys.ShouldBe([oldest.Id, newer.Id], ignoreOrder: true);
        roles[oldest.Id].ShouldBe(ChatRole.Owner);
        roles[newer.Id].ShouldBe(ChatRole.Member);
    }

    [Fact]
    public async Task Invite_link_lifecycle()
    {
        var owner = await factory.NewUserAsync("Owner");
        var bob = await factory.NewUserAsync("Bob");
        var joiner = await factory.NewUserAsync("Joiner");
        var result = await owner.CreateGroupAsync(new CreateGroupRequest { Title = "Club", Description = "Members only", MemberIds = [bob.Id] });
        var chat = result.Chat.Id;
        await using var bobRt = await bob.ConnectAsync();
        await using var joinerRt = await joiner.ConnectAsync();

        var token = await owner.CreateInviteLinkAsync(chat);
        token.Length.ShouldBe(22); // 16 random bytes, base64url without padding
        token.ShouldAllBe(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_');
        (await owner.CreateInviteLinkAsync(chat)).ShouldBe(token);
        (await bob.CreateInviteLinkAsync(chat)).ShouldBe(token); // AddMembers is allowed by default
        (await owner.DetailsAsync(chat)).InviteToken.ShouldBe(token);
        (await bob.DetailsAsync(chat)).InviteToken.ShouldBeNull();

        var preview = await joiner.GetAsync<ChatInvitePreviewDto>(ApiRoutes.Chats.JoinPath(token));
        preview.ChatId.ShouldBe(chat);
        preview.Title.ShouldBe("Club");
        preview.Description.ShouldBe("Members only");
        preview.MemberCount.ShouldBe(2);
        preview.IsMember.ShouldBeFalse();

        var joined = await (await joiner.PostEmptyAsync(ApiRoutes.Chats.JoinPath(token))).ReadAsync<ChatSummaryDto>();
        joined.Id.ShouldBe(chat);
        joined.MyRole.ShouldBe(ChatRole.Member);
        joined.MemberCount.ShouldBe(3);
        await joinerRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == chat);
        await bobRt.WaitAsync<ChatRefEvent>(Ev.ChatMembersChanged, e => e.ChatId == chat);
        (await bobRt.WaitAsync<ChatInfoDto>(Ev.ChatUpdated, c => c.Id == chat)).MemberCount.ShouldBe(3);
        var joinedMessage = await bobRt.WaitAsync<MessageDto>(Ev.MessageCreated, m => m.ChatId == chat && m.SystemEvent?.Type == SystemEventType.MemberJoinedByLink);
        joinedMessage.SystemEvent!.ActorId.ShouldBe(joiner.Id);

        // Joining again is a no-op.
        (await (await joiner.PostEmptyAsync(ApiRoutes.Chats.JoinPath(token))).ReadAsync<ChatSummaryDto>()).MemberCount.ShouldBe(3);
        (await joiner.GetAsync<ChatInvitePreviewDto>(ApiRoutes.Chats.JoinPath(token))).IsMember.ShouldBeTrue();
        (await owner.PageAsync(chat)).Messages.Count(m => m.SystemEvent?.Type == SystemEventType.MemberJoinedByLink).ShouldBe(1);

        // Revoked links stop working; a new link is a different token.
        await owner.DeleteRawAsync(ApiRoutes.Chats.InviteLinkPath(chat)).NoContentAsync();
        var late = await factory.NewUserAsync("Late");
        await (await late.GetRawAsync(ApiRoutes.Chats.JoinPath(token))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);
        await (await late.PostEmptyAsync(ApiRoutes.Chats.JoinPath(token))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);
        (await owner.DetailsAsync(chat)).InviteToken.ShouldBeNull();
        var fresh = await owner.CreateInviteLinkAsync(chat);
        fresh.ShouldNotBe(token);
        (await late.GetAsync<ChatInvitePreviewDto>(ApiRoutes.Chats.JoinPath(fresh))).ChatId.ShouldBe(chat);

        // Malformed and unknown tokens look exactly the same.
        await (await late.GetRawAsync(ApiRoutes.Chats.JoinPath("not a token!"))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);
        await (await late.PostEmptyAsync(ApiRoutes.Chats.JoinPath("AAAAAAAAAAAAAAAAAAAAAA"))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.InviteInvalid);

        // Without AddMembers members can neither create nor revoke links.
        await owner.SetPermissionsAsync(chat, new ChatPermissionsDto { AddMembers = false });
        await (await bob.PostEmptyAsync(ApiRoutes.Chats.InviteLinkPath(chat))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
        await (await bob.DeleteRawAsync(ApiRoutes.Chats.InviteLinkPath(chat))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.NotChatAdmin);
    }

    [Fact]
    public async Task Only_the_owner_deletes_a_group_for_everyone()
    {
        var owner = await factory.NewUserAsync("Owner");
        var admin = await factory.NewUserAsync("Admin");
        var bob = await factory.NewUserAsync("Bob");
        var chat = await owner.NewGroupAsync(admin, bob);
        await owner.SetRoleAsync(chat, admin.Id, ChatRole.Admin);
        await bob.SendAsync(chat, "hello");
        await using var adminRt = await admin.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();
        await using var ownerRt = await owner.ConnectAsync();

        await (await admin.DeleteRawAsync(ApiRoutes.Chats.DeletePath(chat, forEveryone: true))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await (await bob.DeleteRawAsync(ApiRoutes.Chats.DeletePath(chat, forEveryone: true))).ShouldFailAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        await owner.DeleteChatAsync(chat, forEveryone: true);
        foreach (var rt in new[] { ownerRt, adminRt, bobRt })
            await rt.WaitAsync<ChatRefEvent>(Ev.ChatRemoved, e => e.ChatId == chat);
        await (await bob.GetRawAsync(ApiRoutes.Chats.ByIdPath(chat))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        (await factory.WithDbAsync(db => db.Chats.AnyAsync(c => c.Id == chat))).ShouldBeFalse();
    }
}
