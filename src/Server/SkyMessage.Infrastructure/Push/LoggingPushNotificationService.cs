using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;

namespace SkyMessage.Infrastructure.Push;

public static class PushSetup
{
    /// <summary>v1: push tokens are stored but nothing is sent. A real FCM/APNs sender registered first wins.</summary>
    public static IServiceCollection AddPushNotifications(this IServiceCollection services)
    {
        services.TryAddSingleton<IPushNotificationService, LoggingPushNotificationService>();
        return services;
    }
}

/// <summary>
/// No-op push service that only logs what would be pushed (users without a live connection), honoring
/// <c>Push:Enabled</c> so the switch already behaves as it will once delivery exists. Ids only, no content.
/// </summary>
internal sealed class LoggingPushNotificationService(
    IOptionsMonitor<PushOptions> options,
    IPresenceService presence,
    ILogger<LoggingPushNotificationService> logger) : IPushNotificationService
{
    public Task NotifyNewMessageAsync(MessageDto message, IReadOnlyCollection<Guid> recipientIds, CancellationToken cancellationToken = default)
    {
        Log("message", message.Id, recipientIds);
        return Task.CompletedTask;
    }

    public Task NotifyIncomingCallAsync(CallDto call, IReadOnlyCollection<Guid> recipientIds, CancellationToken cancellationToken = default)
    {
        Log("incoming call", call.Id, recipientIds);
        return Task.CompletedTask;
    }

    public Task NotifyCallEndedAsync(CallDto call, IReadOnlyCollection<Guid> recipientIds, CancellationToken cancellationToken = default)
    {
        Log("call ended", call.Id, recipientIds);
        return Task.CompletedTask;
    }

    private void Log(string kind, Guid subjectId, IReadOnlyCollection<Guid> recipientIds)
    {
        // Devices with a live realtime connection get the event directly; only the rest would need a push.
        var offline = recipientIds.Count(id => !presence.IsConnected(id));
        if (offline == 0) return;

        if (options.CurrentValue.Enabled)
            logger.LogInformation("Push delivery is not available in this version: {Kind} {SubjectId} for {Count} offline recipients",
                kind, subjectId, offline);
        else
            logger.LogDebug("Push disabled: {Kind} {SubjectId} not pushed to {Count} offline recipients", kind, subjectId, offline);
    }
}
