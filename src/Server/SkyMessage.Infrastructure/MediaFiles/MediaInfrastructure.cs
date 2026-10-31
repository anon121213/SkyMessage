using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Infrastructure.MediaFiles.Processing;
using SkyMessage.Infrastructure.MediaFiles.Storage;

namespace SkyMessage.Infrastructure.MediaFiles;

/// <summary>Chunk storage in PostgreSQL (+ encryption at rest), FFmpeg/SkiaSharp processing, cleanup.</summary>
public static class MediaInfrastructure
{
    public static IServiceCollection AddMediaInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Fail fast at startup: a wrong master key must never silently disable encryption or break uploads later.
        services.AddOptions<StorageOptions>()
            .Validate(o => MediaEncryption.IsValidMasterKey(o.MasterKey),
                "Storage:MasterKey must be the base64 encoding of exactly 32 bytes (or empty to store media unencrypted).")
            .Validate(o => o.ChunkSize is >= MediaUploadLimits.MinChunkSize and <= MediaUploadLimits.MaxChunkSize,
                $"Storage:ChunkSize must be between {MediaUploadLimits.MinChunkSize} and {MediaUploadLimits.MaxChunkSize} bytes.")
            .Validate(o => o.MaxUploadBytes > 0, "Storage:MaxUploadBytes must be positive.")
            .Validate(o => o.IncompleteUploadTtlHours > 0, "Storage:IncompleteUploadTtlHours must be positive.")
            .ValidateOnStart();
        services.AddOptions<MediaProcessingOptions>()
            .Validate(o => o.MaxConcurrency is >= 1 and <= 64, "MediaProcessing:MaxConcurrency must be between 1 and 64.")
            .Validate(o => o.PhotoMaxDimension >= 64 && o.ThumbnailMaxDimension >= 32 && o.VideoMaxDimension >= 64 && o.VideoNoteSize >= 64,
                "MediaProcessing dimensions are too small.")
            .Validate(o => o.VideoCrf is >= 0 and <= 51, "MediaProcessing:VideoCrf must be between 0 and 51.")
            .ValidateOnStart();

        services.AddSingleton<MediaEncryption>();
        services.AddScoped<PostgresMediaChunkStore>();
        services.AddScoped<IMediaChunkStore>(sp => sp.GetRequiredService<PostgresMediaChunkStore>());

        services.AddSingleton<MediaTempStorage>();
        services.AddSingleton<FfmpegRunner>();
        services.AddSingleton<ImageProcessor>();
        services.AddSingleton<HeifConverter>();
        services.AddSingleton<VideoProcessor>();
        services.AddSingleton<AudioProcessor>();
        services.AddScoped<MediaProcessor>();

        services.AddSingleton<MediaProcessingQueue>();
        services.AddSingleton<IMediaProcessingQueue>(sp => sp.GetRequiredService<MediaProcessingQueue>());
        services.AddHostedService<MediaProcessingWorker>();
        services.AddHostedService<MediaCleanupService>();
        return services;
    }
}
