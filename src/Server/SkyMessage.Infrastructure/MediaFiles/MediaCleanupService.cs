using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using SkyMessage.Infrastructure.Persistence;

namespace SkyMessage.Infrastructure.MediaFiles;

internal sealed record MediaCleanupReport(int StaleUploads, int FailedMedia, int UnreferencedMedia, int TempDirectories)
{
    public bool IsEmpty => StaleUploads + FailedMedia + UnreferencedMedia + TempDirectories == 0;
}

/// <summary>
/// Hourly: deletes uploads still Uploading after <c>Storage:IncompleteUploadTtlHours</c>, Failed media older than the
/// same TTL (they can never be attached), Ready media older than 7 days that nothing references (no message
/// attachment, not a current user or chat avatar) and orphaned processing temp directories. Chunks and thumbnails go
/// with the media row (ON DELETE CASCADE). The reference checks are repeated inside each DELETE and attachments
/// reference media with ON DELETE RESTRICT, so a media attached concurrently is never lost.
/// </summary>
internal sealed class MediaCleanupService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<StorageOptions> storage,
    MediaTempStorage temp,
    ILogger<MediaCleanupService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan UnreferencedRetention = TimeSpan.FromDays(7);
    internal static readonly TimeSpan TempRetention = TimeSpan.FromDays(1);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    var report = await RunOnceAsync(stoppingToken);
                    if (!report.IsEmpty)
                        logger.LogInformation(
                            "Media cleanup removed {StaleUploads} stale uploads, {FailedMedia} failed and {UnreferencedMedia} unreferenced media, {TempDirectories} temp directories",
                            report.StaleUploads, report.FailedMedia, report.UnreferencedMedia, report.TempDirectories);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Media cleanup failed; retrying in {Interval}", Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<MediaCleanupReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var ttl = TimeSpan.FromHours(Math.Max(1, storage.Value.IncompleteUploadTtlHours));
        var staleCutoff = now - ttl;
        var unreferencedCutoff = now - UnreferencedRetention;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var staleUploads = await DeleteInBatchesAsync(db,
            media => media.Where(m => m.Status == MediaStatus.Uploading && m.CreatedAt < staleCutoff), cancellationToken);
        var failed = await DeleteInBatchesAsync(db,
            media => media.Where(m => m.Status == MediaStatus.Failed && m.UpdatedAt < staleCutoff), cancellationToken);
        var unreferenced = await DeleteInBatchesAsync(db,
            media => media.Where(m => m.Status == MediaStatus.Ready
                                      && m.CreatedAt < unreferencedCutoff
                                      && !db.MessageAttachments.Any(a => a.MediaId == m.Id)
                                      && !db.Users.Any(u => u.AvatarMediaId == m.Id)
                                      && !db.Chats.Any(c => c.AvatarMediaId == m.Id)),
            cancellationToken);
        var tempDirectories = temp.DeleteStaleJobs(TempRetention, now);

        return new MediaCleanupReport(staleUploads, failed, unreferenced, tempDirectories);
    }

    private async Task<int> DeleteInBatchesAsync(AppDbContext db, Func<IQueryable<Media>, IQueryable<Media>> filter, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var ids = await filter(db.Media.AsNoTracking())
                .OrderBy(m => m.CreatedAt)
                .Select(m => m.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0) return total;

            int deleted;
            try
            {
                // The filter is applied again so rows that changed since the SELECT are left alone.
                deleted = await filter(db.Media.Where(m => ids.Contains(m.Id))).ExecuteDeleteAsync(cancellationToken);
            }
            catch (Exception ex) when (IsForeignKeyViolation(ex))
            {
                deleted = await DeleteOneByOneAsync(db, filter, ids, cancellationToken);
            }

            total += deleted;
            if (ids.Count < BatchSize || deleted == 0) return total;
        }
    }

    /// <summary>A batch hit a row that was just attached to a message: delete the rest individually.</summary>
    private async Task<int> DeleteOneByOneAsync(AppDbContext db, Func<IQueryable<Media>, IQueryable<Media>> filter, List<Guid> ids,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var id in ids)
        {
            try
            {
                deleted += await filter(db.Media.Where(m => m.Id == id)).ExecuteDeleteAsync(cancellationToken);
            }
            catch (Exception ex) when (IsForeignKeyViolation(ex))
            {
                logger.LogDebug("Media {MediaId} became referenced during cleanup; kept", id);
            }
        }
        return deleted;
    }

    private static bool IsForeignKeyViolation(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } => true,
        DbUpdateException { InnerException: { } inner } => IsForeignKeyViolation(inner),
        InvalidOperationException { InnerException: { } inner } => IsForeignKeyViolation(inner),
        _ => false,
    };
}
