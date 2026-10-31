using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Web.Platform;

/// <summary>Hidden &lt;input type=file&gt; (gallery = image/* + video/*, camera via the capture attribute on phones).</summary>
public sealed class WebFilePickerService(WebBridge bridge, WebBlobs blobs, ILogger<WebFilePickerService> logger) : IFilePickerService
{
    public Task<IReadOnlyList<PickedFile>> PickMediaAsync(bool allowMultiple, CancellationToken cancellationToken = default) =>
        PickAsync("image/*,video/*", allowMultiple, null, cancellationToken);

    public Task<IReadOnlyList<PickedFile>> PickFilesAsync(bool allowMultiple, CancellationToken cancellationToken = default) =>
        PickAsync("", allowMultiple, null, cancellationToken);

    public async Task<PickedFile?> PickImageAsync(CancellationToken cancellationToken = default) =>
        (await PickAsync("image/*", false, null, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    public bool CanCapture => bridge.State.IsMobile;

    public async Task<PickedFile?> CapturePhotoAsync(CancellationToken cancellationToken = default) =>
        (await PickAsync("image/*", false, "environment", cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    public async Task<PickedFile?> CaptureVideoAsync(CancellationToken cancellationToken = default) =>
        (await PickAsync("video/*", false, "environment", cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    private async Task<IReadOnlyList<PickedFile>> PickAsync(string accept, bool multiple, string? capture, CancellationToken cancellationToken)
    {
        try
        {
            var picked = await bridge.Js.InvokeAsync<PickedBlob[]>("skyPlatform.files.pick", cancellationToken, accept, multiple, capture).ConfigureAwait(false);
            return picked.Take(Limits.MaxAttachmentsPerMessage).Select(p => blobs.ToPickedFile(p.Ref, p.Name, p.Type, p.Size)).ToList();
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "File picker failed");
            return [];
        }
    }

    private sealed record PickedBlob(string Ref, string Name, string Type, long Size);
}

/// <summary>Canvas image compression (EXIF orientation applied by the browser, metadata dropped); no video transcoding.</summary>
public sealed class WebMediaCompressor(WebBridge bridge, WebBlobs blobs, ILogger<WebMediaCompressor> logger) : IMediaCompressor
{
    public bool CanCompressImages => true;

    public bool CanCompressVideos => false;

    public async Task<CompressedMedia> ProbeAsync(PickedFile file, CancellationToken cancellationToken = default)
    {
        if (!file.IsImage && !file.IsVideo) return Original(file, null);
        try
        {
            var reference = await blobs.RegisterAsync(file, cancellationToken).ConfigureAwait(false);
            var probe = await bridge.Js.InvokeAsync<Probe?>("skyPlatform.media.probe", cancellationToken, reference).ConfigureAwait(false);
            return Original(file, probe);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Probe failed");
            return Original(file, null);
        }
    }

    public async Task<CompressedMedia> CompressImageAsync(PickedFile file, ImageCompressionOptions options, CancellationToken cancellationToken = default)
    {
        var type = file.ContentType.ToLowerInvariant();
        var probe = await ProbeAsync(file, cancellationToken).ConfigureAwait(false);
        if (type == "image/gif") return probe;
        var longSide = Math.Max(probe.Width ?? 0, probe.Height ?? 0);
        var fits = longSide > 0 && longSide <= options.MaxDimension;
        var lossless = type is "image/png" or "image/webp";
        if (fits && lossless) return probe;
        if (fits && !options.StripMetadata && type == "image/jpeg") return probe;

        try
        {
            var reference = await blobs.RegisterAsync(file, cancellationToken).ConfigureAwait(false);
            var result = await bridge.Js.InvokeAsync<Compressed?>("skyPlatform.media.compressImage", cancellationToken,
                reference, options.MaxDimension, Math.Clamp(options.Quality, 1, 100), lossless).ConfigureAwait(false);
            if (result is null) return probe;
            var extension = result.Type == "image/png" ? ".png" : ".jpg";
            return new CompressedMedia
            {
                FileName = Path.GetFileNameWithoutExtension(file.FileName) is { Length: > 0 } baseName ? baseName + extension : "photo" + extension,
                ContentType = result.Type,
                Size = result.Size,
                Width = result.Width,
                Height = result.Height,
                PreviewJpeg = Decode(result.Preview) ?? probe.PreviewJpeg,
                OpenReadAsync = ct => blobs.OpenAsync(result.Ref, ct),
            };
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Image compression failed; sending the original");
            return probe;
        }
    }

    public Task<CompressedMedia> CompressVideoAsync(PickedFile file, VideoCompressionOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        ProbeAsync(file, cancellationToken);

    private static CompressedMedia Original(PickedFile file, Probe? probe) => new()
    {
        FileName = file.FileName,
        ContentType = file.ContentType,
        Size = file.Size,
        Width = probe?.Width,
        Height = probe?.Height,
        DurationMs = probe?.DurationMs,
        PreviewJpeg = Decode(probe?.Preview),
        OpenReadAsync = file.OpenReadAsync,
    };

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record Probe(int? Width, int? Height, int? DurationMs, string? Preview);

    private sealed record Compressed(string Ref, string Type, long Size, int Width, int Height, string? Preview);
}
