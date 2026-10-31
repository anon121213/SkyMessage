using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Identity;

namespace SkyMessage.Infrastructure.Identity;

/// <summary>
/// Creates the deployment's InstanceId on first start. Best effort and non-blocking: if the database is not
/// ready yet (e.g. a test fixture creates the schema after start-up) the id is created on first use instead.
/// </summary>
internal sealed class ServerInstanceInitializer(IServiceScopeFactory scopes, ILogger<ServerInstanceInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var id = await scope.ServiceProvider.GetRequiredService<ServerInfoService>().GetInstanceIdAsync(stoppingToken);
            logger.LogInformation("Server instance {InstanceId}", id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning("Server instance id not initialized at start-up ({Error}); it will be created on first use", ex.Message);
        }
    }
}
