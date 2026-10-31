using FluentValidation;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Calls;

internal sealed class StartCallRequestValidator : AbstractValidator<StartCallRequest>
{
    public StartCallRequestValidator()
    {
        RuleFor(x => x.ChatId).NotEmpty();
    }
}
