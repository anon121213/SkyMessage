using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Services;

/// <summary>A picked photo with its instant local preview (data URL), ready to upload.</summary>
/// <param name="Prepared">Already framed and encoded on the device (the crop editor, an emoji avatar): uploaded as is.</param>
public sealed record PickedPhoto(PickedFile File, string? PreviewUrl, bool Prepared = false)
{
    /// <summary>A square JPEG made on the device (<paramref name="side"/> px), with its data URL as the preview.</summary>
    public static PickedPhoto FromJpeg(byte[] jpeg, int side) => new(new PickedFile
    {
        FileName = "avatar.jpg",
        ContentType = "image/jpeg",
        Size = jpeg.Length,
        OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(jpeg, writable: false)),
    }, "data:image/jpeg;base64," + Convert.ToBase64String(jpeg), Prepared: true)
    {
        Side = side,
    };

    /// <summary>Side in px of a prepared square photo (0 when unknown).</summary>
    public int Side { get; init; }
}

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

    /// <summary>
    /// A copy for the crop editor: re-encoded to a JPEG of at most <paramref name="maxDimension"/> px with the
    /// orientation applied when the device can (also HEIC on Android, which the web view cannot show), else the original.
    /// </summary>
    public async Task<PickedFile> PrepareForEditingAsync(PickedFile file, int maxDimension = 2048, CancellationToken cancellationToken = default)
    {
        if (!compressor.CanCompressImages) return file;
        try
        {
            var media = await compressor.CompressImageAsync(file, new ImageCompressionOptions { MaxDimension = maxDimension, Quality = 92, StripMetadata = true },
                cancellationToken);
            byte[] bytes;
            try
            {
                await using var stream = await media.OpenReadAsync(cancellationToken);
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, cancellationToken);
                bytes = copy.ToArray();
            }
            finally
            {
                DeleteTemp(media.TempFilePath);
            }
            return new PickedFile
            {
                FileName = media.FileName,
                ContentType = media.ContentType,
                Size = bytes.Length,
                OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Photo not prepared for editing; the original is used");
            return file;
        }
    }

    /// <summary>Compresses (when the device can) and uploads; completes when the server reports the photo Ready.</summary>
    public async Task<MediaDto> UploadAsync(PickedPhoto photo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (photo.Prepared)
        {
            return await uploader.UploadAsync(new MediaUploadRequest
            {
                Kind = MediaKind.Photo,
                FileName = photo.File.FileName,
                ContentType = photo.File.ContentType,
                Size = photo.File.Size,
                OpenReadAsync = photo.File.OpenReadAsync,
                Width = photo.Side > 0 ? photo.Side : null,
                Height = photo.Side > 0 ? photo.Side : null,
            }, progress, cancellationToken);
        }
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
            DeleteTemp(media.TempFilePath);
        }
    }

    private void DeleteTemp(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Temporary photo not deleted");
        }
    }
}
