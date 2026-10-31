using Microsoft.Extensions.DependencyInjection;

namespace SkyMessage.Application.MediaFiles;

/// <summary>Chunked uploads, media access control, metadata. (Namespace avoids clashing with the Media entity.)</summary>
public static class MediaFeature
{
    public static IServiceCollection AddMediaFeature(this IServiceCollection services)
    {
        // Storage (IMediaChunkStore), the processing queue and the background services live in Infrastructure.
        services.AddScoped<IMediaUploadService, MediaUploadService>();
        services.AddScoped<IMediaAccessService, MediaAccessService>();
        services.AddScoped<IMediaContentService, MediaContentService>();
        return services;
    }
}
