using Microsoft.Extensions.Caching.Memory;
using SkyMessage.Application.Common.Interfaces;

namespace SkyMessage.Application.Identity;

/// <summary>Drops cached typing recipients of a chat as soon as someone leaves it (or it is deleted).</summary>
internal sealed class IdentityMembershipObserver(IMemoryCache cache) : IChatMembershipObserver
{
    public Task OnMemberRemovedAsync(Guid chatId, Guid userId, CancellationToken cancellationToken = default)
    {
        cache.Remove(IdentityCacheKeys.TypingMembers(chatId));
        return Task.CompletedTask;
    }

    public Task OnChatDeletingAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        cache.Remove(IdentityCacheKeys.TypingMembers(chatId));
        return Task.CompletedTask;
    }
}
