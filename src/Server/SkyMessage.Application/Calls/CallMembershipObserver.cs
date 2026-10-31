using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Interfaces;

namespace SkyMessage.Application.Calls;

/// <summary>A member removed from a group leaves its ongoing call: marked Left and disconnected on every device.</summary>
internal sealed class CallMembershipObserver(IServiceScopeFactory scopes, ILogger<CallMembershipObserver> logger) : IChatMembershipObserver
{
    public async Task OnMemberRemovedAsync(Guid chatId, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Own scope and DbContext: the caller's context may still track its own changes.
            await using var scope = scopes.CreateAsyncScope();
            var lifecycle = scope.ServiceProvider.GetRequiredService<CallLifecycle>();
            await lifecycle.OnMemberRemovedAsync(chatId, userId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Call cleanup after removing user {UserId} from chat {ChatId} failed", userId, chatId);
        }
    }

    public async Task OnChatDeletingAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var lifecycle = scope.ServiceProvider.GetRequiredService<CallLifecycle>();
            await lifecycle.OnChatDeletingAsync(chatId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Ending calls of deleted chat {ChatId} failed", chatId);
        }
    }
}
