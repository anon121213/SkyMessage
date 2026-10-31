using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Identity;

namespace SkyMessage.Infrastructure.Identity;

/// <summary>
/// Drives <see cref="PresenceTracker"/>: completes debounced offline transitions every second and hands
/// every transition (in order) to <see cref="PresenceBroadcaster"/>.
/// </summary>
internal sealed class PresenceWorker(
    PresenceTracker tracker,
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<PresenceWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweeping = SweepAsync(stoppingToken);
        try
        {
            await foreach (var transition in tracker.Transitions.ReadAllAsync(stoppingToken))
                await PublishAsync(transition, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await sweeping;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Users still online were last seen now; persist it so a restart does not show stale times.
        var online = tracker.OnlineUserIds();
        if (online.Count == 0) return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PresenceBroadcaster>()
                .PersistLastSeenAsync(online, clock.UtcNow, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist last-seen times of {Count} online users on shutdown", online.Count);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    tracker.SweepExpired();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Presence sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task PublishAsync(PresenceTransition transition, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PresenceBroadcaster>()
                .PublishTransitionAsync(transition.UserId, transition.IsOnline, transition.At, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish presence of {UserId}", transition.UserId);
        }
    }
}
