using Android.Content.PM;
using Android.Runtime;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;
using AndroidUri = Android.Net.Uri;

namespace SkyMessage.App.Droid;

/// <summary>
/// Gallery: the system Photo Picker with mixed photo + video selection (no storage permission). Files: the
/// document picker. Both return content:// URIs which are read in place (persistable grant taken when
/// offered) instead of being copied. Capture uses the camera app through MAUI's media picker.
/// </summary>
public sealed class AndroidFilePickerService(ILogger<AndroidFilePickerService> logger) : IFilePickerService
{
    public async Task<IReadOnlyList<PickedFile>> PickMediaAsync(bool allowMultiple, CancellationToken cancellationToken = default)
    {
        try
        {
            var mediaType = ActivityResultContracts.PickVisualMedia.ImageAndVideo.Instance!;
            if (allowMultiple)
            {
                var request = new PickVisualMediaRequest.Builder()
                    .SetMediaType(mediaType)
                    .SetMaxItems(Limits.MaxAttachmentsPerMessage)
                    .Build();
                var result = await ActivityResults.LaunchAsync(
                    new ActivityResultContracts.PickMultipleVisualMedia(Limits.MaxAttachmentsPerMessage), request, cancellationToken).ConfigureAwait(false);
                return ToFiles(UrisOf(result), "media");
            }
            var single = new PickVisualMediaRequest.Builder().SetMediaType(mediaType).Build();
            var picked = await ActivityResults.LaunchAsync(new ActivityResultContracts.PickVisualMedia(), single, cancellationToken).ConfigureAwait(false);
            return ToFiles(UrisOf(picked), "media");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Photo picker failed");
            return [];
        }
    }

    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync(bool allowMultiple, CancellationToken cancellationToken = default)
    {
        try
        {
            var mimeTypes = JavaStrings("*/*");
            ActivityResultContract contract = allowMultiple
                ? new ActivityResultContracts.OpenMultipleDocuments()
                : new ActivityResultContracts.OpenDocument();
            var result = await ActivityResults.LaunchAsync(contract, mimeTypes, cancellationToken).ConfigureAwait(false);
            return ToFiles(UrisOf(result), "file");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Document picker failed");
            return [];
        }
    }

    public async Task<PickedFile?> PickImageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new PickVisualMediaRequest.Builder()
                .SetMediaType(ActivityResultContracts.PickVisualMedia.ImageOnly.Instance!)
                .Build();
            var result = await ActivityResults.LaunchAsync(new ActivityResultContracts.PickVisualMedia(), request, cancellationToken).ConfigureAwait(false);
            return ToFiles(UrisOf(result), "photo").FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Image picker failed");
            return null;
        }
    }

    public bool CanCapture
    {
        get
        {
            try
            {
                return Android.App.Application.Context.PackageManager?.HasSystemFeature(PackageManager.FeatureCameraAny) == true
                       && MediaPicker.Default.IsCaptureSupported;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task<PickedFile?> CapturePhotoAsync(CancellationToken cancellationToken = default) =>
        CaptureAsync(() => MediaPicker.Default.CapturePhotoAsync(), "image/jpeg");

    public Task<PickedFile?> CaptureVideoAsync(CancellationToken cancellationToken = default) =>
        CaptureAsync(() => MediaPicker.Default.CaptureVideoAsync(), "video/mp4");

    private async Task<PickedFile?> CaptureAsync(Func<Task<FileResult?>> capture, string defaultType)
    {
        try
        {
            var result = await MainThread.InvokeOnMainThreadAsync(capture).ConfigureAwait(false);
            if (result is null || string.IsNullOrEmpty(result.FullPath) || !File.Exists(result.FullPath)) return null;
            var path = result.FullPath;
            var contentType = string.IsNullOrEmpty(result.ContentType) ? defaultType : result.ContentType;
            return new PickedFile
            {
                FileName = string.IsNullOrEmpty(result.FileName) ? Path.GetFileName(path) : result.FileName,
                ContentType = contentType,
                Size = new FileInfo(path).Length,
                LocalPath = path,
                OpenReadAsync = _ => Task.FromResult<Stream>(File.OpenRead(path)),
            };
        }
        catch (PermissionException ex)
        {
            // Camera (or, below Android 13, storage) access refused: the composer shows the permission error.
            logger.LogInformation(ex, "Camera permission denied");
            throw new ApiException(403, ClientErrorCodes.PermissionDenied, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Camera capture failed");
            return null;
        }
    }

    private static List<AndroidUri> UrisOf(Java.Lang.Object? result)
    {
        var uris = new List<AndroidUri>();
        if (result is null) return uris;
        if (result is AndroidUri direct)
        {
            uris.Add(direct);
            return uris;
        }
        try
        {
            var list = result.JavaCast<Java.Util.IList>();
            for (var i = 0; i < list.Size(); i++)
                if (list.Get(i)?.JavaCast<AndroidUri>() is { } uri) uris.Add(uri);
        }
        catch (InvalidCastException)
        {
            if (result.JavaCast<AndroidUri>() is { } uri) uris.Add(uri);
        }
        return uris;
    }

    private static IReadOnlyList<PickedFile> ToFiles(List<AndroidUri> uris, string fallbackName)
    {
        var files = new List<PickedFile>(Math.Min(uris.Count, Limits.MaxAttachmentsPerMessage));
        foreach (var uri in uris.Take(Limits.MaxAttachmentsPerMessage))
        {
            ContentUriHelper.TryPersistReadGrant(uri);
            var (name, size, type) = ContentUriHelper.Describe(uri, fallbackName);
            var text = uri.ToString()!;
            files.Add(new PickedFile
            {
                FileName = name,
                ContentType = type,
                Size = size,
                PlatformRef = text,
                OpenReadAsync = _ => Task.FromResult(ContentUriHelper.OpenRead(text)),
            });
        }
        return files;
    }

    private static Java.Lang.Object JavaStrings(params string[] values)
    {
        var handle = JNIEnv.NewArray(values);
        return Java.Lang.Object.GetObject<Java.Lang.Object>(handle, JniHandleOwnership.TransferLocalRef)!;
    }
}
