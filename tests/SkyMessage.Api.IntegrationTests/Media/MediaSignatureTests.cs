using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Media.MediaTestKit;

namespace SkyMessage.Api.IntegrationTests.Media;

/// <summary>
/// Content and thumbnails are anonymous endpoints: they need a bearer user or a <c>?t=</c> signature that is bound
/// to one media id and dies with its session (SPEC §3, §6.2, §6.4).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MediaSignatureTests(ApiFactory factory)
{
    [Fact]
    public async Task Signed_urls_work_without_an_authorization_header()
    {
        var owner = await factory.RegisterUserAsync();
        var photo = TestImages.Png(90, 60);
        var media = await owner.UploadReadyAsync(MediaKind.Photo, photo, "image/png", "signed.png");
        var anonymous = factory.CreateJsonClient();

        (await anonymous.DownloadAsync(media.Id, owner.Sign(media.Id))).ShouldBeSameBytes(photo, "content via ?t=");
        using var thumbnail = TestImages.Decode(await anonymous.DownloadThumbnailAsync(media.Id, owner.Sign(media.Id)));
        (thumbnail.Width, thumbnail.Height).ShouldBe((90, 60));
    }

    [Fact]
    public async Task Without_bearer_or_signature_the_request_is_unauthorized()
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync();
        var anonymous = factory.CreateJsonClient();

        foreach (var mediaId in new[] { media.Id, Guid.NewGuid() })
        {
            using (var content = await anonymous.GetContentAsync(mediaId))
                await content.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
            using (var thumbnail = await anonymous.GetThumbnailAsync(mediaId))
                await thumbnail.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
            using (var metadata = await anonymous.GetMediaResponseAsync(mediaId))
                await metadata.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        }
    }

    [Fact]
    public async Task Signature_is_bound_to_one_media_id()
    {
        var owner = await factory.RegisterUserAsync();
        var first = await owner.UploadPhotoAsync("first.png");
        var second = await owner.UploadPhotoAsync("second.png");
        var anonymous = factory.CreateJsonClient();

        using (var content = await anonymous.GetContentAsync(second.Id, owner.Sign(first.Id)))
            await content.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        using (var thumbnail = await anonymous.GetThumbnailAsync(second.Id, owner.Sign(first.Id)))
            await thumbnail.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        using (var own = await anonymous.GetContentAsync(second.Id, owner.Sign(second.Id)))
            await own.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tampered_forged_or_malformed_signatures_are_rejected()
    {
        var owner = await factory.RegisterUserAsync();
        var other = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync();
        var anonymous = factory.CreateJsonClient();
        var valid = owner.Sign(media.Id);
        var signaturePart = valid[33..];

        string[] invalid =
        [
            valid[..^1] + (valid[^1] == 'A' ? 'B' : 'A'),                                // one character changed
            valid[..^1],                                                                  // truncated
            valid + "A",                                                                  // too long
            $"{Guid.NewGuid():N}.{signaturePart}",                                        // unknown session
            $"{other.Auth.SessionId:N}.{signaturePart}",                                  // someone else's session
            MediaSignature.Create(other.Auth.MediaKey, owner.Auth.SessionId, media.Id),  // owner's session, wrong key
            MediaSignature.Create(MediaSignature.EncodeKey(MediaSignature.NewKey()), owner.Auth.SessionId, media.Id),
            "garbage",
            owner.Auth.MediaKey,
            owner.Auth.AccessToken,
        ];

        foreach (var token in invalid)
        {
            using var response = await anonymous.GetContentAsync(media.Id, token);
            await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        }
    }

    [Fact]
    public async Task Valid_signature_still_goes_through_the_access_rules()
    {
        var owner = await factory.RegisterUserAsync();
        var member = await factory.RegisterUserAsync();
        var outsider = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync();
        var chatId = await owner.OpenPrivateChatAsync(member.Id);
        await owner.SendMediaAsync(chatId, media.Id);
        var anonymous = factory.CreateJsonClient();

        using (var allowed = await anonymous.GetContentAsync(media.Id, member.Sign(media.Id)))
            await allowed.ShouldHaveStatusAsync(HttpStatusCode.OK);
        using (var allowedThumbnail = await anonymous.GetThumbnailAsync(media.Id, member.Sign(media.Id)))
            await allowedThumbnail.ShouldHaveStatusAsync(HttpStatusCode.OK);
        using (var denied = await anonymous.GetContentAsync(media.Id, outsider.Sign(media.Id)))
            await denied.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        using (var deniedThumbnail = await anonymous.GetThumbnailAsync(media.Id, outsider.Sign(media.Id)))
            await deniedThumbnail.ShouldBeProblemAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
    }

    [Fact]
    public async Task Signatures_and_tokens_die_when_the_session_signs_out()
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync();
        var anonymous = factory.CreateJsonClient();
        var token = owner.Sign(media.Id);
        using (var before = await anonymous.GetContentAsync(media.Id, token))
            await before.ShouldHaveStatusAsync(HttpStatusCode.OK);

        using (var logout = await owner.Client.PostAsync(ApiRoutes.Auth.Logout, content: null, Ct))
            await logout.ShouldHaveStatusAsync(HttpStatusCode.NoContent);

        // The session's media key was cached by the request above: revocation must evict it.
        using (var after = await anonymous.GetContentAsync(media.Id, token))
            await after.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        using (var thumbnail = await anonymous.GetThumbnailAsync(media.Id, token))
            await thumbnail.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        using (var bearer = await owner.Client.GetContentAsync(media.Id))
            await bearer.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Terminating_a_device_kills_its_signatures_but_not_those_of_other_devices()
    {
        var phone = await factory.RegisterUserAsync();
        var laptop = await factory.SignInAgainAsync(phone);
        var media = await phone.UploadPhotoAsync();
        var anonymous = factory.CreateJsonClient();
        using (var before = await anonymous.GetContentAsync(media.Id, phone.Sign(media.Id)))
            await before.ShouldHaveStatusAsync(HttpStatusCode.OK);

        using (var terminate = await laptop.Client.DeleteAsync(ApiRoutes.Sessions.ByIdPath(phone.Auth.SessionId), Ct))
            await terminate.ShouldHaveStatusAsync(HttpStatusCode.NoContent);

        using (var revoked = await anonymous.GetContentAsync(media.Id, phone.Sign(media.Id)))
            await revoked.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        using (var stillValid = await anonymous.GetContentAsync(media.Id, laptop.Sign(media.Id)))
            await stillValid.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Expired_access_token_does_not_spoil_a_valid_signature()
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync();
        // A client whose access token ran out while the app was in the background still loads signed media.
        var expired = factory.Services.GetRequiredService<IAccessTokenIssuer>()
            .Issue(owner.Id, owner.Auth.User.Username, owner.Auth.SessionId, DateTimeOffset.UtcNow.AddHours(-2));
        expired.ExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow);
        var client = factory.CreateJsonClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired.Token);

        using (var signed = await client.GetContentAsync(media.Id, owner.Sign(media.Id)))
            await signed.ShouldHaveStatusAsync(HttpStatusCode.OK);
        using (var unsigned = await client.GetContentAsync(media.Id))
            await unsigned.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        var garbageClient = factory.CreateJsonClient();
        garbageClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        using (var garbage = await garbageClient.GetThumbnailAsync(media.Id, owner.Sign(media.Id)))
            await garbage.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Each_session_signs_with_its_own_key()
    {
        var phone = await factory.RegisterUserAsync();
        var laptop = await factory.SignInAgainAsync(phone);
        var media = await phone.UploadPhotoAsync();

        laptop.Auth.MediaKey.ShouldNotBe(phone.Auth.MediaKey);
        laptop.Sign(media.Id).ShouldNotBe(phone.Sign(media.Id));
        using var response = await factory.CreateJsonClient().GetContentAsync(media.Id, laptop.Sign(media.Id));
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Signature_does_not_authorize_the_metadata_endpoint()
    {
        var owner = await factory.RegisterUserAsync();
        var media = await owner.UploadPhotoAsync();

        using var response = await factory.CreateJsonClient().GetAsync($"{ApiRoutes.Media.ByIdPath(media.Id)}?t={Uri.EscapeDataString(owner.Sign(media.Id))}", Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
    }
}
