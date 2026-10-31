using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Calls;

namespace SkyMessage.Infrastructure.Calls;

/// <summary>
/// In-memory delayed grace checks (one pending per call + user). Lost on restart, which is acceptable:
/// reconciliation ends calls whose participants stay away anyway.
/// </summary>
internal sealed class CallGraceScheduler(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<CallGraceScheduler> logger) : ICallGraceScheduler
{
    private readonly ConcurrentDictionary<(Guid CallId, Guid UserId), DateTimeOffset> _due = new();

    public void Schedule(Guid callId, Guid userId, TimeSpan delay)
    {
        var key = (callId, userId);
        var due = time.GetUtcNow() + delay;
        while (true)
        {
            if (_due.TryAdd(key, due))
            {
                _ = RunAsync(key);
                return;
            }
            if (!_due.TryGetValue(key, out var current)) continue;
            if (current >= due || _due.TryUpdate(key, due, current)) return;
        }
    }

    private async Task RunAsync((Guid CallId, Guid UserId) key)
    {
        var stopping = lifetime.ApplicationStopping;
        try
        {
            while (true)
            {
                if (!_due.TryGetValue(key, out var due)) return;
                var wait = due - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, stopping);
                    continue;
                }
                // Taken only if nobody pushed the due time later in the meantime.
                if (_due.TryRemove(new KeyValuePair<(Guid, Guid), DateTimeOffset>(key, due))) break;
            }

            TimeSpan? again;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var handler = scope.ServiceProvider.GetRequiredService<ICallGraceHandler>();
                again = await handler.RunGraceCheckAsync(key.CallId, key.UserId, stopping);
            }
            if (again is { } delay) Schedule(key.CallId, key.UserId, delay);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grace check for user {UserId} in call {CallId} failed", key.UserId, key.CallId);
        }
    }
}
