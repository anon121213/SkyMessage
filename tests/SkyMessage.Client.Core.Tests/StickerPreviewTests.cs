using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Client.UI.Features.Stickers;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>Chat list and notification previews of stickers and GIFs, and what the chat treats as a sticker.</summary>
public sealed class StickerPreviewTests
{
    private static MessagePreviewFormatter Formatter(string language)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var localizer = new Localizer(services);
        localizer.SetLanguage(language);
        return new MessagePreviewFormatter(localizer, new FormatService(localizer, services), services);
    }

    private static MessageDto Sticker(string emoji) => new()
    {
        Type = MessageType.Photo,
        Attachments =
        [
            new MediaDto
            {
                Kind = MediaKind.Photo, Status = MediaStatus.Ready, ContentType = "image/webp",
                Sticker = new StickerInfoDto { PackId = Guid.NewGuid(), StickerId = Guid.NewGuid(), Emoji = emoji },
            },
        ],
    };

    private static MessageDto Gif(string? caption = null) => new()
    {
        Type = MessageType.Video,
        Text = caption,
        Attachments = [new MediaDto { Kind = MediaKind.Video, Status = MediaStatus.Ready, ContentType = "video/mp4", IsAnimation = true }],
    };

    [Fact]
    public void A_sticker_is_previewed_with_its_emoji_and_a_gif_as_gif()
    {
        var ru = Formatter("ru");
        ru.FormatPreview(Sticker("😺")).ShouldBe("Стикер 😺");
        ru.FormatPreview(Sticker("")).ShouldBe("Стикер");
        ru.FormatPreview(Gif()).ShouldBe("GIF");
        ru.FormatPreview(Gif("Танцуем!")).ShouldBe("GIF Танцуем!");
        Formatter("en").FormatPreview(Sticker("❤️")).ShouldBe("Sticker ❤️");
        // A plain photo stays a photo.
        ru.FormatPreview(new MessageDto { Type = MessageType.Photo, Attachments = [new MediaDto { Kind = MediaKind.Photo }] })
            .ShouldContain("Фото");
    }

    [Fact]
    public void Only_a_single_photo_with_sticker_attributes_is_a_sticker()
    {
        StickerUi.IsSticker(Sticker("😺")).ShouldBeTrue();
        StickerUi.IsSticker(Gif()).ShouldBeFalse();
        StickerUi.IsSticker(new MessageDto { Type = MessageType.Photo, Attachments = [new MediaDto { Kind = MediaKind.Photo }] }).ShouldBeFalse();
        StickerUi.GifOf(Gif())!.IsAnimation.ShouldBeTrue();
        var sticker = StickerUi.AsSticker(Sticker("😺").Attachments[0]).ShouldNotBeNull();
        sticker.Emoji.ShouldBe(["😺"]);
    }
}
