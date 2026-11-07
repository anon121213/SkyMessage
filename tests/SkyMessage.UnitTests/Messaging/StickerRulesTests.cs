using System.Text.Json;
using Shouldly;
using SkyMessage.Contracts;
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
    /// properties a type does not have, so such a message is a plain photo or video for them, never an error.
    /// </summary>
    [Fact]
    public void Apps_before_stickers_read_sticker_and_gif_media_as_plain_photo_and_video()
    {
        var sticker = new MessageDto
        {
            Id = Guid.NewGuid(),
            Type = MessageType.Photo,
            Attachments =
            [
                new MediaDto
                {
                    Id = Guid.NewGuid(), Kind = MediaKind.Photo, Status = MediaStatus.Ready, ContentType = "image/webp", Width = 512, Height = 512,
                    HasThumbnail = true, Sticker = new StickerInfoDto { PackId = Guid.NewGuid(), StickerId = Guid.NewGuid(), Emoji = "😀" },
                },
                new MediaDto { Id = Guid.NewGuid(), Kind = MediaKind.Video, Status = MediaStatus.Ready, ContentType = "video/mp4", IsAnimation = true },
            ],
        };

        var json = JsonSerializer.Serialize(sticker, SkyJson.Options);
        json.ShouldContain("\"sticker\":{");
        json.ShouldContain("\"isAnimation\":true");

        var old = JsonSerializer.Deserialize<OldMessage>(json, SkyJson.Options).ShouldNotBeNull();
        old.Type.ShouldBe(MessageType.Photo);
        old.Attachments[0].Kind.ShouldBe(MediaKind.Photo);
        old.Attachments[0].ContentType.ShouldBe("image/webp");
        old.Attachments[1].Kind.ShouldBe(MediaKind.Video);

        // And a state event they never subscribed to is just a payload they never read.
        JsonSerializer.Deserialize<StickerStateDto>(JsonSerializer.Serialize(new StickerStateDto { Revision = 3 }, SkyJson.Options), SkyJson.Options)!
            .Revision.ShouldBe(3);
    }

    /// <summary>The message and media shapes of 0.2.61 (before stickers and GIFs).</summary>
    private sealed record OldMessage
    {
        public Guid Id { get; init; }
        public MessageType Type { get; init; }
        public IReadOnlyList<OldMedia> Attachments { get; init; } = [];
    }

    private sealed record OldMedia
    {
        public Guid Id { get; init; }
        public MediaKind Kind { get; init; }
        public MediaStatus Status { get; init; }
        public string ContentType { get; init; } = "";
        public int? Width { get; init; }
        public bool HasThumbnail { get; init; }
    }
}
