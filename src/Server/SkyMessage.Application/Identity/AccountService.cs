using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Account deletion: the user row becomes an anonymous tombstone (messages stay, shown as from a deleted
/// account), every session is revoked, groups are left with the usual ownership hand-over, Saved messages,
/// contacts and own blocks are removed, and so is the user's part of a private chat's content protection (nobody
/// else could ever take it back).
/// </summary>
public sealed class AccountService(
    IAppDbContext db,
    IUserPasswordHasher passwords,
    LoginThrottle throttle,
    SessionRevoker revoker,
    SavedChatProvider savedChats,
    UserChangeNotifier userChanges,
    IRealtimeNotifier notifier,
    IPresenceService presence,
    ISystemMessageService systemMessages,
    IEnumerable<IChatMembershipObserver> membershipObservers,
    IClock clock,
    ILogger<AccountService> logger)
{
    private enum GroupLeave
    {
        NotMember,
        Left,
        GroupEmptied,
    }

    public async Task DeleteAsync(Guid userId, string password, CancellationToken ct)
    {
        var throttleKey = LoginThrottle.PasswordKey(userId);
        if (throttle.IsLockedOut(throttleKey))
            throw new RateLimitedException();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
                   ?? throw new NotFoundException(ErrorCodes.UserNotFound);
        if (passwords.Verify(user, user.PasswordHash, password) == PasswordCheckResult.Failed)
        {
            throttle.RegisterFailure(throttleKey);
            throw new BadRequestException(ErrorCodes.WrongPassword);
        }
        throttle.Reset(throttleKey);

        // Captured before memberships and contacts disappear: all of them must refetch the tombstone.
        var related = await RelatedUsers.GetAsync(db, userId, ct);
        var contactOwners = await db.Contacts.AsNoTracking()
            .Where(c => c.ContactUserId == userId)
            .Select(c => c.OwnerId)
            .ToListAsync(ct);
        var groupIds = await db.ChatMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Chat!.Type == ChatType.Group && !m.Chat.IsDeleted)
            .Select(m => m.ChatId)
            .OrderBy(id => id)
            .ToListAsync(ct);
        var protectedChatIds = (await db.Chats.AsNoTracking()
            .Where(c => c.Type == ChatType.Private && !c.IsDeleted && c.ProtectContentBy.Contains(userId))
            .Select(c => c.Id)
            .ToListAsync(ct)).ToHashSet();

        // CPU-heavy, so outside the transaction: a hash nobody knows the password of.
        var unusableHash = passwords.Hash(user, RefreshTokens.Generate());
        var now = clock.UtcNow;
        var leftGroups = new List<Guid>();
        var emptiedGroups = new List<Guid>();
        var protectionChanged = new List<Guid>();
        List<Guid> revokedSessions;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Ascending ids = consistent lock order with any other multi-chat transaction.
            foreach (var chatId in groupIds.Concat(protectedChatIds).Order())
            {
                if (protectedChatIds.Contains(chatId))
                {
                    if (await TakeBackProtectionAsync(chatId, userId, now, ct)) protectionChanged.Add(chatId);
                    continue;
                }
                switch (await LeaveGroupAsync(chatId, userId, now, ct))
                {
                    case GroupLeave.Left:
                        leftGroups.Add(chatId);
                        break;
                    case GroupLeave.GroupEmptied:
                        emptiedGroups.Add(chatId);
                        break;
                }
            }

            var savedKey = Chat.SavedKeyFor(userId);
            await db.Chats.Where(c => c.PrivateKey == savedKey).ExecuteDeleteAsync(ct);
            await db.Contacts.Where(c => c.OwnerId == userId || c.ContactUserId == userId).ExecuteDeleteAsync(ct);
            await db.Blocks.Where(b => b.BlockerId == userId).ExecuteDeleteAsync(ct);
            revokedSessions = await revoker.MarkRevokedAsync(s => s.UserId == userId, ct);

            Tombstone(user, unusableHash, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        savedChats.Forget(userId);
        await revoker.NotifyRevokedAsync(revokedSessions);
        foreach (var chatId in leftGroups)
            await AnnounceLeaveAsync(chatId, userId);
        foreach (var chatId in leftGroups.Concat(emptiedGroups))
            await NotifyObserversAsync(chatId, userId);
        // No service message: nobody switched it off, the person who wanted it is gone (and a notice would bring
        // back a chat the other side deleted).
        foreach (var chatId in protectionChanged)
            await PublishChatUpdatedAsync(chatId, userId);
        await userChanges.NotifyAsync(userId, related);
        await NotifyContactOwnersAsync(contactOwners);

        logger.LogInformation("User {UserId} deleted the account ({GroupCount} groups left, {SessionCount} sessions revoked)",
            userId, leftGroups.Count + emptiedGroups.Count, revokedSessions.Count);
    }

    private async Task<GroupLeave> LeaveGroupAsync(Guid chatId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await ChatSequence.LockChatAsync(db, chatId, ct);
        var role = await db.ChatMembers
            .Where(m => m.ChatId == chatId && m.UserId == userId)
            .Select(m => (ChatRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        if (role is null) return GroupLeave.NotMember;

        await db.ChatMembers.Where(m => m.ChatId == chatId && m.UserId == userId).ExecuteDeleteAsync(ct);
        var remaining = await db.ChatMembers.CountAsync(m => m.ChatId == chatId, ct);
        if (remaining == 0)
        {
            // The last member out deletes the group.
            await db.Chats.Where(c => c.Id == chatId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.IsDeleted, true)
                .SetProperty(c => c.InviteToken, (string?)null)
                .SetProperty(c => c.MemberCount, 0)
                .SetProperty(c => c.UpdatedAt, now), ct);
            return GroupLeave.GroupEmptied;
        }

        if (role == ChatRole.Owner)
        {
            // Ownership passes to the oldest admin, else to the oldest member.
            var successorId = await db.ChatMembers
                .Where(m => m.ChatId == chatId)
                .OrderByDescending(m => m.Role)
                .ThenBy(m => m.JoinedAt)
                .ThenBy(m => m.UserId)
                .Select(m => m.UserId)
                .FirstAsync(ct);
            await db.ChatMembers
                .Where(m => m.ChatId == chatId && m.UserId == successorId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ChatRole.Owner), ct);
        }

        // The chat row is locked, so the exact count is safe to write.
        await db.Chats.Where(c => c.Id == chatId).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.MemberCount, remaining)
            .SetProperty(c => c.UpdatedAt, now), ct);
        return GroupLeave.Left;
    }

    /// <summary>Removes the user's part of a private chat's content protection; false when it was not on (anymore).</summary>
    private async Task<bool> TakeBackProtectionAsync(Guid chatId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        // The same lock ChatService takes to switch it: the other side switching at the same moment goes first or after.
        await ChatSequence.LockChatAsync(db, chatId, ct);
        var chat = await db.Chats.FirstAsync(c => c.Id == chatId, ct);
        if (!chat.ProtectContentBy.Contains(userId)) return false;
        chat.ProtectContentBy = ContentProtectionRules.Toggle(chat.ProtectContentBy, userId, enabled: false);
        chat.ProtectContent = ContentProtectionRules.IsProtected(chat.ProtectContentBy);
        chat.UpdatedAt = now;
        return true;
    }

    private static void Tombstone(User user, string unusablePasswordHash, DateTimeOffset now)
    {
        user.IsDeleted = true;
        user.DeletedAt = now;
        user.UpdatedAt = now;
        user.Username = User.DeletedUsernameFor(user.Id);
        user.UsernameNormalized = User.Normalize(user.Username);
        user.DisplayName = User.DeletedDisplayName;
        user.Bio = null;
        user.PhoneNumber = null;
        user.PhoneSource = PhoneSource.None;
        user.PhoneLinkedAt = null;
        user.AvatarMediaId = null;
        user.LastSeenAt = null;
        user.PasswordHash = unusablePasswordHash;

        var privacy = user.Privacy;
        privacy.LastSeen = PrivacyLevel.Nobody;
        privacy.ProfilePhoto = PrivacyLevel.Nobody;
        privacy.PhoneNumber = PrivacyLevel.Nobody;
        privacy.FindByPhone = PrivacyLevel.Nobody;
        privacy.Messages = PrivacyLevel.Nobody;
        privacy.Calls = PrivacyLevel.Nobody;
        privacy.GroupInvites = PrivacyLevel.Nobody;
    }

    private async Task AnnounceLeaveAsync(Guid chatId, Guid userId)
    {
        try
        {
            await systemMessages.PostSystemEventAsync(chatId, new SystemEventDto
            {
                Type = SystemEventType.MemberLeft,
                ActorId = userId,
                TargetUserIds = [userId],
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to post MemberLeft for deleted user {UserId} in chat {ChatId}", userId, chatId);
        }

        try
        {
            var members = await db.ChatMembers.AsNoTracking()
                .Where(m => m.ChatId == chatId)
                .Select(m => m.UserId)
                .ToListAsync();
            if (members.Count == 0) return;
            var clients = notifier.Users(members);
            await clients.ChatMembersChanged(new ChatRefEvent { ChatId = chatId });
            if (await LoadChatInfoAsync(chatId) is { } info)
                await clients.ChatUpdated(info);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish membership events for chat {ChatId}", chatId);
        }
    }

    private async Task PublishChatUpdatedAsync(Guid chatId, Guid deletedUserId)
    {
        try
        {
            var members = await db.ChatMembers.AsNoTracking()
                .Where(m => m.ChatId == chatId && m.UserId != deletedUserId)
                .Select(m => m.UserId)
                .ToListAsync();
            if (members.Count > 0 && await LoadChatInfoAsync(chatId) is { } info)
                await notifier.Users(members).ChatUpdated(info);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish ChatUpdated for chat {ChatId}", chatId);
        }
    }

    private async Task NotifyObserversAsync(Guid chatId, Guid userId)
    {
        foreach (var observer in membershipObservers)
        {
            try
            {
                await observer.OnMemberRemovedAsync(chatId, userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Membership observer {Observer} failed for chat {ChatId}", observer.GetType().Name, chatId);
            }
        }
    }

    private async Task NotifyContactOwnersAsync(IEnumerable<Guid> owners)
    {
        try
        {
            var connected = owners.Distinct().Where(presence.IsConnected).ToList();
            if (connected.Count > 0)
                await notifier.Users(connected).ContactsChanged(new ContactsChangedEvent());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish ContactsChanged after an account deletion");
        }
    }

    private Task<ChatInfoDto?> LoadChatInfoAsync(Guid chatId) =>
        db.Chats.AsNoTracking()
            .Where(c => c.Id == chatId && !c.IsDeleted)
            .Select(c => new ChatInfoDto
            {
                Id = c.Id,
                Type = c.Type,
                Title = c.Title,
                Description = c.Description,
                AvatarMediaId = c.AvatarMediaId,
                MemberCount = c.MemberCount,
                Permissions = new ChatPermissionsDto
                {
                    SendMessages = c.Permissions.SendMessages,
                    SendMedia = c.Permissions.SendMedia,
                    AddMembers = c.Permissions.AddMembers,
                    PinMessages = c.Permissions.PinMessages,
                    ChangeInfo = c.Permissions.ChangeInfo,
                },
                ActiveCallId = db.Calls
                    .Where(x => x.ChatId == c.Id && (x.Status == CallStatus.Ringing || x.Status == CallStatus.Active))
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefault(),
                ProtectContent = c.ProtectContent,
                ProtectContentBy = c.ProtectContentBy,
            })
            .FirstOrDefaultAsync();
}
