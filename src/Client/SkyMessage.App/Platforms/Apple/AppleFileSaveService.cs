using Foundation;
using Microsoft.Extensions.Logging;
using Photos;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using UIKit;

namespace SkyMessage.App.Apple;

/// <summary>
/// iOS: the system "save to Files" sheet (document picker in export mode) and the photo library (add-only).
/// Mac: straight into ~/Downloads (sandbox downloads entitlement), falling back to the save sheet.
/// </summary>
public sealed class AppleFileSaveService(IPermissionService permissions, ILogger<AppleFileSaveService> logger) : IFileSaveService
{
#if IOS
    public bool CanSaveToGallery => true;
#else
    public bool CanSaveToGallery => false;
#endif

    public async Task<bool> SaveAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
    {
        var name = FileNames.Sanitize(fileName);
        string? staged = null;
        try
        {
#if MACCATALYST
            var downloads = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.DownloadsDirectory, NSSearchPathDomain.User).FirstOrDefault()?.Path;
            if (!string.IsNullOrEmpty(downloads))
            {
                try
                {
                    Directory.CreateDirectory(downloads);
                    var target = Path.Combine(downloads, FileNames.Unique(name, n => File.Exists(Path.Combine(downloads, n))));
                    await using (var output = File.Create(target))
                        await content.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger.LogInformation(ex, "Downloads folder not writable; asking where to save");
                    if (content.CanSeek) content.Position = 0;
                    else throw;
                }
            }
#endif
            staged = await StageAsync(name, content, cancellationToken).ConfigureAwait(false);
            return await ExportAsync(staged).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Saving a file failed");
            return false;
        }
        finally
        {
            if (staged is not null) TryDeleteDir(Path.GetDirectoryName(staged));
        }
    }

    public async Task<bool> SaveToGalleryAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
    {
#if IOS
        string? staged = null;
        try
        {
            var state = await permissions.RequestAsync(AppPermission.Photos).ConfigureAwait(false);
            if (state is not (PermissionState.Granted or PermissionState.Limited)) return false;
            staged = await StageAsync(FileNames.Sanitize(fileName), content, cancellationToken).ConfigureAwait(false);
            var url = NSUrl.FromFilename(staged);
            var isVideo = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(() =>
            {
                if (isVideo) PHAssetChangeRequest.FromVideo(url);
                else PHAssetChangeRequest.FromImage(url);
            }, (success, error) =>
            {
                if (!success) logger.LogWarning("Saving to the photo library failed: {Error}", error?.LocalizedDescription);
                done.TrySetResult(success);
            });
            return await done.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Saving to the photo library failed");
            return false;
        }
        finally
        {
            if (staged is not null) TryDeleteDir(Path.GetDirectoryName(staged));
        }
#else
        // No photo-library entitlement on the Mac: the file goes to Downloads like any other save.
        _ = permissions;
        return await SaveAsync(fileName, contentType, content, cancellationToken).ConfigureAwait(false);
#endif
    }

    /// <summary>Writes the content to temp/{random}/{name} so the sheet shows the real file name.</summary>
    private static async Task<string> StageAsync(string name, Stream content, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(AppPaths.Temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        await using (var output = File.Create(path))
            await content.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static Task<bool> ExportAsync(string path)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var picker = new UIDocumentPickerViewController([NSUrl.FromFilename(path)], asCopy: true)
                {
                    Delegate = new ExportDelegate(tcs),
                    ModalPresentationStyle = UIModalPresentationStyle.FormSheet,
                };
                var presenter = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController();
                if (presenter is null)
                {
                    tcs.TrySetResult(false);
                    return;
                }
                presenter.PresentViewController(picker, true, null);
            }
            catch (Exception)
            {
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private static void TryDeleteDir(string? dir)
    {
        try
        {
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private sealed class ExportDelegate(TaskCompletionSource<bool> tcs) : UIDocumentPickerDelegate
    {
        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls) => tcs.TrySetResult(urls.Length > 0);

        public override void WasCancelled(UIDocumentPickerViewController controller) => tcs.TrySetResult(false);
    }
}
