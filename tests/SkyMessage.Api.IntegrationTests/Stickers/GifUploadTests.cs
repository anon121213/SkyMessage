using Microsoft.AspNetCore.Hosting;
using Shouldly;
using SkyMessage.Api.IntegrationTests.Media;
using SkyMessage.Api.IntegrationTests.Messaging;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Stickers;

/// <summary>
/// A GIF becomes a loop only when the upload asks for it (<see cref="CreateUploadRequest.ConvertAnimation"/>, chat
/// attachments of apps that know GIFs) and only within the conversion budget. Apps before GIFs (0.2.61) never ask, so
/// their GIFs stay photos — which profile photos, covers and group photos need.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class GifUploadTests(ApiFactory factory)
{
    [Fact]
    public async Task An_animated_gif_uploaded_without_asking_stays_a_photo_and_can_be_a_profile_cover_or_group_photo()
    {
        var alice = await factory.NewUserAsync();
        var bob = await factory.NewUserAsync();
        var gif = await StickerKit.AnimatedGifAsync();

        async Task<MediaDto> UploadAsync()
        {
            var media = await alice.UploadAsync(MediaKind.Photo, gif, "image/gif", "dance.gif");
            media.Status.ShouldBe(MediaStatus.Ready);
            media.Kind.ShouldBe(MediaKind.Photo);
            media.ContentType.ShouldBe("image/gif");
            media.IsAnimation.ShouldBeFalse();
            return media;
        }

        var avatar = await UploadAsync();
        (await (await alice.PutRawAsync(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = avatar.Id })).ReadAsync<MeDto>())
            .AvatarMediaId.ShouldBe(avatar.Id);
        var cover = await UploadAsync();
        (await (await alice.PutRawAsync(ApiRoutes.Users.MeCover, new SetCoverRequest { MediaId = cover.Id })).ReadAsync<MeDto>())
            .Cover.ShouldNotBeNull().MediaId.ShouldBe(cover.Id);
        var groupPhoto = await UploadAsync();
        var created = await alice.CreateGroupAsync(new CreateGroupRequest { Title = "Гифки", MemberIds = [bob.Id], AvatarMediaId = groupPhoto.Id });
        created.Chat.AvatarMediaId.ShouldBe(groupPhoto.Id);

        // The same GIF attached in a chat by an app that knows GIFs becomes a loop.
        var loop = await alice.UploadAsync(MediaKind.Photo, gif, "image/gif", "dance.gif", convertAnimation: true);
        loop.Kind.ShouldBe(MediaKind.Video);
        loop.IsAnimation.ShouldBeTrue();
    }

    [Fact]
    public async Task A_gif_that_cannot_be_converted_stays_the_gif_it_is()
    {
        // A server without ffmpeg cannot convert anything: the GIF is kept, as before animations existed.
        await using var noTools = factory.WithWebHostBuilder(b => b
            .UseSetting("MediaProcessing:FfmpegPath", "/nonexistent/skymessage/ffmpeg")
            .UseSetting("MediaProcessing:FfprobePath", "/nonexistent/skymessage/ffprobe"));
        var owner = (await factory.RegisterUserAsync()).On(noTools);

        var media = await owner.UploadReadyAsync(MediaKind.Photo, StickerKit.CraftedGif(64, 48, 3), "image/gif", "loop.gif",
            r => r with { ConvertAnimation = true });

        media.Kind.ShouldBe(MediaKind.Photo);
        media.IsAnimation.ShouldBeFalse();
        media.ContentType.ShouldBe("image/gif");
        (media.Width, media.Height).ShouldBe((64, 48));
        media.HasThumbnail.ShouldBeTrue();
    }

    [Theory]
    [InlineData(4100, 16, 2)]
    [InlineData(2100, 2100, 2)]
    [InlineData(1024, 1024, 1500)]
    public async Task A_gif_too_large_to_decode_cheaply_stays_the_gif_it_is(int width, int height, int frames)
    {
        var alice = await factory.NewUserAsync();

        var media = await alice.UploadAsync(MediaKind.Photo, StickerKit.CraftedGif(width, height, frames), "image/gif", "huge.gif", convertAnimation: true);

        media.Status.ShouldBe(MediaStatus.Ready);
        media.Kind.ShouldBe(MediaKind.Photo);
        media.IsAnimation.ShouldBeFalse();
        media.ContentType.ShouldBe("image/gif");
        (media.Width, media.Height).ShouldBe((width, height));
    }
}
