using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>Column lengths of <see cref="Session"/> (see the EF configuration).</summary>
internal static class SessionFieldLimits
{
    public const int AppVersion = 32;
    public const int OsVersion = 64;
    public const int IpAddress = 64;
    public const int PushToken = 1024;
}

internal static class IdentityMapping
{
    public static PrivacySettingsDto ToDto(this UserPrivacy p) => new()
    {
        LastSeen = p.LastSeen,
        ProfilePhoto = p.ProfilePhoto,
        PhoneNumber = p.PhoneNumber,
        FindByPhone = p.FindByPhone,
        Messages = p.Messages,
        Calls = p.Calls,
        GroupInvites = p.GroupInvites,
    };

    /// <returns>True when anything changed.</returns>
    public static bool CopyFrom(this UserPrivacy p, PrivacySettingsDto dto)
    {
        var changed = p.LastSeen != dto.LastSeen || p.ProfilePhoto != dto.ProfilePhoto || p.PhoneNumber != dto.PhoneNumber
                      || p.FindByPhone != dto.FindByPhone || p.Messages != dto.Messages || p.Calls != dto.Calls
                      || p.GroupInvites != dto.GroupInvites;
        p.LastSeen = dto.LastSeen;
        p.ProfilePhoto = dto.ProfilePhoto;
        p.PhoneNumber = dto.PhoneNumber;
        p.FindByPhone = dto.FindByPhone;
        p.Messages = dto.Messages;
        p.Calls = dto.Calls;
        p.GroupInvites = dto.GroupInvites;
        return changed;
    }

    public static MeDto ToMeDto(this User u, Guid savedMessagesChatId) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        Bio = u.Bio,
        AvatarMediaId = u.AvatarMediaId,
        PhoneNumber = u.PhoneNumber,
        PhoneSource = u.PhoneNumber is null ? PhoneSource.None : u.PhoneSource,
        Privacy = u.Privacy.ToDto(),
        SavedMessagesChatId = savedMessagesChatId,
        CreatedAt = u.CreatedAt,
    };

    public static string? Clip(string? value, int maxLength)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
