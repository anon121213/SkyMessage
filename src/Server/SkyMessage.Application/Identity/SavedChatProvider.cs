using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>Every user owns one "Saved messages" chat (created at registration, recreated if ever missing).</summary>
public sealed class SavedChatProvider(IAppDbContext db, IMemoryCache cache, IClock clock)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public static Chat Create(Guid userId, DateTimeOffset now)
    {
        var chat = new Chat
        {
            Type = ChatType.Saved,
            PrivateKey = Chat.SavedKeyFor(userId),
            CreatedById = userId,
            CreatedAt = now,
            UpdatedAt = now,
            LastActivityAt = now,
            MemberCount = 1,
        };
        chat.Members.Add(new ChatMember { ChatId = chat.Id, UserId = userId, Role = ChatRole.Owner, JoinedAt = now });
        return chat;
    }

    public void Remember(Guid userId, Guid chatId) =>
        cache.Set(IdentityCacheKeys.SavedChat(userId), chatId, IdentityCacheKeys.Sliding(CacheTtl));

    public void Forget(Guid userId) => cache.Remove(IdentityCacheKeys.SavedChat(userId));

    /// <summary>
    /// Id of the user's Saved chat; creates it when missing. Saves immediately, so call it only when the
    /// context has no other pending changes.
    /// </summary>
    public async Task<Guid> GetIdAsync(Guid userId, CancellationToken ct)
    {
        if (cache.TryGetValue(IdentityCacheKeys.SavedChat(userId), out Guid cached))
            return cached;

        var key = Chat.SavedKeyFor(userId);
        var id = await FindAsync(key, ct);
        if (id is null)
        {
            var chat = Create(userId, clock.UtcNow);
            db.Chats.Add(chat);
            try
            {
                await db.SaveChangesAsync(ct);
                id = chat.Id;
            }
            catch (DbUpdateException)
            {
                // Created concurrently (unique private key): use the winner's row.
                foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity == chat || e.Entity is ChatMember m && m.ChatId == chat.Id).ToList())
                    entry.State = EntityState.Detached;
                id = await FindAsync(key, ct);
                if (id is null) throw;
            }
        }

        Remember(userId, id.Value);
        return id.Value;
    }

    private Task<Guid?> FindAsync(string key, CancellationToken ct) =>
        db.Chats.AsNoTracking().Where(c => c.PrivateKey == key).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
}
