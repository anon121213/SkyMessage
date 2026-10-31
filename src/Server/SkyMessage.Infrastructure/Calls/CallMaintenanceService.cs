using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Calls;

namespace SkyMessage.Infrastructure.Calls;

/// <summary>
/// Every 5 s: ring timeouts. Every 30 s: reconciliation with LiveKit. Each run gets its own DI scope; failures
/// (database or LiveKit down, schema not created yet) are logged once per outage and retried on the next tick.
/// </summary>
internal sealed class CallMaintenanceService(IServiceScopeFactory scopes, TimeProvider time, ILogger<CallMaintenanceService> logger)
    : BackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly Job _ringTimeouts = new("ring timeouts");
    private readonly Job _reconciliation = new("reconciliation");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, time);
        var sinceReconcile = TimeSpan.Zero;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunAsync(_ringTimeouts, (m, ct) => m.ExpireRingingAsync(ct), stoppingToken);

                sinceReconcile += Tick;
                if (sinceReconcile < ReconcileInterval) continue;
                sinceReconcile = TimeSpan.Zero;
                await RunAsync(_reconciliation, (m, ct) => m.ReconcileAsync(ct), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(Job job, Func<ICallMaintenance, CancellationToken, Task> run, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await run(scope.ServiceProvider.GetRequiredService<ICallMaintenance>(), stoppingToken);
            if (job.Failures > 0)
                logger.LogInformation("Call maintenance ({Job}) recovered after {Failures} failed run(s)", job.Name, job.Failures);
            job.Failures = 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (job.Failures++ == 0) logger.LogWarning(ex, "Call maintenance ({Job}) failed; retrying on every run", job.Name);
            else logger.LogDebug(ex, "Call maintenance ({Job}) failed again ({Failures} in a row)", job.Name, job.Failures);
        }
    }

    private sealed class Job(string name)
    {
        public string Name { get; } = name;
        public int Failures { get; set; }
    }
}
