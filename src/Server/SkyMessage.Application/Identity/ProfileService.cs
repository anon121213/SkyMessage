using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>The caller's own profile: name, bio, username, avatar, phone, privacy.</summary>
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

    /// <summary>The avatar must be the caller's own, processed photo.</summary>
    public async Task<MeDto> SetAvatarAsync(Guid userId, Guid mediaId, CancellationToken ct)
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

        var user = await LoadAsync(userId, ct);
        if (user.AvatarMediaId != mediaId)
        {
            user.AvatarMediaId = mediaId;
            user.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await userChanges.NotifyAsync(userId);
        }
        return await ToMeAsync(user, ct);
    }

    public async Task<MeDto> RemoveAvatarAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        if (user.AvatarMediaId is not null)
        {
            user.AvatarMediaId = null;
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
