using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>
/// Builds viewer-dependent <see cref="ChatSummaryDto"/>s in a fixed number of queries: one projection over the
/// viewer's memberships (with correlated counters), then last messages, people, blocks and active calls.
/// </summary>
internal sealed class ChatSummaryBuilder(IAppDbContext db, MessageDtoLoader loader, IUserSummaryService userSummaries)
{
    /// <summary>Neutral fallback; clients localize Saved Messages by <see cref="ChatType.Saved"/>.</summary>
    public const string SavedMessagesTitle = "Saved Messages";

    private sealed class SummaryRow
    {
        public Guid ChatId { get; init; }
        public ChatType Type { get; init; }
        public string? Title { get; init; }
        public Guid? AvatarMediaId { get; init; }
        public int MemberCount { get; init; }
        public bool SendMessages { get; init; }
        public bool SendMedia { get; init; }
        public bool AddMembers { get; init; }
        public bool PinMessages { get; init; }
        public bool ChangeInfo { get; init; }
        public long LastMessageSeq { get; init; }
        public DateTimeOffset LastActivityAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public ChatRole Role { get; init; }
        public long LastReadSeq { get; init; }
        public long ClearedUpToSeq { get; init; }
        public bool IsPinned { get; init; }
        public DateTimeOffset? PinnedAt { get; init; }
        public long PinOrder { get; init; }
        public bool IsArchived { get; init; }
        public DateTimeOffset? MutedUntil { get; init; }
        public bool ContactBarDismissed { get; init; }
        public bool ProtectContent { get; init; }
        public List<Guid> ProtectContentBy { get; init; } = [];
        public Guid? LastMessageId { get; init; }
        public int UnreadCount { get; init; }
        public int UnreadMentionCount { get; init; }
        public long? FirstUnreadMentionSeq { get; init; }
        public long? OthersLastReadSeq { get; init; }
        public Guid? PeerId { get; init; }
    }

    public async Task<ChatSummaryDto> BuildOneAsync(Guid viewerId, Guid chatId, CancellationToken ct) =>
        (await BuildAsync(viewerId, [chatId], visibleOnly: false, ct)).FirstOrDefault()
        ?? throw new NotFoundException(ErrorCodes.ChatNotFound);

    /// <param name="chatIds">Null = every chat of the viewer.</param>
    /// <param name="visibleOnly">Skip chats the viewer has hidden (chat list semantics).</param>
    public async Task<List<ChatSummaryDto>> BuildAsync(Guid viewerId, IReadOnlyCollection<Guid>? chatIds, bool visibleOnly, CancellationToken ct)
    {
        var memberships = db.ChatMembers.AsNoTracking().Where(cm => cm.UserId == viewerId && !cm.Chat!.IsDeleted);
        if (chatIds is not null)
        {
            if (chatIds.Count == 0) return [];
            var ids = chatIds.Distinct().ToList();
            memberships = memberships.Where(cm => ids.Contains(cm.ChatId));
        }
        if (visibleOnly)
            memberships = memberships.Where(cm => !cm.IsHidden);

        var rows = await memberships
            .Select(cm => new SummaryRow
            {
                ChatId = cm.ChatId,
                Type = cm.Chat!.Type,
                Title = cm.Chat.Title,
                AvatarMediaId = cm.Chat.AvatarMediaId,
                MemberCount = cm.Chat.MemberCount,
                SendMessages = cm.Chat.Permissions.SendMessages,
                SendMedia = cm.Chat.Permissions.SendMedia,
                AddMembers = cm.Chat.Permissions.AddMembers,
                PinMessages = cm.Chat.Permissions.PinMessages,
                ChangeInfo = cm.Chat.Permissions.ChangeInfo,
                LastMessageSeq = cm.Chat.LastMessageSeq,
                LastActivityAt = cm.Chat.LastActivityAt,
                CreatedAt = cm.Chat.CreatedAt,
                Role = cm.Role,
                LastReadSeq = cm.LastReadSeq,
                ClearedUpToSeq = cm.ClearedUpToSeq,
                IsPinned = cm.IsPinned,
                PinnedAt = cm.PinnedAt,
                PinOrder = cm.PinOrder,
                IsArchived = cm.IsArchived,
                MutedUntil = cm.MutedUntil,
                ContactBarDismissed = cm.ContactBarDismissed,
                ProtectContent = cm.Chat.ProtectContent,
                ProtectContentBy = cm.Chat.ProtectContentBy,
                // Newest message this viewer can see (after their clear-history mark, not deleted, not hidden).
                LastMessageId = db.Messages
                    .Where(m => m.ChatId == cm.ChatId && m.Seq > cm.ClearedUpToSeq && m.DeletedAt == null
                                && !db.HiddenMessages.Any(h => h.MessageId == m.Id && h.UserId == viewerId))
                    .OrderByDescending(m => m.Seq)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefault(),
                UnreadCount = db.Messages.Count(m => m.ChatId == cm.ChatId
                                                     && m.Seq > Math.Max(cm.LastReadSeq, cm.ClearedUpToSeq)
                                                     && m.DeletedAt == null
                                                     && (m.SenderId == null || m.SenderId != viewerId)
                                                     && !db.HiddenMessages.Any(h => h.MessageId == m.Id && h.UserId == viewerId)),
                UnreadMentionCount = db.MessageMentions.Count(x => x.UserId == viewerId
                                                                   && x.ChatId == cm.ChatId
                                                                   && x.Seq > Math.Max(cm.LastReadSeq, cm.ClearedUpToSeq)
                                                                   && !db.HiddenMessages.Any(h => h.MessageId == x.MessageId && h.UserId == viewerId)),
                FirstUnreadMentionSeq = db.MessageMentions
                    .Where(x => x.UserId == viewerId
                                && x.ChatId == cm.ChatId
                                && x.Seq > Math.Max(cm.LastReadSeq, cm.ClearedUpToSeq)
                                && !db.HiddenMessages.Any(h => h.MessageId == x.MessageId && h.UserId == viewerId))
                    .Min(x => (long?)x.Seq),
                OthersLastReadSeq = db.ChatMembers
                    .Where(o => o.ChatId == cm.ChatId && o.UserId != viewerId)
                    .Max(o => (long?)o.LastReadSeq),
                PeerId = cm.Chat.Type == ChatType.Private
                    ? db.ChatMembers
                        .Where(o => o.ChatId == cm.ChatId && o.UserId != viewerId)
                        .OrderBy(o => o.JoinedAt)
                        .Select(o => (Guid?)o.UserId)
                        .FirstOrDefault()
                    : null,
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var lastIds = rows.Where(r => r.LastMessageId is not null).Select(r => r.LastMessageId!.Value).ToList();
        var lastMessages = (await loader.LoadAsync(lastIds, ct)).ToDictionary(m => m.Id);

        var peerIds = rows.Where(r => r.PeerId is not null).Select(r => r.PeerId!.Value).Distinct().ToList();
        var userIds = peerIds
            .Concat(lastMessages.Values.Where(m => m.SenderId is not null).Select(m => m.SenderId!.Value))
            .Distinct()
            .ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, UserSummaryDto>()
            : await userSummaries.GetAsync(viewerId, userIds, ct);

        var blockedPeers = peerIds.Count == 0
            ? []
            : (await db.Blocks.AsNoTracking()
                .Where(b => (b.BlockerId == viewerId && peerIds.Contains(b.BlockedId))
                            || (b.BlockedId == viewerId && peerIds.Contains(b.BlockerId)))
                .Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId)
                .ToListAsync(ct)).ToHashSet();

        var rowChatIds = rows.Select(r => r.ChatId).ToList();
        var calls = await db.Calls.AsNoTracking()
            .Where(c => rowChatIds.Contains(c.ChatId) && (c.Status == CallStatus.Ringing || c.Status == CallStatus.Active))
            .Select(c => new { c.ChatId, c.Id, c.CreatedAt })
            .ToListAsync(ct);
        var activeCalls = calls
            .GroupBy(c => c.ChatId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CreatedAt).First().Id);

        return rows.Select(r =>
        {
            var peer = r.PeerId is { } peerId ? users.GetValueOrDefault(peerId) : null;
            var last = r.LastMessageId is { } lastId ? lastMessages.GetValueOrDefault(lastId) : null;
            var lastSender = last?.SenderId is { } senderId ? users.GetValueOrDefault(senderId) : null;
            return new ChatSummaryDto
            {
                Id = r.ChatId,
                Type = r.Type,
                Title = r.Type switch
                {
                    ChatType.Group => r.Title ?? "",
                    ChatType.Private => peer is null ? "" : peer.ContactName ?? peer.DisplayName,
                    _ => SavedMessagesTitle,
                },
                AvatarMediaId = r.Type switch
                {
                    ChatType.Group => r.AvatarMediaId,
                    // The summary service already applies the peer's ProfilePhoto privacy and blocks.
                    ChatType.Private => peer?.AvatarMediaId,
                    _ => null,
                },
                Peer = peer,
                MemberCount = r.MemberCount,
                MyRole = r.Role,
                Permissions = new ChatPermissionsDto
                {
                    SendMessages = r.SendMessages,
                    SendMedia = r.SendMedia,
                    AddMembers = r.AddMembers,
                    PinMessages = r.PinMessages,
                    ChangeInfo = r.ChangeInfo,
                },
                LastMessage = last,
                LastMessageSender = lastSender,
                LastMessageSeq = r.LastMessageSeq,
                LastActivityAt = r.LastActivityAt,
                UnreadCount = r.UnreadCount,
                UnreadMentionCount = r.UnreadMentionCount,
                FirstUnreadMentionSeq = r.FirstUnreadMentionSeq,
                MyLastReadSeq = r.LastReadSeq,
                // Saved Messages has no other reader: everything counts as read.
                OthersLastReadSeq = r.Type == ChatType.Saved ? r.LastMessageSeq : r.OthersLastReadSeq ?? 0,
                ClearedUpToSeq = r.ClearedUpToSeq,
                IsPinned = r.IsPinned,
                PinnedAt = r.PinnedAt,
                PinOrder = r.IsPinned ? r.PinOrder : 0,
                IsArchived = r.IsArchived,
                MutedUntil = r.MutedUntil,
                ContactBarDismissed = r.ContactBarDismissed,
                IsBlocked = r.Type == ChatType.Private && r.PeerId is { } p && blockedPeers.Contains(p),
                ActiveCallId = activeCalls.TryGetValue(r.ChatId, out var callId) ? callId : null,
                ProtectContent = r.ProtectContent,
                ProtectContentBy = r.ProtectContentBy,
                CreatedAt = r.CreatedAt,
            };
        }).ToList();
    }
}
