using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>
/// The caller's own profile: name, bio, username, avatar (and its history), phone, privacy, birthday, profile color,
/// cover and links.
/// </summary>
public sealed class ProfileService(
    IAppDbContext db,
    SavedChatProvider savedChats,
    UserChangeNotifier userChanges,
    IClock clock)
{
    public async Task<MeDto> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
                   ?? throw new NotFoundException(ErrorCodes.UserNotFound);
        return await ToMeAsync(user, ct);
    }

    public async Task<MeDto> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        var changed = false;

        if (request.DisplayName is not null)
        {
            var name = request.DisplayName.Trim();
            if (name.Length is 0 or > Limits.DisplayNameMaxLength)
                throw new BadRequestException(ErrorCodes.Validation);
            if (name != user.DisplayName)
            {
                user.DisplayName = name;
                changed = true;
            }
        }

        if (request.Bio is not null)
        {
            var bio = request.Bio.Trim();
            if (bio.Length > Limits.BioMaxLength)
                throw new BadRequestException(ErrorCodes.Validation);
            var value = bio.Length == 0 ? null : bio;
            if (value != user.Bio)
            {
                user.Bio = value;
                changed = true;
            }
        }

        string? newUsername = null;
        if (request.Username is not null)
        {
            var username = UsernameRules.Clean(request.Username);
            if (username != user.Username)
            {
                if (UsernameRules.Validate(username) is { } error)
                    throw new BadRequestException(error);
                var normalized = User.Normalize(username);
                if (normalized != user.UsernameNormalized && await db.Users.AnyAsync(u => u.UsernameNormalized == normalized, ct))
                    throw new ConflictException(ErrorCodes.UsernameTaken);
                user.Username = username;
                user.UsernameNormalized = normalized;
                newUsername = normalized;
                changed = true;
            }
        }

        if (request.ProfileColor is not null)
        {
            var color = request.ProfileColor.Trim();
            if (color.Length > 0 && !ProfileStyle.IsColor(color))
                throw new BadRequestException(ErrorCodes.Validation);
            var value = color.Length == 0 ? null : color;
            if (value != user.ProfileColor)
            {
                user.ProfileColor = value;
                changed = true;
            }
        }

        if (request.Links is not null)
        {
            var links = CleanLinks(request.Links);
            if (!links.Select(l => (l.Url, l.Title)).SequenceEqual((user.Links ?? []).Select(l => (l.Url, l.Title))))
            {
                user.Links = links;
                changed = true;
            }
        }

        if (changed)
        {
            user.UpdatedAt = clock.UtcNow;
            await SaveAsync(async () => newUsername is not null
                && await db.Users.AnyAsync(u => u.UsernameNormalized == newUsername && u.Id != userId, ct)
                    ? ErrorCodes.UsernameTaken
                    : null, ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    /// <summary>
    /// The avatar must be the caller's own, processed photo (a new upload or one from the history: it moves to the
    /// front of the history).
    /// </summary>
    public async Task<MeDto> SetAvatarAsync(Guid userId, Guid mediaId, CancellationToken ct)
    {
        await RequireOwnReadyPhotoAsync(userId, mediaId, ct);

        var user = await LoadAsync(userId, ct);
        var now = clock.UtcNow;
        var changed = user.AvatarMediaId != mediaId;
        if (changed)
        {
            user.AvatarMediaId = mediaId;
            user.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }
        if (changed || !await db.OfUser(userId).AnyAsync(a => a.MediaId == mediaId, ct))
        {
            await AvatarHistory.RecordForUserAsync(db, userId, mediaId, now, ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    /// <summary>No photo from now on; the removed one leaves the history too (older photos stay there).</summary>
    public async Task<MeDto> RemoveAvatarAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        if (user.AvatarMediaId is { } current)
        {
            user.AvatarMediaId = null;
            user.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await db.OfUser(userId).Where(a => a.MediaId == current).ExecuteDeleteAsync(ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    /// <summary>
    /// Drops a photo from my avatar history. When it is the current avatar, the newest remaining photo takes over
    /// (no photo when none is left). Unknown photos → 404.
    /// </summary>
    public async Task<MeDto> DeleteAvatarAsync(Guid userId, Guid mediaId, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        var removed = await db.OfUser(userId).Where(a => a.MediaId == mediaId).ExecuteDeleteAsync(ct);
        if (removed == 0 && user.AvatarMediaId != mediaId)
            throw new NotFoundException(ErrorCodes.MediaNotFound);

        if (user.AvatarMediaId == mediaId)
        {
            user.AvatarMediaId = await AvatarHistory.NewestAsync(db.OfUser(userId), mediaId, ct);
            user.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await userChanges.NotifyAsync(userId);
        return await ToMeAsync(user, ct);
    }

    /// <summary>Day and month, the year is optional (<see cref="Birthdays.IsValid"/>).</summary>
    public async Task<MeDto> SetBirthdayAsync(Guid userId, SetBirthdayRequest request, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        if (!Birthdays.IsValid(request.Day, request.Month, request.Year, today))
            throw new BadRequestException(ErrorCodes.Validation);

        var user = await LoadAsync(userId, ct);
        if (user.BirthDay != request.Day || user.BirthMonth != request.Month || user.BirthYear != request.Year)
        {
            user.BirthDay = request.Day;
            user.BirthMonth = request.Month;
            user.BirthYear = request.Year;
            user.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    public async Task<MeDto> RemoveBirthdayAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        if (user.BirthDay is not null || user.BirthMonth is not null || user.BirthYear is not null)
        {
            user.BirthDay = null;
            user.BirthMonth = null;
            user.BirthYear = null;
            user.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    /// <summary>A gradient key, or my own processed photo (which replaces any gradient).</summary>
    public async Task<MeDto> SetCoverAsync(Guid userId, SetCoverRequest request, CancellationToken ct)
    {
        var gradient = request.Gradient?.Trim();
        if (request.MediaId is { } mediaId)
        {
            if (!string.IsNullOrEmpty(gradient)) throw new BadRequestException(ErrorCodes.Validation);
            await RequireOwnReadyPhotoAsync(userId, mediaId, ct);
        }
        else if (!ProfileStyle.IsCoverGradient(gradient))
        {
            throw new BadRequestException(ErrorCodes.Validation);
        }

        var user = await LoadAsync(userId, ct);
        var newGradient = request.MediaId is null ? gradient : null;
        if (user.CoverMediaId != request.MediaId || user.CoverGradient != newGradient)
        {
            user.CoverMediaId = request.MediaId;
            user.CoverGradient = newGradient;
            user.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    public async Task<MeDto> RemoveCoverAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        if (user.CoverMediaId is not null || user.CoverGradient is not null)
        {
            user.CoverMediaId = null;
            user.CoverGradient = null;
            user.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    /// <summary>Links a phone number (not SMS-verified). Region: request ?? own number's region ?? RU.</summary>
    public async Task<MeDto> SetPhoneAsync(Guid userId, SetPhoneRequest request, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        var region = PhoneNormalizer.ResolveRegion(request.DefaultRegion, PhoneNormalizer.RegionOf(user.PhoneNumber));
        var phone = PhoneNormalizer.Normalize(request.PhoneNumber, region)
                    ?? throw new BadRequestException(ErrorCodes.PhoneInvalid);
        if (phone != user.PhoneNumber && await db.Users.AnyAsync(u => u.PhoneNumber == phone && u.Id != userId, ct))
            throw new ConflictException(ErrorCodes.PhoneTaken);

        var now = clock.UtcNow;
        var source = request.Source == PhoneSource.Sim ? PhoneSource.Sim : PhoneSource.Manual;
        var changed = false;
        if (user.PhoneNumber != phone)
        {
            user.PhoneNumber = phone;
            user.PhoneLinkedAt = now;
            changed = true;
        }
        if (user.PhoneSource != source)
        {
            user.PhoneSource = source;
            changed = true;
        }
        if (request.FindByPhone is { } findByPhone && user.Privacy.FindByPhone != findByPhone)
        {
            user.Privacy.FindByPhone = findByPhone;
            changed = true;
        }

        if (changed)
        {
            user.UpdatedAt = now;
            await SaveAsync(async () => await db.Users.AnyAsync(u => u.PhoneNumber == phone && u.Id != userId, ct)
                ? ErrorCodes.PhoneTaken
                : null, ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    public async Task<MeDto> RemovePhoneAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        if (user.PhoneNumber is not null || user.PhoneSource != PhoneSource.None)
        {
            user.PhoneNumber = null;
            user.PhoneSource = PhoneSource.None;
            user.PhoneLinkedAt = null;
            user.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    public async Task<PrivacySettingsDto> GetPrivacyAsync(Guid userId, CancellationToken ct)
    {
        var privacy = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => u.Privacy)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(ErrorCodes.UserNotFound);
        return privacy.ToDto();
    }

    public async Task<PrivacySettingsDto> SetPrivacyAsync(Guid userId, PrivacySettingsDto settings, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        if (user.Privacy.CopyFrom(settings))
        {
            user.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await userChanges.NotifyAsync(userId);
        }
        return user.Privacy.ToDto();
    }

    private async Task RequireOwnReadyPhotoAsync(Guid userId, Guid mediaId, CancellationToken ct)
    {
        var media = await db.Media.AsNoTracking()
            .Where(m => m.Id == mediaId && m.OwnerId == userId)
            .Select(m => new { m.Kind, m.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(ErrorCodes.MediaNotFound);
        if (media.Kind != MediaKind.Photo)
            throw new BadRequestException(ErrorCodes.MediaUnsupported);
        if (media.Status != MediaStatus.Ready)
            throw new BadRequestException(ErrorCodes.MediaNotReady);
    }

    /// <summary>Normalized links (<see cref="ProfileStyle.NormalizeLinkUrl"/>), duplicates dropped; any invalid one → 400.</summary>
    private static List<ProfileLink> CleanLinks(IReadOnlyList<ProfileLinkDto> links)
    {
        if (links.Count > Limits.ProfileLinksMax) throw new BadRequestException(ErrorCodes.Validation);
        var result = new List<ProfileLink>(links.Count);
        foreach (var link in links)
        {
            var url = ProfileStyle.NormalizeLinkUrl(link?.Url) ?? throw new BadRequestException(ErrorCodes.Validation);
            if (result.Any(l => string.Equals(l.Url, url, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new ProfileLink { Url = url, Title = ProfileStyle.NormalizeLinkTitle(link!.Title) });
        }
        return result;
    }

    private async Task<User> LoadAsync(Guid userId, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
        ?? throw new NotFoundException(ErrorCodes.UserNotFound);

    private async Task<MeDto> ToMeAsync(User user, CancellationToken ct) =>
        user.ToMeDto(await savedChats.GetIdAsync(user.Id, ct));

    /// <summary>Saves; a unique-index race is reported with the code <paramref name="conflictCode"/> finds.</summary>
    private async Task SaveAsync(Func<Task<string?>> conflictCode, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await conflictCode() is { } code)
                throw new ConflictException(code);
            throw;
        }
    }
}
