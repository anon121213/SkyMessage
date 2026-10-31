using FluentValidation;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Validation;

// Request validators run automatically for controller arguments (ValidationFilter → 400 validation_failed).
// Business errors with their own codes (message_empty, media_not_ready, ...) are raised by the services.

internal sealed class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator()
    {
        RuleFor(x => x.ClientMessageId).NotEqual(Guid.Empty).WithMessage("ClientMessageId is required.");
        RuleFor(x => x).Custom((request, context) => AddContentErrors(request.Text, request.Entities, context));
        RuleFor(x => x.MediaIds)
            .Must(ids => ids is null || ids.Count <= Limits.MaxAttachmentsPerMessage)
            .WithMessage($"At most {Limits.MaxAttachmentsPerMessage} attachments are allowed.")
            .Must(ids => ids is null || (ids.Distinct().Count() == ids.Count && !ids.Contains(Guid.Empty)))
            .WithMessage("Media ids must be distinct and not empty.");
        RuleFor(x => x.ReplyToMessageId).NotEqual(Guid.Empty).When(x => x.ReplyToMessageId is not null);
    }

    internal static void AddContentErrors<T>(string? text, IReadOnlyList<MessageEntityDto>? entities, ValidationContext<T> context)
    {
        foreach (var error in MessageContentRules.TextErrors(text))
            context.AddFailure("text", error);
        foreach (var error in MessageContentRules.EntityErrors(text, entities))
            context.AddFailure("entities", error);
    }
}

internal sealed class EditMessageRequestValidator : AbstractValidator<EditMessageRequest>
{
    public EditMessageRequestValidator()
    {
        RuleFor(x => x).Custom((request, context) =>
            SendMessageRequestValidator.AddContentErrors(request.Text, request.Entities, context));
    }
}

internal sealed class ForwardMessagesRequestValidator : AbstractValidator<ForwardMessagesRequest>
{
    public ForwardMessagesRequestValidator()
    {
        RuleFor(x => x.FromChatId).NotEqual(Guid.Empty);
        RuleFor(x => x.MessageIds)
            .NotNull()
            .Must(ids => ids.Count is > 0 and <= Limits.MaxMessagesPerBatch)
            .WithMessage($"Forward 1 to {Limits.MaxMessagesPerBatch} messages.")
            .Must(ids => ids.Distinct().Count() == ids.Count && !ids.Contains(Guid.Empty))
            .WithMessage("Message ids must be distinct and not empty.");
        RuleFor(x => x.ClientMessageIds)
            .NotNull()
            .Must((request, ids) => ids.Count == (request.MessageIds?.Count ?? 0))
            .WithMessage("ClientMessageIds must be parallel to MessageIds.")
            .Must(ids => ids.Distinct().Count() == ids.Count && !ids.Contains(Guid.Empty))
            .WithMessage("ClientMessageIds must be distinct and not empty.");
    }
}

internal sealed class DeleteMessagesRequestValidator : AbstractValidator<DeleteMessagesRequest>
{
    public DeleteMessagesRequestValidator()
    {
        RuleFor(x => x.MessageIds)
            .NotNull()
            .Must(ids => ids.Count is > 0 and <= Limits.MaxMessagesPerBatch)
            .WithMessage($"Delete 1 to {Limits.MaxMessagesPerBatch} messages.")
            .Must(ids => !ids.Contains(Guid.Empty))
            .WithMessage("Message ids must not be empty.");
    }
}

internal sealed class SetReactionRequestValidator : AbstractValidator<SetReactionRequest>
{
    public SetReactionRequestValidator()
    {
        RuleFor(x => x.Emoji)
            .Must(emoji => ReactionRules.IsValid(emoji?.Trim()))
            .WithMessage($"A reaction is one emoji of at most {Limits.ReactionMaxLength} characters.");
    }
}

internal sealed class CreatePrivateChatRequestValidator : AbstractValidator<CreatePrivateChatRequest>
{
    public CreatePrivateChatRequestValidator()
    {
        RuleFor(x => x.UserId).NotEqual(Guid.Empty);
    }
}

internal sealed class CreateGroupRequestValidator : AbstractValidator<CreateGroupRequest>
{
    public CreateGroupRequestValidator()
    {
        RuleFor(x => x.Title).Must(BeValidTitle).WithMessage($"Title must be 1 to {Limits.GroupTitleMaxLength} characters.");
        RuleFor(x => x.Description).Must(BeValidDescription)
            .WithMessage($"Description must be at most {Limits.GroupDescriptionMaxLength} characters.");
        RuleFor(x => x.MemberIds)
            .Must(ids => ids is null || ids.Count < Limits.MaxGroupMembers)
            .WithMessage($"A group has at most {Limits.MaxGroupMembers} members.")
            .Must(ids => ids is null || !ids.Contains(Guid.Empty))
            .WithMessage("Member ids must not be empty.");
        RuleFor(x => x.AvatarMediaId).NotEqual(Guid.Empty).When(x => x.AvatarMediaId is not null);
    }

    internal static bool BeValidTitle(string? title)
    {
        var trimmed = title?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= Limits.GroupTitleMaxLength && !MessageContentRules.HasForbiddenChars(trimmed);
    }

    internal static bool BeValidDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return true;
        var trimmed = description.Trim();
        return trimmed.Length <= Limits.GroupDescriptionMaxLength && !MessageContentRules.HasForbiddenChars(trimmed);
    }
}

internal sealed class UpdateChatRequestValidator : AbstractValidator<UpdateChatRequest>
{
    public UpdateChatRequestValidator()
    {
        RuleFor(x => x.Title).Must(CreateGroupRequestValidator.BeValidTitle).When(x => x.Title is not null)
            .WithMessage($"Title must be 1 to {Limits.GroupTitleMaxLength} characters.");
        RuleFor(x => x.Description).Must(CreateGroupRequestValidator.BeValidDescription).When(x => x.Description is not null)
            .WithMessage($"Description must be at most {Limits.GroupDescriptionMaxLength} characters.");
    }
}

internal sealed class SetChatAvatarRequestValidator : AbstractValidator<SetChatAvatarRequest>
{
    public SetChatAvatarRequestValidator()
    {
        RuleFor(x => x.MediaId).NotEqual(Guid.Empty);
    }
}

internal sealed class AddMembersRequestValidator : AbstractValidator<AddMembersRequest>
{
    public AddMembersRequestValidator()
    {
        RuleFor(x => x.UserIds)
            .NotNull()
            .Must(ids => ids.Count is > 0 and <= Limits.MaxGroupMembers)
            .WithMessage($"Add 1 to {Limits.MaxGroupMembers} users.")
            .Must(ids => !ids.Contains(Guid.Empty))
            .WithMessage("User ids must not be empty.");
    }
}

internal sealed class SetMemberRoleRequestValidator : AbstractValidator<SetMemberRoleRequest>
{
    public SetMemberRoleRequestValidator()
    {
        RuleFor(x => x.Role).IsInEnum();
    }
}

internal sealed class UpdateChatSettingsRequestValidator : AbstractValidator<UpdateChatSettingsRequest>
{
    public UpdateChatSettingsRequestValidator()
    {
        RuleFor(x => x.MuteForSeconds)
            .Must(seconds => seconds is null || MuteRules.IsValidDuration(seconds.Value))
            .WithMessage("Use 0 to unmute, -1 to mute forever or a positive number of seconds.");
    }
}

internal sealed class MarkReadRequestValidator : AbstractValidator<MarkReadRequest>
{
    public MarkReadRequestValidator()
    {
        RuleFor(x => x.UpToSeq).GreaterThanOrEqualTo(0);
    }
}
