using Shouldly;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Messaging;

public sealed class MessageTypeRulesTests
{
    [Fact]
    public void No_media_is_text() => MessageTypeRules.Infer([]).ShouldBe(MessageType.Text);

    [Theory]
    [InlineData(MediaKind.Photo, MessageType.Photo)]
    [InlineData(MediaKind.Video, MessageType.Video)]
    [InlineData(MediaKind.File, MessageType.File)]
    [InlineData(MediaKind.Audio, MessageType.Audio)]
    [InlineData(MediaKind.Voice, MessageType.Voice)]
    [InlineData(MediaKind.VideoNote, MessageType.VideoNote)]
    public void Single_attachment_maps_to_its_type(MediaKind kind, MessageType expected) =>
        MessageTypeRules.Infer([kind]).ShouldBe(expected);

    [Fact]
    public void Several_photos_and_videos_make_an_album()
    {
        MessageTypeRules.Infer([MediaKind.Photo, MediaKind.Photo]).ShouldBe(MessageType.Album);
        MessageTypeRules.Infer([MediaKind.Video, MediaKind.Photo, MediaKind.Video]).ShouldBe(MessageType.Album);
    }

    [Fact]
    public void Files_and_audio_group_as_files_unless_all_audio()
    {
        MessageTypeRules.Infer([MediaKind.File, MediaKind.File]).ShouldBe(MessageType.File);
        MessageTypeRules.Infer([MediaKind.File, MediaKind.Audio]).ShouldBe(MessageType.File);
        MessageTypeRules.Infer([MediaKind.Audio, MediaKind.Audio]).ShouldBe(MessageType.Audio);
    }

    [Theory]
    [InlineData(new[] { MediaKind.Voice, MediaKind.Voice })]
    [InlineData(new[] { MediaKind.Voice, MediaKind.Photo })]
    [InlineData(new[] { MediaKind.VideoNote, MediaKind.File })]
    [InlineData(new[] { MediaKind.Photo, MediaKind.File })]
    [InlineData(new[] { MediaKind.Video, MediaKind.Audio })]
    public void Invalid_combinations_are_rejected(MediaKind[] kinds) => MessageTypeRules.Infer(kinds).ShouldBeNull();

    [Fact]
    public void More_than_the_attachment_limit_is_rejected()
    {
        var kinds = Enumerable.Repeat(MediaKind.Photo, Limits.MaxAttachmentsPerMessage + 1).ToArray();
        MessageTypeRules.Infer(kinds).ShouldBeNull();
        MessageTypeRules.Infer(kinds[..Limits.MaxAttachmentsPerMessage]).ShouldBe(MessageType.Album);
    }

    [Fact]
    public void Unknown_kind_is_rejected() => MessageTypeRules.Infer([(MediaKind)99]).ShouldBeNull();

    [Theory]
    [InlineData(MessageType.System, false)]
    [InlineData(MessageType.Call, false)]
    [InlineData(MessageType.Text, true)]
    [InlineData(MessageType.Album, true)]
    public void Service_messages_are_not_user_content(MessageType type, bool expected) =>
        MessageTypeRules.IsUserContent(type).ShouldBe(expected);
}
