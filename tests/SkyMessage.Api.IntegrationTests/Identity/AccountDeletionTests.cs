using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class AccountDeletionTests(ApiFactory factory)
{
    [Fact]
    public async Task Wrong_password_keeps_the_account()
    {
        var user = await factory.NewUserAsync();

        await (await user.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = "wrong-password" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.WrongPassword);
        await (await user.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = "" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        (await user.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Id.ShouldBe(user.Id);
        (await factory.LoadUserAsync(user.Id)).IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Deleting_the_account_tombstones_the_profile_and_revokes_every_session()
    {
        var user = await factory.NewUserAsync(NewUsername("Gone"), "Soon Gone");
        var secondDevice = await user.SignInAgainAsync();
        var phone = TestPhones.Thai();
        await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164 });
        await user.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Bio = "about me" });
        var avatar = await factory.CreateMediaAsync(user.Id);
        await user.Client.PutAs<MeDto>(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = avatar });
        var viewer = await factory.NewUserAsync();
        await using var hub1 = await RealtimeProbe.ConnectAsync(user);
        await using var hub2 = await RealtimeProbe.ConnectAsync(secondDevice);

        await (await user.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = user.Password }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);

        (await hub1.WaitForAsync<SessionRevokedEvent>()).SessionId.ShouldBe(user.SessionId);
        (await hub2.WaitForAsync<SessionRevokedEvent>()).SessionId.ShouldBe(secondDevice.SessionId);
        await Task.WhenAll(hub1.Closed, hub2.Closed).WaitAsync(EventTimeout, Ct);
        await (await user.Client.Get(ApiRoutes.Users.Me)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        await (await secondDevice.Client.Get(ApiRoutes.Users.Me)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
        await (await factory.NewClient().Post(ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = secondDevice.Auth.RefreshToken }))
            .ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenInvalid);
        await (await factory.TryLoginAsync(user.Username, user.Password)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await (await factory.TryLoginAsync(phone.E164, user.Password)).ShouldFailAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);

        var stored = await factory.LoadUserAsync(user.Id);
        stored.IsDeleted.ShouldBeTrue();
        stored.DeletedAt.ShouldNotBeNull();
        stored.Username.ShouldBe($"deleted_{user.Id:N}");
        stored.DisplayName.ShouldBe(User.DeletedDisplayName);
        stored.Bio.ShouldBeNull();
        stored.PhoneNumber.ShouldBeNull();
        stored.AvatarMediaId.ShouldBeNull();
        (await factory.WithDbAsync(db => db.Sessions.CountAsync(s => s.UserId == user.Id && s.RevokedAt == null, Ct))).ShouldBe(0);
        (await factory.WithDbAsync(db => db.Chats.AnyAsync(c => c.PrivateKey == Chat.SavedKeyFor(user.Id), Ct))).ShouldBeFalse();

        var profile = await viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(user.Id));
        profile.User.IsDeleted.ShouldBeTrue();
        profile.User.DisplayName.ShouldBe(User.DeletedDisplayName);
        profile.User.AvatarMediaId.ShouldBeNull();
        profile.Bio.ShouldBeNull();
        profile.PhoneNumber.ShouldBeNull();
        profile.CanMessage.ShouldBeFalse();
        profile.CanCall.ShouldBeFalse();
        await (await viewer.Client.Get(ApiRoutes.Users.ByUsernamePath(user.Username)))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
        await (await viewer.Client.Get(ApiRoutes.Users.ByUsernamePath(stored.Username)))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task The_username_and_phone_number_become_free_again()
    {
        var user = await factory.NewUserAsync(NewUsername("Free"));
        var phone = TestPhones.Russian();
        await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164 });

        await (await user.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = user.Password }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);

        var successor = await factory.NewUserAsync(user.Username);
        successor.Id.ShouldNotBe(user.Id);
        var me = await successor.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.National });
        me.PhoneNumber.ShouldBe(phone.E164);
    }

    [Fact]
    public async Task Related_users_are_told_and_contacts_disappear_on_both_sides()
    {
        var leaving = await factory.NewUserAsync();
        var friend = await factory.NewUserAsync();
        var chatPeer = await factory.NewUserAsync();
        await friend.AddContactAsync(leaving, "Old friend");
        await leaving.AddContactAsync(friend);
        var chatId = await leaving.CreatePrivateChatWithMessageAsync(chatPeer, "message that stays");
        await using var friendHub = await RealtimeProbe.ConnectAsync(friend);
        await using var peerHub = await RealtimeProbe.ConnectAsync(chatPeer);

        await (await leaving.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = leaving.Password }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);

        await friendHub.WaitForAsync<UserRefEvent>(e => e.UserId == leaving.Id);
        await friendHub.WaitForAsync<ContactsChangedEvent>();
        await peerHub.WaitForAsync<UserRefEvent>(e => e.UserId == leaving.Id);
        (await friend.Client.GetAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root)).ShouldBeEmpty();
        (await factory.WithDbAsync(db => db.Contacts.CountAsync(c => c.OwnerId == leaving.Id || c.ContactUserId == leaving.Id, Ct))).ShouldBe(0);

        // Messages stay, shown as from a deleted account.
        var page = await chatPeer.Client.GetAs<MessagesPageDto>(ApiRoutes.Messages.PagePath(chatId));
        var message = page.Messages.ShouldHaveSingleItem();
        message.SenderId.ShouldBe(leaving.Id);
        message.Text.ShouldBe("message that stays");
        var sender = page.Users.Single(u => u.Id == leaving.Id);
        sender.IsDeleted.ShouldBeTrue();
        sender.DisplayName.ShouldBe(User.DeletedDisplayName);
    }

    [Fact]
    public async Task Groups_are_left_with_ownership_passed_on_and_solo_groups_are_deleted()
    {
        var owner = await factory.NewUserAsync();
        var admin = await factory.NewUserAsync();
        var member = await factory.NewUserAsync();
        var shared = await owner.CreateGroupAsync(member, admin);
        await (await owner.Client.Put(ApiRoutes.Chats.MemberRolePath(shared, admin.Id), new SetMemberRoleRequest { Role = ChatRole.Admin }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);
        var solo = await owner.CreateGroupAsync();

        await (await owner.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = owner.Password }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);

        var asAdmin = await admin.Client.GetAs<ChatSummaryDto>(ApiRoutes.Chats.ByIdPath(shared));
        asAdmin.MyRole.ShouldBe(ChatRole.Owner);
        asAdmin.MemberCount.ShouldBe(2);
        (await member.Client.GetAs<ChatSummaryDto>(ApiRoutes.Chats.ByIdPath(shared))).MyRole.ShouldBe(ChatRole.Member);
        var members = await factory.WithDbAsync(db => db.ChatMembers.AsNoTracking().Where(m => m.ChatId == shared).Select(m => m.UserId).ToListAsync(Ct));
        members.ShouldBe([admin.Id, member.Id], ignoreOrder: true);
        (await factory.WithDbAsync(db => db.Chats.AsNoTracking().SingleAsync(c => c.Id == solo, Ct))).IsDeleted.ShouldBeTrue();
    }
}
