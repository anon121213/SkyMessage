using System.Net;
using System.Net.Http.Json;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>
/// Who may read media (content, thumbnail and metadata): the owner; chat members through a visible attachment;
/// members through a chat avatar; anyone allowed to see a user's profile photo. Everyone else gets 404 (SPEC §6.4).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MediaAccessTests(ApiFactory factory)
{
    [Fact]
    public async Task Owner_reads_unattached_media_and_nobody_else_does()
    {
        var owner = await factory.RegisterUserAsync();
        var stranger = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync();

        await owner.ShouldSeeAsync(media);
        await stranger.ShouldNotSeeAsync(media.Id);
        using var signed = await factory.CreateJsonClient().GetContentAsync(media.Id, stranger.Sign(media.Id));
        await signed.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
    }

    [Fact]
    public async Task Media_that_is_not_ready_is_the_owners_business_only()
    {
        var owner = await factory.RegisterUserAsync();
        var stranger = await factory.RegisterUserAsync();
        var session = await owner.StartUploadAsync(UploadRequest(MediaKind.Photo, "pending.png", "image/png", 1000));

        (await owner.GetMediaAsync(session.MediaId)).Status.ShouldBe(MediaStatus.Uploading);
        using (var content = await owner.Client.GetContentAsync(session.MediaId))
            await content.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.MediaNotReady);
        using (var thumbnail = await owner.Client.GetThumbnailAsync(session.MediaId))
            await thumbnail.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.MediaNotReady);
        await stranger.ShouldNotSeeAsync(session.MediaId);
    }

    [Fact]
    public async Task Unknown_media_is_not_found()
    {
        var user = await factory.RegisterUserAsync();

        await user.ShouldNotSeeAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task Members_of_a_private_chat_read_its_attachments_and_outsiders_do_not()
    {
        var alice = await factory.RegisterUserAsync();
        var bob = await factory.RegisterUserAsync();
        var outsider = await factory.RegisterUserAsync();
        var photo = TestImages.Png(200, 100);
        var media = await alice.UploadReadyAsync(MediaKind.Photo, photo, "image/png", "holiday.png");
        var chatId = await alice.OpenPrivateChatAsync(bob.Id);

        var message = await alice.SendMediaAsync(chatId, media.Id);

        message.Attachments.Single().Id.ShouldBe(media.Id);
        await bob.ShouldSeeAsync(media);
        (await bob.Client.DownloadAsync(media.Id)).ShouldBeSameBytes(photo, "attachment seen by the peer");
        (await bob.GetMediaAsync(media.Id)).ShouldSatisfyAllConditions(
            m => m.Status.ShouldBe(MediaStatus.Ready),
            m => m.FileName.ShouldBe("holiday.png"),
            m => (m.Width, m.Height).ShouldBe((200, 100)));
        await outsider.ShouldNotSeeAsync(media.Id);
    }

    [Fact]
    public async Task Reading_an_attachment_does_not_expose_the_upload_itself()
    {
        var owner = await factory.RegisterUserAsync();
        var member = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync();
        await owner.SendMediaAsync(await owner.OpenPrivateChatAsync(member.Id), media.Id);

        await member.ShouldSeeAsync(media);
        using (var status = await member.Client.GetAsync(ApiRoutes.Media.UploadPath(media.Id), Ct))
            await status.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var complete = await member.Client.PostCompleteAsync(media.Id))
            await complete.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var chunk = await member.Client.PutChunkAsync(media.Id, 0, new byte[10]))
            await chunk.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
    }

    [Fact]
    public async Task Other_peoples_media_cannot_be_attached_or_used_as_an_avatar()
    {
        var owner = await factory.RegisterUserAsync();
        var thief = await factory.RegisterUserAsync();
        var accomplice = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync("private.png");
        var chatId = await thief.OpenPrivateChatAsync(accomplice.Id);
        var groupId = await thief.CreateGroupAsync([accomplice]);

        using (var send = await thief.Client.PostAsJsonAsync(ApiRoutes.Messages.RootPath(chatId),
                   new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = [media.Id] }, SkyJson.Options, Ct))
            await send.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var avatar = await thief.Client.PutAsJsonAsync(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = media.Id }, SkyJson.Options, Ct))
            await avatar.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var groupAvatar = await thief.Client.PutAsJsonAsync(ApiRoutes.Chats.AvatarPath(groupId),
                   new SetChatAvatarRequest { MediaId = media.Id }, SkyJson.Options, Ct))
            await groupAvatar.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var newGroup = await thief.Client.PostAsJsonAsync(ApiRoutes.Chats.Groups,
                   new CreateGroupRequest { Title = "Stolen", MemberIds = [accomplice.Id], AvatarMediaId = media.Id }, SkyJson.Options, Ct))
            await newGroup.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        await thief.ShouldNotSeeAsync(media.Id);
        await accomplice.ShouldNotSeeAsync(media.Id);
    }

    [Fact]
    public async Task Messages_of_a_chat_one_is_not_in_cannot_be_forwarded_to_reach_their_media()
    {
        var alice = await factory.RegisterUserAsync();
        var bob = await factory.RegisterUserAsync();
        var outsider = await factory.RegisterUserAsync();
        var chatId = await alice.OpenPrivateChatAsync(bob.Id);
        var media = await alice.UploadPhotoAsync();
        var message = await alice.SendMediaAsync(chatId, media.Id);
        var savedChatId = outsider.Auth.User.SavedMessagesChatId;

        using var forward = await outsider.Client.PostAsJsonAsync(ApiRoutes.Messages.ForwardPath(savedChatId), new ForwardMessagesRequest
        {
            FromChatId = chatId,
            MessageIds = [message.Id],
            ClientMessageIds = [Guid.NewGuid()],
        }, SkyJson.Options, Ct);

        forward.IsSuccessStatusCode.ShouldBeFalse(await forward.Content.ReadAsStringAsync(Ct));
        await outsider.ShouldNotSeeAsync(media.Id);
    }

    [Fact]
    public async Task Denials_are_not_cached_so_newly_granted_access_works_at_once()
    {
        var owner = await factory.RegisterUserAsync();
        var member = await factory.RegisterUserAsync();
        var newcomer = await factory.RegisterUserAsync();
        var groupId = await owner.CreateGroupAsync([member]);
        var photo = await owner.UploadPhotoAsync("history.png");
        await owner.SendMediaAsync(groupId, photo.Id);
        var avatar = await owner.UploadPhotoAsync("me.png");
        await owner.SetAvatarAsync(avatar.Id);
        await owner.SetProfilePhotoPrivacyAsync(PrivacyLevel.Contacts);
        await newcomer.ShouldNotSeeAsync(photo.Id);
        await newcomer.ShouldNotSeeAsync(avatar.Id);

        using (var add = await owner.Client.PostAsJsonAsync(ApiRoutes.Chats.MembersPath(groupId), new AddMembersRequest { UserIds = [newcomer.Id] }, SkyJson.Options, Ct))
        {
            await add.ShouldHaveStatusAsync(HttpStatusCode.OK);
            (await add.Content.ReadFromJsonAsync<AddMembersResultDto>(SkyJson.Options, Ct))!.Added.ShouldBe([newcomer.Id]);
        }
        await owner.AddContactAsync(newcomer);

        await newcomer.ShouldSeeAsync(photo);
        await newcomer.ShouldSeeAsync(avatar);
    }

    [Fact]
    public async Task Group_members_read_attachments_until_they_leave()
    {
        var owner = await factory.RegisterUserAsync();
        var member = await factory.RegisterUserAsync();
        var leaver = await factory.RegisterUserAsync();
        var outsider = await factory.RegisterUserAsync();
        var groupId = await owner.CreateGroupAsync([member, leaver]);
        var file = await owner.UploadReadyAsync(MediaKind.File, RandomBytes(3000, seed: 61), "application/pdf", "minutes.pdf");
        await owner.SendMediaAsync(groupId, file.Id);

        await member.ShouldSeeAsync(file);
        await outsider.ShouldNotSeeAsync(file.Id);

        await leaver.LeaveChatAsync(groupId);
        await leaver.ShouldNotSeeAsync(file.Id);
    }

    [Fact]
    public async Task Attachment_of_a_message_deleted_for_everyone_is_no_longer_readable()
    {
        var owner = await factory.RegisterUserAsync();
        var reader = await factory.RegisterUserAsync();
        var lateReader = await factory.RegisterUserAsync();
        var groupId = await owner.CreateGroupAsync([reader, lateReader]);
        var media = await owner.UploadPhotoAsync();
        var message = await owner.SendMediaAsync(groupId, media.Id);
        await reader.ShouldSeeAsync(media);

        await owner.DeleteMessageAsync(groupId, message.Id, forEveryone: true);

        await lateReader.ShouldNotSeeAsync(media.Id);
        // Positive decisions are cached for 60 s (SPEC §6.4); once that is gone the earlier reader is denied too.
        factory.ForgetCachedDecisions();
        await reader.ShouldNotSeeAsync(media.Id);
        await owner.ShouldSeeAsync(media);
    }

    [Fact]
    public async Task Deleting_a_message_for_me_hides_its_attachment_from_me_only()
    {
        var owner = await factory.RegisterUserAsync();
        var hider = await factory.RegisterUserAsync();
        var other = await factory.RegisterUserAsync();
        var groupId = await owner.CreateGroupAsync([hider, other]);
        var media = await owner.UploadPhotoAsync();
        var message = await owner.SendMediaAsync(groupId, media.Id);

        await hider.DeleteMessageAsync(groupId, message.Id, forEveryone: false);

        await hider.ShouldNotSeeAsync(media.Id);
        await other.ShouldSeeAsync(media);
    }

    [Fact]
    public async Task Clearing_history_hides_earlier_attachments_from_that_member_only()
    {
        var owner = await factory.RegisterUserAsync();
        var clearer = await factory.RegisterUserAsync();
        var other = await factory.RegisterUserAsync();
        var groupId = await owner.CreateGroupAsync([clearer, other]);
        var before = await owner.UploadPhotoAsync("before.png");
        await owner.SendMediaAsync(groupId, before.Id);

        await clearer.ClearHistoryAsync(groupId);
        var after = await owner.UploadPhotoAsync("after.png");
        await owner.SendMediaAsync(groupId, after.Id);

        await clearer.ShouldNotSeeAsync(before.Id);
        await clearer.ShouldSeeAsync(after);
        await other.ShouldSeeAsync(before);
    }

    [Fact]
    public async Task Forwarded_attachments_are_readable_in_the_target_chat_even_after_the_original_is_deleted()
    {
        var alice = await factory.RegisterUserAsync();
        var bob = await factory.RegisterUserAsync();
        var carol = await factory.RegisterUserAsync();
        var outsider = await factory.RegisterUserAsync();
        var privateChat = await alice.OpenPrivateChatAsync(bob.Id);
        var media = await alice.UploadPhotoAsync();
        var original = await alice.SendMediaAsync(privateChat, media.Id);
        var groupId = await bob.CreateGroupAsync([carol]);

        var forwarded = await bob.ForwardAsync(privateChat, groupId, original.Id);

        forwarded.Single().Attachments.Single().Id.ShouldBe(media.Id);
        await carol.ShouldSeeAsync(media);
        await outsider.ShouldNotSeeAsync(media.Id);

        await alice.DeleteMessageAsync(privateChat, original.Id, forEveryone: true);
        factory.ForgetCachedDecisions();
        await carol.ShouldSeeAsync(media);
    }

    [Fact]
    public async Task Deleting_a_group_for_everyone_revokes_access_to_its_attachments_and_avatar()
    {
        var owner = await factory.RegisterUserAsync();
        var member = await factory.RegisterUserAsync();
        var avatar = await owner.UploadPhotoAsync("group.png");
        var groupId = await owner.CreateGroupAsync([member], avatar.Id);
        var photo = await owner.UploadPhotoAsync("party.png");
        await owner.SendMediaAsync(groupId, photo.Id);
        await member.ShouldSeeAsync(photo);
        await member.ShouldSeeAsync(avatar);

        await owner.DeleteChatForEveryoneAsync(groupId);
        factory.ForgetCachedDecisions();

        await member.ShouldNotSeeAsync(photo.Id);
        await member.ShouldNotSeeAsync(avatar.Id);
        await owner.ShouldSeeAsync(photo);
    }

    [Fact]
    public async Task Chat_avatar_is_readable_by_members_only()
    {
        var owner = await factory.RegisterUserAsync();
        var member = await factory.RegisterUserAsync();
        var outsider = await factory.RegisterUserAsync();
        var avatar = await owner.UploadPhotoAsync("group.png");

        var groupId = await owner.CreateGroupAsync([member], avatar.Id);

        await member.ShouldSeeAsync(avatar);
        await outsider.ShouldNotSeeAsync(avatar.Id);

        var newAvatar = await owner.UploadPhotoAsync("group2.png");
        await owner.SetChatAvatarAsync(groupId, newAvatar.Id);
        await member.ShouldSeeAsync(newAvatar);
        await outsider.ShouldNotSeeAsync(newAvatar.Id);
        factory.ForgetCachedDecisions();
        await member.ShouldNotSeeAsync(avatar.Id);
    }

    [Fact]
    public async Task Profile_photo_is_public_by_default()
    {
        var owner = await factory.RegisterUserAsync();
        var stranger = await factory.RegisterUserAsync();
        var avatar = await owner.UploadPhotoAsync("me.png");

        await owner.SetAvatarAsync(avatar.Id);

        await stranger.ShouldSeeAsync(avatar);
        using var signed = await factory.CreateJsonClient().GetThumbnailAsync(avatar.Id, stranger.Sign(avatar.Id));
        await signed.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Profile_photo_limited_to_contacts_is_hidden_from_everyone_else()
    {
        var owner = await factory.RegisterUserAsync();
        var contact = await factory.RegisterUserAsync();
        var stranger = await factory.RegisterUserAsync();
        var admirer = await factory.RegisterUserAsync();
        var avatar = await owner.UploadPhotoAsync("me.png");
        await owner.SetAvatarAsync(avatar.Id);
        await owner.SetProfilePhotoPrivacyAsync(PrivacyLevel.Contacts);
        await owner.AddContactAsync(contact);
        // Having the owner in *my* contacts does not count: the owner decides.
        await admirer.AddContactAsync(owner);

        await contact.ShouldSeeAsync(avatar);
        await stranger.ShouldNotSeeAsync(avatar.Id);
        await admirer.ShouldNotSeeAsync(avatar.Id);
    }

    [Fact]
    public async Task Profile_photo_shown_to_nobody_is_hidden_even_from_contacts_and_chat_partners()
    {
        var owner = await factory.RegisterUserAsync();
        var contact = await factory.RegisterUserAsync();
        var avatar = await owner.UploadPhotoAsync("me.png");
        await owner.SetAvatarAsync(avatar.Id);
        await owner.AddContactAsync(contact);
        var chatId = await owner.OpenPrivateChatAsync(contact.Id);
        var photo = await owner.UploadPhotoAsync("shared.png");
        await owner.SendMediaAsync(chatId, photo.Id);

        await owner.SetProfilePhotoPrivacyAsync(PrivacyLevel.Nobody);

        await contact.ShouldNotSeeAsync(avatar.Id);
        await contact.ShouldSeeAsync(photo);
        await owner.ShouldSeeAsync(avatar);
    }

    [Fact]
    public async Task Blocked_users_cannot_read_the_blockers_profile_photo()
    {
        var owner = await factory.RegisterUserAsync();
        var blocked = await factory.RegisterUserAsync();
        var stranger = await factory.RegisterUserAsync();
        var avatar = await owner.UploadPhotoAsync("me.png");
        await owner.SetAvatarAsync(avatar.Id);

        await owner.BlockAsync(blocked);

        await blocked.ShouldNotSeeAsync(avatar.Id);
        await stranger.ShouldSeeAsync(avatar);
        // Blocks come first: being in the blocker's contacts does not help.
        var blockedContact = await factory.RegisterUserAsync();
        await owner.AddContactAsync(blockedContact);
        await owner.BlockAsync(blockedContact);
        await blockedContact.ShouldNotSeeAsync(avatar.Id);
        // Blocking someone does not hide *their* photo from me.
        var blockedAvatar = await blocked.UploadPhotoAsync("blocked.png");
        await blocked.SetAvatarAsync(blockedAvatar.Id);
        await owner.ShouldSeeAsync(blockedAvatar);
    }

    [Fact]
    public async Task Replaced_or_removed_profile_photos_are_no_longer_shared()
    {
        var owner = await factory.RegisterUserAsync();
        var first = await factory.RegisterUserAsync();
        var second = await factory.RegisterUserAsync();
        var oldAvatar = await owner.UploadPhotoAsync("old.png");
        var newAvatar = await owner.UploadPhotoAsync("new.png");
        var otherPhoto = await owner.UploadPhotoAsync("private.png");
        await owner.SetAvatarAsync(oldAvatar.Id);

        await owner.SetAvatarAsync(newAvatar.Id);

        await first.ShouldNotSeeAsync(oldAvatar.Id);
        await first.ShouldSeeAsync(newAvatar);
        await first.ShouldNotSeeAsync(otherPhoto.Id);

        await owner.RemoveAvatarAsync();
        await second.ShouldNotSeeAsync(newAvatar.Id);
    }

    [Fact]
    public async Task Avatar_of_a_deleted_account_is_no_longer_shared()
    {
        var owner = await factory.RegisterUserAsync();
        var stranger = await factory.RegisterUserAsync();
        var avatar = await owner.UploadPhotoAsync("me.png");
        await owner.SetAvatarAsync(avatar.Id);

        using (var delete = await owner.Client.PostAsync(ApiRoutes.Users.MeDelete,
                   System.Net.Http.Json.JsonContent.Create(new DeleteAccountRequest { Password = Password }, options: SkyJson.Options), Ct))
            await delete.ShouldHaveStatusAsync(HttpStatusCode.NoContent);

        await stranger.ShouldNotSeeAsync(avatar.Id);
    }
}
