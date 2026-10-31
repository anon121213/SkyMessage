using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Services;

/// <summary>A picked photo with its instant local preview (data URL), ready to upload.</summary>
public sealed record PickedPhoto(PickedFile File, string? PreviewUrl);

/// <summary>
/// Profile and group photos: pick one image, show an instant preview, compress on the device (metadata
/// stripped) and upload it as a Ready <see cref="MediaKind.Photo"/> owned by the user — then pass the media id to
/// SetAvatar / SetChatAvatar / CreateGroup. Singleton, no JS.
/// </summary>
public sealed class AvatarUploader(IFilePickerService picker, IMediaCompressor compressor, IMediaUploader uploader, ILogger<AvatarUploader> logger)
{
    public const int MaxDimension = 1280;
    public const int Quality = 86;

    /// <summary>Opens the image picker; null when cancelled.</summary>
    public async Task<PickedPhoto?> PickAsync(CancellationToken cancellationToken = default)
    {
        var file = await picker.PickImageAsync(cancellationToken);
        if (file is null) return null;
        string? preview = null;
        try
        {
            var probe = await compressor.ProbeAsync(file, cancellationToken);
            if (probe.PreviewJpeg is { Length: > 0 } jpeg) preview = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Photo preview unavailable");
        }
        return new PickedPhoto(file, preview);
    }

    /// <summary>Compresses (when the device can) and uploads; completes when the server reports the photo Ready.</summary>
    public async Task<MediaDto> UploadAsync(PickedPhoto photo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var media = compressor.CanCompressImages
            ? await compressor.CompressImageAsync(photo.File, new ImageCompressionOptions { MaxDimension = MaxDimension, Quality = Quality, StripMetadata = true }, cancellationToken)
            : await compressor.ProbeAsync(photo.File, cancellationToken);
        try
        {
            return await uploader.UploadAsync(new MediaUploadRequest
            {
                Kind = MediaKind.Photo,
                FileName = media.FileName,
                ContentType = media.ContentType,
                Size = media.Size > 0 ? media.Size : photo.File.Size,
                OpenReadAsync = media.OpenReadAsync,
                Width = media.Width,
                Height = media.Height,
            }, progress, cancellationToken);
        }
        finally
        {
            if (!string.IsNullOrEmpty(media.TempFilePath))
            {
                try
                {
                    File.Delete(media.TempFilePath);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Temporary photo not deleted");
                }
            }
        }
    }
}
