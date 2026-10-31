using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.Persistence;

namespace SkyMessage.Infrastructure.MediaFiles.Processing;

/// <summary>In-memory hand-off from "complete" to the worker. Lost on restart by design: Processing rows are re-queued at startup.</summary>
internal sealed class MediaProcessingQueue : IMediaProcessingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid mediaId) => _channel.Writer.TryWrite(mediaId);
}

/// <summary>
/// Runs <see cref="MediaProcessor"/> for queued media with at most <c>MediaProcessing:MaxConcurrency</c> jobs at a
/// time. On start it re-queues every row still in Processing (crash or restart mid-job). Duplicate queue entries
/// are harmless: an id is never processed twice concurrently and finished rows are skipped. Transient database
/// failures are retried with a growing delay before the media is marked Failed.
/// </summary>
internal sealed class MediaProcessingWorker(
    MediaProcessingQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<MediaProcessingOptions> options,
    ILogger<MediaProcessingWorker> logger) : BackgroundService
{
    internal const int MaxAttempts = 3;

    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // Never delay host startup.
        var concurrency = Math.Clamp(options.Value.MaxConcurrency, 1, 64);
        var workers = Enumerable.Range(0, concurrency).Select(_ => RunWorkerAsync(stoppingToken)).ToArray();
        await RequeuePendingAsync(stoppingToken);
        await Task.WhenAll(workers);
    }

    private async Task RequeuePendingAsync(CancellationToken stoppingToken)
    {
        // The database may still be starting (or, in tests, its schema not created yet): retry for a while.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pending = await db.Media.AsNoTracking()
                    .Where(m => m.Status == MediaStatus.Processing)
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => m.Id)
                    .ToListAsync(stoppingToken);
                foreach (var id in pending) queue.Enqueue(id);
                if (pending.Count > 0) logger.LogInformation("Re-queued {Count} media for processing", pending.Count);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (attempt < 20)
            {
                logger.LogDebug(ex, "Pending media could not be loaded yet (attempt {Attempt})", attempt);
                if (!await DelayAsync(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), stoppingToken)) return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pending media could not be re-queued; they will be picked up on the next restart");
                return;
            }
        }
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var mediaId in queue.Reader.ReadAllAsync(stoppingToken))
                await ProcessOneAsync(mediaId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessOneAsync(Guid mediaId, CancellationToken stoppingToken)
    {
        if (!_inFlight.TryAdd(mediaId, 0)) return;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MediaProcessor>().ProcessAsync(mediaId, stoppingToken);
            _attempts.TryRemove(mediaId, out _);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var attempt = _attempts.AddOrUpdate(mediaId, 1, (_, previous) => previous + 1);
            if (attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "Processing media {MediaId} failed (attempt {Attempt}); retrying", mediaId, attempt);
                _ = RequeueLaterAsync(mediaId, TimeSpan.FromSeconds(15 * attempt), stoppingToken);
            }
            else
            {
                _attempts.TryRemove(mediaId, out _);
                logger.LogError(ex, "Processing media {MediaId} failed {Attempts} times; giving up", mediaId, attempt);
                await TryMarkFailedAsync(mediaId, stoppingToken);
            }
        }
        finally
        {
            _inFlight.TryRemove(mediaId, out _);
        }
    }

    private async Task RequeueLaterAsync(Guid mediaId, TimeSpan delay, CancellationToken stoppingToken)
    {
        if (await DelayAsync(delay, stoppingToken)) queue.Enqueue(mediaId);
    }

    private async Task TryMarkFailedAsync(Guid mediaId, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MediaProcessor>()
                .MarkFailedAsync(mediaId, "Processing failed repeatedly.", stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Media {MediaId} could not be marked as failed; it stays queued for the next restart", mediaId);
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
