using Shouldly;
using SkyMessage.Application.Messaging.Validation;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Messaging;

public sealed class MessagingValidatorsTests
{
    [Fact]
    public void Send_requires_a_client_message_id()
    {
        var result = new SendMessageRequestValidator().Validate(new SendMessageRequest { Text = "hi" });
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(SendMessageRequest.ClientMessageId));
    }

    [Fact]
    public void Send_accepts_a_formatted_message()
    {
        var result = new SendMessageRequestValidator().Validate(new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = "see docs",
            Entities = [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 4, Length = 4, Url = "https://example.com/docs" }],
            MediaIds = [Guid.NewGuid()],
        });
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Send_rejects_bad_entities_and_media_lists()
    {
        var media = Guid.NewGuid();
        var result = new SendMessageRequestValidator().Validate(new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = "hi",
            Entities = [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 2, Url = "javascript:void(0)" }],
            MediaIds = [media, media],
        });
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "entities");
        result.Errors.ShouldContain(e => e.PropertyName == nameof(SendMessageRequest.MediaIds));

        var tooMany = new SendMessageRequestValidator().Validate(new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            MediaIds = Enumerable.Range(0, Limits.MaxAttachmentsPerMessage + 1).Select(_ => Guid.NewGuid()).ToList(),
        });
        tooMany.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Send_rejects_nul_characters() =>
        new SendMessageRequestValidator().Validate(new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = "a\0b" })
            .Errors.ShouldContain(e => e.PropertyName == "text");

    [Fact]
    public void Forward_lists_must_be_parallel_and_distinct()
    {
        var validator = new ForwardMessagesRequestValidator();
        validator.Validate(new ForwardMessagesRequest
        {
            FromChatId = Guid.NewGuid(),
            MessageIds = [Guid.NewGuid(), Guid.NewGuid()],
            ClientMessageIds = [Guid.NewGuid(), Guid.NewGuid()],
        }).IsValid.ShouldBeTrue();

        validator.Validate(new ForwardMessagesRequest
        {
            FromChatId = Guid.NewGuid(),
            MessageIds = [Guid.NewGuid(), Guid.NewGuid()],
            ClientMessageIds = [Guid.NewGuid()],
        }).IsValid.ShouldBeFalse();

        var id = Guid.NewGuid();
        validator.Validate(new ForwardMessagesRequest
        {
            FromChatId = Guid.NewGuid(),
            MessageIds = [id, id],
            ClientMessageIds = [Guid.NewGuid(), Guid.NewGuid()],
        }).IsValid.ShouldBeFalse();

        validator.Validate(new ForwardMessagesRequest { FromChatId = Guid.NewGuid() }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Group_title_and_description_rules()
    {
        var validator = new CreateGroupRequestValidator();
        validator.Validate(new CreateGroupRequest { Title = "  Family  " }).IsValid.ShouldBeTrue();
        validator.Validate(new CreateGroupRequest { Title = "   " }).IsValid.ShouldBeFalse();
        validator.Validate(new CreateGroupRequest { Title = new string('t', Limits.GroupTitleMaxLength + 1) }).IsValid.ShouldBeFalse();
        validator.Validate(new CreateGroupRequest { Title = "ok", Description = new string('d', Limits.GroupDescriptionMaxLength + 1) }).IsValid.ShouldBeFalse();
        validator.Validate(new CreateGroupRequest { Title = "ok", MemberIds = [Guid.Empty] }).IsValid.ShouldBeFalse();

        var update = new UpdateChatRequestValidator();
        update.Validate(new UpdateChatRequest()).IsValid.ShouldBeTrue();
        update.Validate(new UpdateChatRequest { Description = "" }).IsValid.ShouldBeTrue();
        update.Validate(new UpdateChatRequest { Title = "" }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Reaction_settings_and_read_rules()
    {
        new SetReactionRequestValidator().Validate(new SetReactionRequest { Emoji = " 👍 " }).IsValid.ShouldBeTrue();
        new SetReactionRequestValidator().Validate(new SetReactionRequest { Emoji = "ok" }).IsValid.ShouldBeFalse();
        new UpdateChatSettingsRequestValidator().Validate(new UpdateChatSettingsRequest { MuteForSeconds = -1 }).IsValid.ShouldBeTrue();
        new UpdateChatSettingsRequestValidator().Validate(new UpdateChatSettingsRequest { MuteForSeconds = -2 }).IsValid.ShouldBeFalse();
        new MarkReadRequestValidator().Validate(new MarkReadRequest { UpToSeq = -1 }).IsValid.ShouldBeFalse();
        new SetMemberRoleRequestValidator().Validate(new SetMemberRoleRequest { Role = (ChatRole)9 }).IsValid.ShouldBeFalse();
        new DeleteMessagesRequestValidator().Validate(new DeleteMessagesRequest()).IsValid.ShouldBeFalse();
        new AddMembersRequestValidator().Validate(new AddMembersRequest { UserIds = [Guid.NewGuid()] }).IsValid.ShouldBeTrue();
    }
}
