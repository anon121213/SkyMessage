using Foundation;
using Microsoft.Extensions.Logging;
using PhotosUI;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;
using UIKit;
using UniformTypeIdentifiers;

namespace SkyMessage.App.Apple;

/// <summary>
/// Gallery via PHPickerViewController (photos + videos together, no library permission); the picked files are
/// copied inside the completion handler because the system deletes them afterwards. Files via the document
/// picker; capture via the camera (iOS only).
/// </summary>
public sealed class AppleFilePickerService(ILogger<AppleFilePickerService> logger) : IFilePickerService
{
    private const string ImageType = "public.image";
    private const string MovieType = "public.movie";

    public Task<IReadOnlyList<PickedFile>> PickMediaAsync(bool allowMultiple, CancellationToken cancellationToken = default) =>
        PickVisualAsync(allowMultiple ? Limits.MaxAttachmentsPerMessage : 1, includeVideos: true, cancellationToken);

    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync(bool allowMultiple, CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await MainThread.InvokeOnMainThreadAsync(async () => allowMultiple
                ? (await FilePicker.Default.PickMultipleAsync(new PickOptions()))?.ToList() ?? []
                : (await FilePicker.Default.PickAsync(new PickOptions())) is { } single ? [single] : new List<FileResult>()).ConfigureAwait(false);
            return results.Where(r => r is not null && File.Exists(r.FullPath)).Take(Limits.MaxAttachmentsPerMessage).Select(r => ToPickedFile(r.FullPath, r.FileName, r.ContentType)).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Document picker failed");
            return [];
        }
    }

    public async Task<PickedFile?> PickImageAsync(CancellationToken cancellationToken = default) =>
        (await PickVisualAsync(1, includeVideos: false, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    public bool CanCapture
    {
        get
        {
#if IOS
            try
            {
                return UIImagePickerController.IsSourceTypeAvailable(UIImagePickerControllerSourceType.Camera);
            }
            catch (Exception)
            {
                return false;
            }
#else
            return false;
#endif
        }
    }

    public Task<PickedFile?> CapturePhotoAsync(CancellationToken cancellationToken = default) =>
        CaptureAsync(() => MediaPicker.Default.CapturePhotoAsync(), "image/jpeg");

    public Task<PickedFile?> CaptureVideoAsync(CancellationToken cancellationToken = default) =>
        CaptureAsync(() => MediaPicker.Default.CaptureVideoAsync(), "video/quicktime");

    private async Task<PickedFile?> CaptureAsync(Func<Task<FileResult?>> capture, string defaultType)
    {
        if (!CanCapture) return null;
        try
        {
            var result = await MainThread.InvokeOnMainThreadAsync(capture).ConfigureAwait(false);
            if (result is null || !File.Exists(result.FullPath)) return null;
            return ToPickedFile(result.FullPath, result.FileName, string.IsNullOrEmpty(result.ContentType) ? defaultType : result.ContentType);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Camera capture failed");
            return null;
        }
    }

    private async Task<IReadOnlyList<PickedFile>> PickVisualAsync(int limit, bool includeVideos, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<PickedFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var configuration = new PHPickerConfiguration
                {
                    SelectionLimit = limit,
                    Filter = includeVideos
                        ? PHPickerFilter.GetAnyFilterMatchingSubfilters([PHPickerFilter.ImagesFilter, PHPickerFilter.VideosFilter])
                        : PHPickerFilter.ImagesFilter,
                    // Keep the original encoding (no transcoding of videos); compression happens afterwards.
                    PreferredAssetRepresentationMode = PHPickerConfigurationAssetRepresentationMode.Current,
                };
                var picker = new PHPickerViewController(configuration) { Delegate = new PickerDelegate(tcs, logger) };
                var presenter = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController()
                                ?? throw new InvalidOperationException("No view controller to present from");
                presenter.PresentViewController(picker, true, null);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Photo picker failed");
            return [];
        }
        await using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            return await tcs.Task.ConfigureAwait(false);
    }

    internal static PickedFile ToPickedFile(string path, string? fileName, string? contentType)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(path) : fileName;
        return new PickedFile
        {
            FileName = name,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? FileNames.ContentTypeFor(name) : contentType,
            Size = new FileInfo(path).Length,
            LocalPath = path,
            OpenReadAsync = _ => Task.FromResult<Stream>(File.OpenRead(path)),
        };
    }

    private sealed class PickerDelegate(TaskCompletionSource<IReadOnlyList<PickedFile>> tcs, ILogger logger) : PHPickerViewControllerDelegate
    {
        public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
        {
            picker.DismissViewController(true, null);
            if (results.Length == 0)
            {
                tcs.TrySetResult([]);
                return;
            }
            _ = CopyAllAsync(results);
        }

        private async Task CopyAllAsync(PHPickerResult[] results)
        {
            var files = new PickedFile?[results.Length];
            var tasks = results.Select((result, index) => CopyAsync(result.ItemProvider, index, files)).ToArray();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Copying picked media failed");
            }
            tcs.TrySetResult(files.Where(f => f is not null).Select(f => f!).ToList());
        }

        private static Task CopyAsync(NSItemProvider provider, int index, PickedFile?[] files)
        {
            var typeId = provider.HasItemConformingTo(MovieType) ? MovieType
                : provider.HasItemConformingTo(ImageType) ? ImageType
                : null;
            if (typeId is null) return Task.CompletedTask;

            // Use the concrete registered type (e.g. public.heic, com.apple.quicktime-movie) for the best name/extension.
            var concrete = provider.RegisteredTypeIdentifiers?.FirstOrDefault(t => UTType.CreateFromIdentifier(t)?.ConformsTo(UTType.CreateFromIdentifier(typeId)!) == true) ?? typeId;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.LoadFileRepresentation(concrete, (url, error) =>
            {
                try
                {
                    if (url?.Path is { } source && File.Exists(source))
                    {
                        var extension = Path.GetExtension(source);
                        var target = AppPaths.NewTempFile(string.IsNullOrEmpty(extension) ? ".bin" : extension);
                        File.Copy(source, target, overwrite: true);
                        var baseName = string.IsNullOrWhiteSpace(provider.SuggestedName) ? Path.GetFileNameWithoutExtension(source) : provider.SuggestedName;
                        var mime = UTType.CreateFromIdentifier(concrete)?.PreferredMimeType ?? FileNames.ContentTypeFor(source);
                        files[index] = ToPickedFile(target, FileNames.Sanitize(baseName + extension), mime);
                    }
                }
                catch (Exception)
                {
                    // One unreadable item must not cancel the rest.
                }
                finally
                {
                    done.TrySetResult();
                }
            });
            return done.Task;
        }
    }
}
