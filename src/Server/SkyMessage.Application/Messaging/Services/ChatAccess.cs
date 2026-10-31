using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>The caller's membership in a chat plus the chat facts most checks need (read without tracking).</summary>
internal sealed record MemberContext(
    Guid ChatId,
    Guid UserId,
    ChatType Type,
    ChatRole Role,
    GroupPermissions Permissions,
    long LastReadSeq,
    long ClearedUpToSeq,
    bool IsHidden,
    long LastMessageSeq,
    string? InviteToken)
{
    public bool IsAdmin => ChatPolicy.IsAdmin(Role);
    public bool IsGroup => Type == ChatType.Group;
}

internal static class ChatAccess
{
    public static async Task<MemberContext?> FindMemberAsync(this IAppDbContext db, Guid chatId, Guid userId, CancellationToken ct)
    {
        var row = await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.UserId == userId && !m.Chat!.IsDeleted)
            .Select(m => new
            {
                m.Role,
                m.LastReadSeq,
                m.ClearedUpToSeq,
                m.IsHidden,
                m.Chat!.Type,
                m.Chat.LastMessageSeq,
                m.Chat.InviteToken,
                m.Chat.Permissions.SendMessages,
                m.Chat.Permissions.SendMedia,
                m.Chat.Permissions.AddMembers,
                m.Chat.Permissions.PinMessages,
                m.Chat.Permissions.ChangeInfo,
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var permissions = new GroupPermissions
        {
            SendMessages = row.SendMessages,
            SendMedia = row.SendMedia,
            AddMembers = row.AddMembers,
            PinMessages = row.PinMessages,
            ChangeInfo = row.ChangeInfo,
        };
        return new MemberContext(chatId, userId, row.Type, row.Role, permissions, row.LastReadSeq, row.ClearedUpToSeq,
            row.IsHidden, row.LastMessageSeq, row.InviteToken);
    }

    /// <summary>The caller's membership; anything else is 404 chat_not_found (never leak existence).</summary>
    public static async Task<MemberContext> RequireMemberAsync(this IAppDbContext db, Guid chatId, Guid userId, CancellationToken ct) =>
        await db.FindMemberAsync(chatId, userId, ct) ?? throw new NotFoundException(ErrorCodes.ChatNotFound);

    public static MemberContext RequireGroup(this MemberContext member) =>
        member.IsGroup ? member : throw new BadRequestException(ErrorCodes.NotAGroup);

    /// <summary>
    /// Messages of the member's chat that the member can see: after their "clear history" watermark and not
    /// deleted "for me". Tombstones are included; callers add <c>DeletedAt == null</c> where needed.
    /// </summary>
    public static IQueryable<Message> VisibleMessages(this IAppDbContext db, MemberContext member)
    {
        var chatId = member.ChatId;
        var userId = member.UserId;
        var clearedUpToSeq = member.ClearedUpToSeq;
        return db.Messages.Where(m => m.ChatId == chatId
                                      && m.Seq > clearedUpToSeq
                                      && !db.HiddenMessages.Any(h => h.MessageId == m.Id && h.UserId == userId));
    }

    public static Task<List<Guid>> MemberIdsAsync(this IAppDbContext db, Guid chatId, CancellationToken ct) =>
        db.ChatMembers.AsNoTracking().Where(m => m.ChatId == chatId).Select(m => m.UserId).ToListAsync(ct);

    /// <summary>The other participant of a private chat.</summary>
    public static Task<Guid?> PeerIdAsync(this IAppDbContext db, Guid chatId, Guid userId, CancellationToken ct) =>
        db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.UserId != userId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(ct);

    /// <summary>Viewer-independent chat info (broadcast in ChatUpdated).</summary>
    public static Task<ChatInfoDto?> LoadChatInfoAsync(this IAppDbContext db, Guid chatId, CancellationToken ct) =>
        db.Chats.AsNoTracking()
            .Where(c => c.Id == chatId)
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
            })
            .FirstOrDefaultAsync(ct);

    public static ChatPermissionsDto ToDto(this GroupPermissions p) => new()
    {
        SendMessages = p.SendMessages,
        SendMedia = p.SendMedia,
        AddMembers = p.AddMembers,
        PinMessages = p.PinMessages,
        ChangeInfo = p.ChangeInfo,
    };
}

/// <summary>Row locks for read-modify-write of existing messages (edit, reactions, pins, tombstones).</summary>
internal static class MessageLocks
{
    /// <summary>
    /// <c>SELECT ... FOR UPDATE</c> in id order (consistent lock order, no deadlocks between batches).
    /// Requires an explicit transaction; locks are held until it ends.
    /// </summary>
    public static async Task LockAsync(IAppDbContext db, IReadOnlyCollection<Guid> messageIds, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("MessageLocks.LockAsync requires an explicit transaction.");
        if (messageIds.Count == 0) return;
        var ids = messageIds.Distinct().ToArray();
        // SqlQuery over non-composable SQL: materialize with ToListAsync (see ChatSequence).
        await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM messages WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
    }
}

internal static class MessagingErrors
{
    public static BadRequestException Validation(string field, string message) =>
        new(ErrorCodes.Validation, "Validation failed", new Dictionary<string, string[]> { [field] = [message] });

    public static BadRequestException Validation(string field, IEnumerable<string> messages) =>
        new(ErrorCodes.Validation, "Validation failed", new Dictionary<string, string[]> { [field] = messages.ToArray() });
}
