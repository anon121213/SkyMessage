using FluentValidation;
using SkyMessage.Contracts;

namespace SkyMessage.Application.MediaFiles;

/// <summary>
/// Shape checks only. Size against <c>Storage:MaxUploadBytes</c> (<see cref="ErrorCodes.MediaTooLarge"/>) and the
/// content type per kind (<see cref="ErrorCodes.MediaUnsupported"/>) are checked by the upload service so clients
/// get those specific codes.
/// </summary>
internal sealed class CreateUploadRequestValidator : AbstractValidator<CreateUploadRequest>
{
    public CreateUploadRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.FileName).NotNull().MaximumLength(MediaUploadLimits.MaxClientFileNameLength);
        RuleFor(x => x.ContentType).NotNull().MaximumLength(MediaUploadLimits.MaxClientContentTypeLength);
        RuleFor(x => x.SizeBytes).GreaterThan(0);
        RuleFor(x => x.Sha256!)
            .Matches("^[0-9a-fA-F]{64}$").WithMessage("Sha256 must be 64 hexadecimal characters.")
            .When(x => x.Sha256 is not null);
        RuleFor(x => x.Width).InclusiveBetween(1, MediaUploadLimits.MaxDimension).When(x => x.Width is not null);
        RuleFor(x => x.Height).InclusiveBetween(1, MediaUploadLimits.MaxDimension).When(x => x.Height is not null);
        RuleFor(x => x.DurationMs).InclusiveBetween(0, MediaUploadLimits.MaxDurationMs).When(x => x.DurationMs is not null);
        RuleFor(x => x.Waveform!)
            .Must(w => w.Length <= MediaUploadLimits.MaxClientWaveformLength)
            .WithMessage($"Waveform must have at most {MediaUploadLimits.MaxClientWaveformLength} values.")
            .When(x => x.Waveform is not null);
    }
}
