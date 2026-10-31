using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>
/// Post-commit side effects: realtime events, service messages, membership observers and pushes. The data is
/// already committed when these run, so failures are logged instead of failing the request (clients catch up
/// on their next resync) and request cancellation no longer applies.
/// </summary>
internal sealed class MessagingEvents(
    IRealtimeNotifier notifier,
    ISystemMessageService systemMessages,
    IEnumerable<IChatMembershipObserver> membershipObservers,
    IEnumerable<IPushNotificationService> pushServices,
    ILogger<MessagingEvents> logger)
{
    public async Task ToUsersAsync(IReadOnlyCollection<Guid> userIds, string eventName, Func<IRealtimeClient, Task> send)
    {
        if (userIds.Count == 0) return;
        try
        {
            var target = userIds.Count == 1 ? notifier.User(userIds.First()) : notifier.Users(userIds);
            await send(target);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Realtime event {Event} could not be published to {Count} user(s)", eventName, userIds.Count);
        }
    }

    public Task ToUserAsync(Guid userId, string eventName, Func<IRealtimeClient, Task> send) =>
        ToUsersAsync([userId], eventName, send);

    /// <summary>Posts a service message (own transaction, broadcasts MessageCreated); null when it failed.</summary>
    public async Task<MessageDto?> PostSystemEventAsync(Guid chatId, SystemEventDto systemEvent)
    {
        try
        {
            return await systemMessages.PostSystemEventAsync(chatId, systemEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "System message {Type} could not be posted in chat {ChatId}", systemEvent.Type, chatId);
            return null;
        }
    }

    public async Task ChatDeletingAsync(Guid chatId)
    {
        foreach (var observer in membershipObservers)
        {
            try
            {
                await observer.OnChatDeletingAsync(chatId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Membership observer {Observer} failed before deleting chat {ChatId}",
                    observer.GetType().Name, chatId);
            }
        }
    }

    public async Task MemberRemovedAsync(Guid chatId, IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            foreach (var observer in membershipObservers)
            {
                try
                {
                    await observer.OnMemberRemovedAsync(chatId, userId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Membership observer {Observer} failed for chat {ChatId}, user {UserId}",
                        observer.GetType().Name, chatId, userId);
                }
            }
        }
    }

    public async Task PushNewMessageAsync(MessageDto message, IReadOnlyCollection<Guid> recipientIds)
    {
        if (recipientIds.Count == 0) return;
        foreach (var push in pushServices)
        {
            try
            {
                await push.NotifyNewMessageAsync(message, recipientIds, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Push for message {MessageId} failed", message.Id);
            }
        }
    }
}
