using System.Text.Json;
using Shouldly;
using SkyMessage.Contracts;
using V0261 = SkyMessage.UnitTests.Compat.V0261;
using Xunit;

namespace SkyMessage.UnitTests.Messaging;

public sealed class StickerRulesTests
{
    [Fact]
    public void Emoji_lists_are_trimmed_deduplicated_and_kept_in_order()
    {
        StickerRules.NormalizeEmoji([" 😀", "❤️", "❤", "👍🏽"]).ShouldBe(["😀", "❤️", "👍🏽"]);
    }

    [Theory]
    [InlineData]
    [InlineData("a")]
    [InlineData("😀😀")]
    [InlineData("")]
    [InlineData("😀", "1")]
    public void Emoji_lists_need_one_to_eight_single_emoji(params string[] emoji)
    {
        StickerRules.NormalizeEmoji(emoji).ShouldBeNull();
    }

    [Fact]
    public void More_than_eight_emoji_are_refused()
    {
        StickerRules.NormalizeEmoji(["😀", "😃", "😄", "😁", "😆", "😅", "😂", "🤣"]).ShouldNotBeNull();
        StickerRules.NormalizeEmoji(["😀", "😃", "😄", "😁", "😆", "😅", "😂", "🤣", "🥲"]).ShouldBeNull();
    }

    [Fact]
    public void Keys_ignore_variation_selectors_and_skin_tones()
    {
        StickerRules.EmojiKey("❤️").ShouldBe(StickerRules.EmojiKey("❤"));
        StickerRules.EmojiKey("👍🏿").ShouldBe("👍");
        StickerRules.EmojiKey("👨‍👩‍👧").ShouldBe("👨‍👩‍👧");
        var sticker = new StickerDto { Emoji = ["😺", "❤"] };
        StickerRules.Matches(sticker, "❤️").ShouldBeTrue();
        StickerRules.Matches(sticker, "😀").ShouldBeFalse();
        StickerRules.Matches(sticker, " ").ShouldBeFalse();
    }

    [Theory]
    [InlineData("Котики", true)]
    [InlineData("  ", false)]
    [InlineData("⠀ㅤ", false)]
    [InlineData("a\u0007b", false)]
    public void Titles_need_something_visible_and_no_control_characters(string title, bool valid)
    {
        StickerRules.IsValidTitle(StickerRules.NormalizeTitle(title)).ShouldBe(valid);
    }

    [Fact]
    public void Titles_are_one_line_of_at_most_64_characters()
    {
        StickerRules.NormalizeTitle(" Наш\r\nкот ").ShouldBe("Наш кот");
        StickerRules.IsValidTitle(new string('я', Limits.StickerPackTitleMaxLength)).ShouldBeTrue();
        StickerRules.IsValidTitle(new string('я', Limits.StickerPackTitleMaxLength + 1)).ShouldBeFalse();
        StickerRules.IsValidTitle(new string('я', 40) + "\u0001").ShouldBeFalse();
    }

    [Fact]
    public void New_slugs_are_ten_unambiguous_characters_and_parse_as_pack_links()
    {
        var slugs = Enumerable.Range(0, 200).Select(_ => StickerRules.NewSlug()).ToList();
        slugs.ShouldAllBe(s => s.Length == StickerRules.SlugLength && StickerRules.IsValidSlug(s));
        slugs.ShouldAllBe(s => !s.Contains('l') && !s.Contains('o') && !s.Contains('0') && !s.Contains('1'));
        slugs.Distinct().Count().ShouldBe(slugs.Count);

        DeepLinks.TryParse(new Uri("https://sky.example/addstickers/" + slugs[0]), out var web).ShouldBeTrue();
        web.ShouldBe(new DeepLinkTarget(DeepLinkKind.StickerPack, slugs[0]));
        DeepLinks.TryParse(new Uri("skymessage://addstickers/abc123def4"), out var app).ShouldBeTrue();
        app!.Kind.ShouldBe(DeepLinkKind.StickerPack);
        DeepLinks.TryParse(new Uri("https://sky.example/addstickers/<script>"), out _).ShouldBeFalse();
        DeepLinks.StickerPackPath("abc123def4").ShouldBe("/addstickers/abc123def4");
    }

    [Fact]
    public void Reordering_moves_only_the_listed_ids_into_the_places_they_held()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();
        StickerRules.Arrange([a, b, c, d], [c, a]).ShouldBe([c, b, a, d]);
        StickerRules.Arrange([a, b], [Guid.NewGuid(), b, b, a]).ShouldBe([b, a]);
    }

    /// <summary>
    /// Apps released before stickers (0.2.61) read MediaDto without Sticker and IsAnimation: System.Text.Json skips
    /// properties a type does not have, so such a message is a plain photo or video for them, never an error. Read with
    /// a frozen copy of the released message shapes and their JSON settings (Compat/V0261Contracts.cs): every member and
    /// enum as 0.2.61 has them, so a new enum value sent to it would fail here as it would there.
    /// </summary>
    [Fact]
    public void Apps_before_stickers_read_sticker_and_gif_messages_as_plain_photos_and_videos()
    {
        var chatId = Guid.NewGuid();
        var sticker = new MediaDto
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Photo, Status = MediaStatus.Ready, FileName = "sticker.webp", ContentType = "image/webp",
            SizeBytes = 21000, Width = 512, Height = 512, HasThumbnail = true, CreatedAt = DateTimeOffset.UtcNow,
            Sticker = new StickerInfoDto { PackId = Guid.NewGuid(), StickerId = Guid.NewGuid(), Emoji = "😀" },
        };
        var gif = new MediaDto
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Video, Status = MediaStatus.Ready, FileName = "dance.mp4", ContentType = "video/mp4",
            SizeBytes = 90000, Width = 720, Height = 404, DurationMs = 1500, HasThumbnail = true, MiniThumbnail = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow, IsAnimation = true,
        };
        var photo = new MediaDto { Id = Guid.NewGuid(), Kind = MediaKind.Photo, Status = MediaStatus.Ready, ContentType = "image/jpeg" };
        MessageDto Message(long seq, MessageType type, string? text, params MediaDto[] media) => new()
        {
            Id = Guid.NewGuid(), ChatId = chatId, Seq = seq, SenderId = Guid.NewGuid(), Type = type, Text = text, Attachments = media,
            Reactions = [new ReactionDto { Emoji = "❤️", Count = 1, UserIds = [Guid.NewGuid()] }],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var stickerMessage = Message(1, MessageType.Photo, null, sticker);
        var page = new MessagesPageDto
        {
            Messages =
            [
                stickerMessage,
                Message(2, MessageType.Video, "Танцуем!", gif),
                Message(3, MessageType.Album, null, gif, photo),
                // A reply to a sticker carries its media in the preview.
                Message(4, MessageType.Text, "Ха", []) with
                {
                    ReplyTo = new ReplyPreviewDto { MessageId = stickerMessage.Id, Seq = 1, Type = MessageType.Photo, Media = sticker },
                },
            ],
            SyncCursor = "c",
        };

        // As the server sends them (REST and SignalR use the same settings).
        var json = JsonSerializer.Serialize(page, SkyJson.Options);
        json.ShouldContain("\"sticker\":{");
        json.ShouldContain("\"isAnimation\":true");

        var old = JsonSerializer.Deserialize<V0261.MessagesPageDto>(json, V0261.OldAppJson.Options).ShouldNotBeNull();
        old.Messages.Select(m => m.Type).ShouldBe([V0261.MessageType.Photo, V0261.MessageType.Video, V0261.MessageType.Album, V0261.MessageType.Text]);
        var oldSticker = old.Messages[0].Attachments.ShouldHaveSingleItem();
        (oldSticker.Kind, oldSticker.ContentType, oldSticker.Width).ShouldBe((V0261.MediaKind.Photo, "image/webp", 512));
        var oldGif = old.Messages[1].Attachments.ShouldHaveSingleItem();
        (oldGif.Kind, oldGif.ContentType, oldGif.DurationMs).ShouldBe((V0261.MediaKind.Video, "video/mp4", 1500));
        old.Messages[1].Text.ShouldBe("Танцуем!");
        old.Messages[2].Attachments.Select(a => a.Kind).ShouldBe([V0261.MediaKind.Video, V0261.MediaKind.Photo]);
        old.Messages[3].ReplyTo.ShouldNotBeNull().Media.ShouldNotBeNull().ContentType.ShouldBe("image/webp");

        // A converted GIF's MediaProcessed and GET api/media/{id}.
        var processed = JsonSerializer.Deserialize<V0261.MediaDto>(JsonSerializer.Serialize(gif, SkyJson.Options), V0261.OldAppJson.Options);
        processed.ShouldNotBeNull().Status.ShouldBe(V0261.MediaStatus.Ready);
        // Incremental sync carries the same messages.
        var changes = new MessageChangesDto { Messages = page.Messages, NextCursor = "n" };
        JsonSerializer.Deserialize<V0261.MessageChangesDto>(JsonSerializer.Serialize(changes, SkyJson.Options), V0261.OldAppJson.Options)
            .ShouldNotBeNull().Messages.Count.ShouldBe(4);
    }
}
