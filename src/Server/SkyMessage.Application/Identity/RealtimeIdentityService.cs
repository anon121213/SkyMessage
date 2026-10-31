using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

/// <summary>Database side of the realtime hub: session checks, activity stamps and typing recipients.</summary>
public sealed class RealtimeIdentityService(IAppDbContext db, IMemoryCache cache, IPresenceService presence, IClock clock)
{
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);
    /// <summary>Short: additions are not observed, so newly added members must start getting Typing soon.</summary>
    public static readonly TimeSpan MembershipTtl = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan BlockPartnersTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Uncached check that the connecting session is still active (closes the race with a revocation that
    /// ran between authentication and registration), then stamps its activity.
    /// </summary>
    public async Task<bool> OnConnectedAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var active = await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null && s.RefreshTokenExpiresAt > now, ct);
        if (active)
            await TouchSessionAsync(sessionId, ct);
        return active;
    }

    /// <summary>Updates <c>Session.LastActiveAt</c> at most once per <see cref="TouchInterval"/>.</summary>
    public async Task TouchSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var key = IdentityCacheKeys.SessionTouch(sessionId);
        if (cache.TryGetValue(key, out _)) return;
        cache.Set(key, true, IdentityCacheKeys.Absolute(TouchInterval));

        var now = clock.UtcNow;
        await db.Sessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActiveAt, now), ct);
    }

    /// <summary>
    /// Connected members of the chat that should see the sender typing: not the sender, not members who
    /// hid the chat, and nobody on either side of a block with the sender (users who blocked the sender, and
    /// users the sender blocked: blocked users never receive the blocker's typing). Empty when the sender is
    /// not a member.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetTypingRecipientsAsync(Guid chatId, Guid senderId, CancellationToken ct)
    {
        var chat = await GetChatAsync(chatId, ct);
        if (chat is null || chat.Type == ChatType.Saved || !chat.Members.Contains(senderId))
            return [];

        var blockPartners = await GetBlockPartnersAsync(senderId, ct);
        var recipients = new List<Guid>();
        foreach (var memberId in chat.Members)
        {
            if (memberId == senderId || chat.Hidden.Contains(memberId) || blockPartners.Contains(memberId)) continue;
            if (presence.IsConnected(memberId))
                recipients.Add(memberId);
        }
        return recipients;
    }

    private async Task<TypingChat?> GetChatAsync(Guid chatId, CancellationToken ct)
    {
        var key = IdentityCacheKeys.TypingMembers(chatId);
        if (cache.TryGetValue(key, out TypingChat? cached))
            return cached;

        var row = await db.Chats.AsNoTracking()
            .Where(c => c.Id == chatId && !c.IsDeleted)
            .Select(c => new
            {
                c.Type,
                Members = c.Members.Select(m => new { m.UserId, m.IsHidden }).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        var chat = row is null
            ? null
            : new TypingChat(
                row.Type,
                row.Members.Select(m => m.UserId).ToHashSet(),
                row.Members.Where(m => m.IsHidden).Select(m => m.UserId).ToHashSet());
        cache.Set(key, chat, IdentityCacheKeys.Absolute(MembershipTtl));
        return chat;
    }

    /// <summary>Users with a block between them and <paramref name="userId"/>, in either direction.</summary>
    private async Task<HashSet<Guid>> GetBlockPartnersAsync(Guid userId, CancellationToken ct)
    {
        var key = IdentityCacheKeys.BlockPartners(userId);
        if (cache.TryGetValue(key, out HashSet<Guid>? cached) && cached is not null)
            return cached;

        var partners = (await db.Blocks.AsNoTracking()
                .Where(b => b.BlockedId == userId || b.BlockerId == userId)
                .Select(b => b.BlockedId == userId ? b.BlockerId : b.BlockedId)
                .ToListAsync(ct))
            .ToHashSet();
        cache.Set(key, partners, IdentityCacheKeys.Absolute(BlockPartnersTtl));
        return partners;
    }

    /// <summary>Immutable once cached (read concurrently by many hub invocations).</summary>
    private sealed record TypingChat(ChatType Type, HashSet<Guid> Members, HashSet<Guid> Hidden);
}
