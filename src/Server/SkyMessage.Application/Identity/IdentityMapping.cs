using SkyMessage.Application.Common.Persistence;
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
        Birthday = p.Birthday,
    };

    /// <returns>True when anything changed.</returns>
    /// <remarks>A missing <see cref="PrivacySettingsDto.Birthday"/> (apps that predate it) keeps the stored level.</remarks>
    public static bool CopyFrom(this UserPrivacy p, PrivacySettingsDto dto)
    {
        var birthday = dto.Birthday ?? p.Birthday;
        var changed = p.LastSeen != dto.LastSeen || p.ProfilePhoto != dto.ProfilePhoto || p.PhoneNumber != dto.PhoneNumber
                      || p.FindByPhone != dto.FindByPhone || p.Messages != dto.Messages || p.Calls != dto.Calls
                      || p.GroupInvites != dto.GroupInvites || p.Birthday != birthday;
        p.Birthday = birthday;
        p.LastSeen = dto.LastSeen;
        p.ProfilePhoto = dto.ProfilePhoto;
        p.PhoneNumber = dto.PhoneNumber;
        p.FindByPhone = dto.FindByPhone;
        p.Messages = dto.Messages;
        p.Calls = dto.Calls;
        p.GroupInvites = dto.GroupInvites;
        return changed;
    }

    /// <param name="earlierAvatarCount">See <see cref="MeDto.EarlierAvatarCount"/> (<see cref="AvatarHistory.CountEarlierAsync"/>).</param>
    public static MeDto ToMeDto(this User u, Guid savedMessagesChatId, int earlierAvatarCount) => new()
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
        Birthday = BirthdayOf(u.BirthDay, u.BirthMonth, u.BirthYear),
        ProfileColor = u.ProfileColor,
        Cover = CoverOf(u.CoverGradient, u.CoverMediaId, photoVisible: true),
        Links = u.Links.ToDtos(),
        EarlierAvatarCount = earlierAvatarCount,
    };

    public static BirthdayDto? BirthdayOf(int? day, int? month, int? year) =>
        day is { } d && month is { } m ? new BirthdayDto { Day = d, Month = m, Year = year } : null;

    /// <summary>A photo cover counts only when the viewer may see it; then the gradient (if any) stands in.</summary>
    public static ProfileCoverDto? CoverOf(string? gradient, Guid? mediaId, bool photoVisible)
    {
        if (mediaId is { } photo && photoVisible) return new ProfileCoverDto { MediaId = photo };
        return gradient is null ? null : new ProfileCoverDto { Gradient = gradient };
    }

    public static IReadOnlyList<ProfileLinkDto> ToDtos(this IEnumerable<ProfileLink>? links) =>
        links?.Select(l => new ProfileLinkDto { Url = l.Url, Title = l.Title }).ToList() ?? [];

    public static string? Clip(string? value, int maxLength)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
