using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Blocks (idempotent). A change sends <c>ContactsChanged{BlocksChanged}</c> to the blocker and
/// <c>UserChanged</c> to both sides, since each now sees the other differently.
/// </summary>
public sealed class BlockService(
    IAppDbContext db,
    IUserSummaryService summaries,
    IRealtimeNotifier notifier,
    IMemoryCache cache,
    IClock clock,
    ILogger<BlockService> logger)
{
    public async Task<IReadOnlyList<UserSummaryDto>> ListAsync(Guid userId, CancellationToken ct)
    {
        var ids = await db.Blocks.AsNoTracking()
            .Where(b => b.BlockerId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => b.BlockedId)
            .ToListAsync(ct);
        var map = await summaries.GetAsync(userId, ids, ct);
        return ids.Where(map.ContainsKey).Select(id => map[id]).ToList();
    }

    public async Task BlockAsync(Guid userId, Guid targetId, CancellationToken ct)
    {
        if (userId == targetId)
            throw new BadRequestException(ErrorCodes.CannotTargetSelf);
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == targetId, ct))
            throw new NotFoundException(ErrorCodes.UserNotFound);
        if (await db.Blocks.AsNoTracking().AnyAsync(b => b.BlockerId == userId && b.BlockedId == targetId, ct))
            return;

        db.Blocks.Add(new Block { BlockerId = userId, BlockedId = targetId, CreatedAt = clock.UtcNow });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            // Blocked concurrently by another request (which publishes the events).
            if (await db.Blocks.AsNoTracking().AnyAsync(b => b.BlockerId == userId && b.BlockedId == targetId, ct))
                return;
            throw;
        }
        await PublishAsync(userId, targetId);
    }

    public async Task UnblockAsync(Guid userId, Guid targetId, CancellationToken ct)
    {
        if (userId == targetId)
            throw new BadRequestException(ErrorCodes.CannotTargetSelf);
        var removed = await db.Blocks.Where(b => b.BlockerId == userId && b.BlockedId == targetId).ExecuteDeleteAsync(ct);
        if (removed > 0)
            await PublishAsync(userId, targetId);
    }

    private async Task PublishAsync(Guid userId, Guid targetId)
    {
        // Typing fan-out excludes block partners in both directions, so both users' sets changed.
        cache.Remove(IdentityCacheKeys.BlockPartners(userId));
        cache.Remove(IdentityCacheKeys.BlockPartners(targetId));
        try
        {
            var me = notifier.User(userId);
            await me.ContactsChanged(new ContactsChangedEvent { BlocksChanged = true });
            await me.UserChanged(new UserRefEvent { UserId = targetId });
            await notifier.User(targetId).UserChanged(new UserRefEvent { UserId = userId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish block change of {UserId}", userId);
        }
    }
}
