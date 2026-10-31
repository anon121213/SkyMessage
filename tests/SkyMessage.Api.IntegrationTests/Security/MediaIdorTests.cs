using System.Net;
using System.Text;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// Media and uploads of other people (SPEC §6.4): the owner, viewers of a visible message, members of a chat with that
/// avatar and people allowed to see a profile photo — nobody else, whatever id or signature they present.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MediaIdorTests(ApiFactory factory)
{
    [Fact]
    public async Task Unshared_media_and_uploads_belong_to_their_owner_only()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var eve = await factory.RegisterUserAsync("eve");
        var photo = await MediaKit.UploadPhotoAsync(alice, "private.jpg");
        var bytes = MediaKit.Jpeg(32, 32);
        var pending = await MediaKit.StartAsync(alice, MediaKind.Photo, "pending.jpg", "image/jpeg", bytes.Length);

        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Media.ByIdPath(photo.Id), null, HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        foreach (var path in new[] { ApiRoutes.Media.ContentPath(photo.Id, null), ApiRoutes.Media.ThumbnailPath(photo.Id, null), ApiRoutes.Media.ContentPath(photo.Id, null, download: true) })
        {
            using var response = await eve.Client.GetAsync(path, SecurityKit.Ct);
            await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        }
        // eve's own valid signature only proves who she is.
        using (var signed = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, eve.Sign(photo.Id))))
            await signed.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // Upload sessions: neither the finished nor the pending one can be read, written or completed.
        foreach (var id in new[] { photo.Id, pending.MediaId })
        {
            await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Media.UploadPath(id), null, HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
            using var put = await MediaKit.PutChunkAsync(eve.Client, pending with { MediaId = id }, 0, bytes);
            await put.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
            using var complete = await eve.Client.PostAsync(ApiRoutes.Media.UploadCompletePath(id), null, SecurityKit.Ct);
            await complete.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        }

        // Nothing eve did changed alice's uploads.
        var session = await alice.Client.GetJsonAsync<UploadSessionDto>(ApiRoutes.Media.UploadPath(pending.MediaId));
        session.Status.ShouldBe(MediaStatus.Uploading);
        session.ReceivedChunks.ShouldBeEmpty();
        using (var own = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, alice.Sign(photo.Id))))
        {
            await own.ShouldHaveStatusAsync(HttpStatusCode.OK);
            (await own.Content.ReadAsByteArrayAsync(SecurityKit.Ct)).Length.ShouldBe((int)photo.SizeBytes);
        }
    }

    [Fact]
    public async Task Someone_elses_media_cannot_be_attached_or_used_as_an_avatar()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var eve = await factory.RegisterUserAsync("eve");
        var photo = await MediaKit.UploadPhotoAsync(alice, "alice.jpg");
        var evesGroup = await eve.CreateGroupAsync("Eve's group");

        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Messages.RootPath(eve.SavedChatId),
            new SendMessageRequest { ClientMessageId = Guid.NewGuid(), MediaIds = [photo.Id] }, HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = photo.Id },
            HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Put, ApiRoutes.Chats.AvatarPath(evesGroup.Id), new SetChatAvatarRequest { MediaId = photo.Id },
            HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = "Stolen avatar", AvatarMediaId = photo.Id },
            HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // Still invisible to eve afterwards.
        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Media.ByIdPath(photo.Id), null, HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        (await eve.Client.GetJsonAsync<MeDto>(ApiRoutes.Users.Me)).AvatarMediaId.ShouldBeNull();
    }

    [Fact]
    public async Task Media_in_a_chat_is_visible_to_its_members_and_to_whoever_it_is_forwarded_to()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var eve = await factory.RegisterUserAsync("eve");
        var chat = await alice.OpenPrivateChatAsync(bob);
        var photo = await MediaKit.UploadPhotoAsync(alice, "holiday.jpg");
        var message = await alice.SendMediaAsync(chat.Id, "holiday", photo.Id);
        message.Type.ShouldBe(MessageType.Photo);

        using (var forEve = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, eve.Sign(photo.Id))))
            await forEve.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Media.ByIdPath(photo.Id), null, HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        using (var forBob = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, bob.Sign(photo.Id))))
            await forBob.ShouldHaveStatusAsync(HttpStatusCode.OK);
        (await bob.Client.GetJsonAsync<MediaDto>(ApiRoutes.Media.ByIdPath(photo.Id))).Id.ShouldBe(photo.Id);

        // Forwarding is sharing: once bob forwards it to eve, eve may see it (and only through that message).
        var bobAndEve = await bob.OpenPrivateChatAsync(eve);
        var forwarded = await bob.Client.PostJsonAsync<List<MessageDto>>(ApiRoutes.Messages.ForwardPath(bobAndEve.Id),
            new ForwardMessagesRequest { FromChatId = chat.Id, MessageIds = [message.Id], ClientMessageIds = [Guid.NewGuid()] });
        forwarded.Single().Attachments.Single().Id.ShouldBe(photo.Id);
        forwarded.Single().Forward!.FromUserId.ShouldBe(alice.Id);
        using (var afterForward = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, eve.Sign(photo.Id))))
            await afterForward.ShouldHaveStatusAsync(HttpStatusCode.OK);

        // The forward does not open the source chat to eve.
        await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Messages.PagePath(chat.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }

    [Fact]
    public async Task Active_content_is_always_downloaded_never_rendered()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var chat = await alice.OpenPrivateChatAsync(bob);
        var html = Encoding.UTF8.GetBytes("<html><script>fetch('/api/users/me').then(r => r.text()).then(alert)</script></html>");
        var page = await MediaKit.UploadAsync(alice, MediaKind.File, "..\\..\\invite‮gpj.html", "text/html", html);
        var svg = await MediaKit.UploadAsync(alice, MediaKind.File, "logo.svg", "image/svg+xml",
            Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"));
        await alice.SendMediaAsync(chat.Id, "files", page.Id, svg.Id);

        // Names are a single safe segment: no path, no bidi override, no header injection.
        page.FileName.ShouldNotContain("\\");
        page.FileName.ShouldNotContain("/");
        page.FileName.ShouldNotContain("‮");

        foreach (var media in new[] { page, svg })
        {
            foreach (var path in new[] { ApiRoutes.Media.ContentPath(media.Id, bob.Sign(media.Id)), ApiRoutes.Media.ContentPath(media.Id, null) })
            {
                using var response = path.Contains("?t=", StringComparison.Ordinal)
                    ? await MediaKit.GetSignedAsync(factory, path)
                    : await bob.Client.GetAsync(path, SecurityKit.Ct);
                await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
                response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream", media.FileName);
                response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment", media.FileName);
                CorsAndHeadersTests.Header(response, "Content-Security-Policy").ShouldBe("sandbox");
                CorsAndHeadersTests.Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
                response.Headers.CacheControl!.Private.ShouldBeTrue();
            }
        }

        // Inline images keep their type but still run sandboxed.
        var photo = await MediaKit.UploadPhotoAsync(alice);
        await alice.SendMediaAsync(chat.Id, null, photo.Id);
        using var image = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(photo.Id, bob.Sign(photo.Id)));
        await image.ShouldHaveStatusAsync(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        image.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        CorsAndHeadersTests.Header(image, "Content-Security-Policy").ShouldBe("sandbox");
    }

    [Fact]
    public async Task Active_types_are_rejected_as_photos_and_header_injection_in_names_is_neutralized()
    {
        var alice = await factory.RegisterUserAsync("alice");
        foreach (var contentType in new[] { "image/svg+xml", "text/html", "application/javascript" })
        {
            await alice.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Media.Uploads,
                new CreateUploadRequest { Kind = MediaKind.Photo, FileName = "x.jpg", ContentType = contentType, SizeBytes = 100 },
                HttpStatusCode.BadRequest, ErrorCodes.MediaUnsupported);
        }

        var file = await MediaKit.UploadAsync(alice, MediaKind.File, "report\r\nSet-Cookie: session=stolen\r\n.txt", "text/plain", "hello"u8.ToArray());
        file.FileName.ShouldNotContain("\r");
        file.FileName.ShouldNotContain("\n");
        using var response = await alice.Client.GetAsync(ApiRoutes.Media.ContentPath(file.Id, null), SecurityKit.Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        CorsAndHeadersTests.Header(response, "Content-Disposition")!.ShouldNotContain("\n");
    }

    [Fact]
    public async Task Profile_photos_follow_privacy_and_blocks()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var eve = await factory.RegisterUserAsync("eve");
        var frank = await factory.RegisterUserAsync("frank");
        var avatar = await MediaKit.UploadPhotoAsync(alice, "me.jpg");
        await alice.Client.PutJsonAsync<MeDto>(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = avatar.Id });
        var privacy = await alice.Client.GetJsonAsync<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy);
        await alice.Client.PutJsonAsync<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy, privacy with { ProfilePhoto = PrivacyLevel.Nobody });

        // Hidden: the id is not handed out, and knowing it does not help.
        (await eve.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id))).User.AvatarMediaId.ShouldBeNull();
        (await eve.Client.PostJsonAsync<List<UserSummaryDto>>(ApiRoutes.Users.Batch, new GetUsersRequest { UserIds = [alice.Id] }))
            .Single().AvatarMediaId.ShouldBeNull();
        using (var hidden = await eve.Client.GetAsync(ApiRoutes.Media.ContentPath(avatar.Id, null), SecurityKit.Ct))
            await hidden.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var hiddenThumb = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ThumbnailPath(avatar.Id, eve.Sign(avatar.Id))))
            await hiddenThumb.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // Visible to everybody again ...
        await alice.Client.PutJsonAsync<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy, privacy with { ProfilePhoto = PrivacyLevel.Everybody });
        (await eve.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id))).User.AvatarMediaId.ShouldBe(avatar.Id);
        using (var visible = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(avatar.Id, eve.Sign(avatar.Id))))
            await visible.ShouldHaveStatusAsync(HttpStatusCode.OK);

        // ... except for people alice blocked.
        await alice.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Blocks.ByUserIdPath(frank.Id));
        (await frank.Client.GetJsonAsync<UserProfileDto>(ApiRoutes.Users.ByIdPath(alice.Id))).User.AvatarMediaId.ShouldBeNull();
        using (var blocked = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(avatar.Id, frank.Sign(avatar.Id))))
            await blocked.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        // A replaced avatar is no longer reachable through the profile rule.
        var next = await MediaKit.UploadPhotoAsync(alice, "me2.jpg");
        await alice.Client.PutJsonAsync<MeDto>(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = next.Id });
        var stranger = await factory.RegisterUserAsync("stranger");
        using (var old = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(avatar.Id, stranger.Sign(avatar.Id))))
            await old.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var current = await MediaKit.GetSignedAsync(factory, ApiRoutes.Media.ContentPath(next.Id, stranger.Sign(next.Id))))
            await current.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }
}
