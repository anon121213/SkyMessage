using FluentValidation;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

// Request shape checks (400 validation_failed). Rules that have their own error code (username format and
// reservation, password strength, phone validity) are enforced by the services so clients get that code.

internal static class IdentityRules
{
    /// <summary>Loose cap for raw usernames; the exact format is checked by <see cref="UsernameRules"/>.</summary>
    public const int RawUsernameMaxLength = 64;
    public const int LoginMaxLength = 128;
    public const int RegionMaxLength = 8;
    public const int InviteCodeMaxLength = 256;
    public const int PushKeyBytes = 32;

    public static bool IsDisplayName(string? name) =>
        name is not null && name.Trim().Length is >= 1 and <= Limits.DisplayNameMaxLength;

    public static bool FitsTrimmed(string? value, int maxLength) => value is null || value.Trim().Length <= maxLength;

    public static bool IsAes256KeyBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        Span<byte> buffer = stackalloc byte[64];
        return Convert.TryFromBase64String(value.Trim(), buffer, out var written) && written == PushKeyBytes;
    }
}

internal sealed class DeviceInfoDtoValidator : AbstractValidator<DeviceInfoDto>
{
    public DeviceInfoDtoValidator()
    {
        RuleFor(x => x.DeviceName).NotNull().MaximumLength(Limits.DeviceNameMaxLength);
        RuleFor(x => x.Platform).IsInEnum();
        RuleFor(x => x.AppVersion).NotNull().MaximumLength(SessionFieldLimits.AppVersion);
        RuleFor(x => x.OsVersion).MaximumLength(SessionFieldLimits.OsVersion);
    }
}

internal sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Username).NotNull().MaximumLength(IdentityRules.RawUsernameMaxLength);
        RuleFor(x => x.Password).NotNull().MaximumLength(Limits.PasswordMaxLength);
        RuleFor(x => x.DisplayName).Must(IdentityRules.IsDisplayName)
            .WithMessage($"Display name must be 1 to {Limits.DisplayNameMaxLength} characters.");
        RuleFor(x => x.Device).NotNull().SetValidator(new DeviceInfoDtoValidator());
        RuleFor(x => x.InviteCode).MaximumLength(IdentityRules.InviteCodeMaxLength);
    }
}

internal sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Login).NotEmpty().MaximumLength(IdentityRules.LoginMaxLength);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(Limits.PasswordMaxLength);
        RuleFor(x => x.Device).NotNull().SetValidator(new DeviceInfoDtoValidator());
        RuleFor(x => x.DefaultRegion).MaximumLength(IdentityRules.RegionMaxLength);
    }
}

internal sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(RefreshTokens.MaxLength);
    }
}

internal sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(Limits.PasswordMaxLength);
        RuleFor(x => x.NewPassword).NotNull().MaximumLength(Limits.PasswordMaxLength);
    }
}

internal sealed class DeleteAccountRequestValidator : AbstractValidator<DeleteAccountRequest>
{
    public DeleteAccountRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MaximumLength(Limits.PasswordMaxLength);
    }
}

internal sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.DisplayName).Must(IdentityRules.IsDisplayName).When(x => x.DisplayName is not null)
            .WithMessage($"Display name must be 1 to {Limits.DisplayNameMaxLength} characters.");
        RuleFor(x => x.Bio).Must(b => IdentityRules.FitsTrimmed(b, Limits.BioMaxLength))
            .WithMessage($"Bio must be at most {Limits.BioMaxLength} characters.");
        RuleFor(x => x.Username).MaximumLength(IdentityRules.RawUsernameMaxLength);
    }
}

internal sealed class SetAvatarRequestValidator : AbstractValidator<SetAvatarRequest>
{
    public SetAvatarRequestValidator()
    {
        RuleFor(x => x.MediaId).NotEmpty();
    }
}

internal sealed class SetPhoneRequestValidator : AbstractValidator<SetPhoneRequest>
{
    public SetPhoneRequestValidator()
    {
        RuleFor(x => x.PhoneNumber).NotEmpty().MaximumLength(PhoneNormalizer.MaxInputLength);
        RuleFor(x => x.Source).Must(s => s is PhoneSource.Sim or PhoneSource.Manual)
            .WithMessage("Source must be Sim or Manual.");
        RuleFor(x => x.FindByPhone).IsInEnum();
        RuleFor(x => x.DefaultRegion).MaximumLength(IdentityRules.RegionMaxLength);
    }
}

internal sealed class PrivacySettingsDtoValidator : AbstractValidator<PrivacySettingsDto>
{
    public PrivacySettingsDtoValidator()
    {
        RuleFor(x => x.LastSeen).IsInEnum();
        RuleFor(x => x.ProfilePhoto).IsInEnum();
        RuleFor(x => x.PhoneNumber).IsInEnum();
        RuleFor(x => x.FindByPhone).IsInEnum();
        RuleFor(x => x.Messages).IsInEnum();
        RuleFor(x => x.Calls).IsInEnum();
        RuleFor(x => x.GroupInvites).IsInEnum();
    }
}

internal sealed class GetUsersRequestValidator : AbstractValidator<GetUsersRequest>
{
    public GetUsersRequestValidator()
    {
        RuleFor(x => x.UserIds).NotNull()
            .Must(ids => ids is null || ids.Count <= Limits.MaxBatchUsers)
            .WithMessage($"At most {Limits.MaxBatchUsers} user ids per request.");
    }
}

internal sealed class FindByPhonesRequestValidator : AbstractValidator<FindByPhonesRequest>
{
    public FindByPhonesRequestValidator()
    {
        RuleFor(x => x.PhoneNumbers).NotNull()
            .Must(numbers => numbers is null || numbers.Count <= Limits.FindByPhonesMax)
            .WithMessage($"At most {Limits.FindByPhonesMax} phone numbers per request.");
        RuleForEach(x => x.PhoneNumbers).NotNull().MaximumLength(PhoneNormalizer.MaxInputLength);
        RuleFor(x => x.DefaultRegion).MaximumLength(IdentityRules.RegionMaxLength);
        RuleFor(x => x.DefaultRegions)
            .Must(regions => regions is null || regions.Count <= 8)
            .WithMessage("At most 8 default regions.");
        RuleForEach(x => x.DefaultRegions).NotNull().MaximumLength(IdentityRules.RegionMaxLength);
    }
}

internal sealed class AddContactRequestValidator : AbstractValidator<AddContactRequest>
{
    public AddContactRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.CustomName).Must(n => IdentityRules.FitsTrimmed(n, Limits.ContactNameMaxLength))
            .WithMessage($"Contact name must be at most {Limits.ContactNameMaxLength} characters.");
    }
}

internal sealed class AddContactsBulkRequestValidator : AbstractValidator<AddContactsBulkRequest>
{
    public AddContactsBulkRequestValidator()
    {
        RuleFor(x => x.Contacts).NotNull()
            .Must(contacts => contacts is null || contacts.Count <= Limits.FindByPhonesMax)
            .WithMessage($"At most {Limits.FindByPhonesMax} contacts per request.");
        RuleForEach(x => x.Contacts).NotNull().SetValidator(new AddContactRequestValidator());
    }
}

internal sealed class UpdateContactRequestValidator : AbstractValidator<UpdateContactRequest>
{
    public UpdateContactRequestValidator()
    {
        RuleFor(x => x.CustomName).Must(n => IdentityRules.FitsTrimmed(n, Limits.ContactNameMaxLength))
            .WithMessage($"Contact name must be at most {Limits.ContactNameMaxLength} characters.");
    }
}

internal sealed class RegisterPushRequestValidator : AbstractValidator<RegisterPushRequest>
{
    public RegisterPushRequestValidator()
    {
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.Token).NotEmpty().When(x => x.Provider != PushProvider.None);
        RuleFor(x => x.Token).MaximumLength(SessionFieldLimits.PushToken);
        RuleFor(x => x.EncryptionKey).Must(IdentityRules.IsAes256KeyBase64)
            .WithMessage("EncryptionKey must be a base64 AES-256 key (32 bytes).");
    }
}
