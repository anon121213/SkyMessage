using Android.Content;
using Android.Media;
using Android.Provider;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;
using AndroidEnvironment = Android.OS.Environment;
using Stream = System.IO.Stream;

namespace SkyMessage.App.Droid;

/// <summary>
/// Saves into the public Download/SkyMessage folder and Pictures|Movies/SkyMessage (gallery). Android 10+ uses
/// MediaStore (no permission); Android 8–9 writes the public folders with WRITE_EXTERNAL_STORAGE.
/// </summary>
public sealed class AndroidFileSaveService(IPermissionService permissions, ILogger<AndroidFileSaveService> logger) : IFileSaveService
{
    private const string Folder = "SkyMessage";

    public bool CanSaveToGallery => true;

    public Task<bool> SaveAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(fileName, contentType, content, Target.Downloads, cancellationToken);

    public Task<bool> SaveToGalleryAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
    {
        var target = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? Target.Movies : Target.Pictures;
        return SaveCoreAsync(fileName, contentType, content, target, cancellationToken);
    }

    private async Task<bool> SaveCoreAsync(string fileName, string contentType, Stream content, Target target, CancellationToken cancellationToken)
    {
        var name = FileNames.Sanitize(fileName);
        if (string.IsNullOrEmpty(contentType)) contentType = FileNames.ContentTypeFor(name);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                return await SaveWithMediaStoreAsync(name, contentType, content, target, cancellationToken).ConfigureAwait(false);
            return await SaveLegacyAsync(name, contentType, content, target, cancellationToken).ConfigureAwait(false);
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
    }

    private static async Task<bool> SaveWithMediaStoreAsync(string name, string contentType, Stream content, Target target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return false;
        var resolver = Android.App.Application.Context.ContentResolver!;
        var (collection, relativePath) = target switch
        {
            Target.Pictures => (MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternalPrimary), $"{AndroidEnvironment.DirectoryPictures}/{Folder}"),
            Target.Movies => (MediaStore.Video.Media.GetContentUri(MediaStore.VolumeExternalPrimary), $"{AndroidEnvironment.DirectoryMovies}/{Folder}"),
            _ => (MediaStore.Downloads.GetContentUri(MediaStore.VolumeExternalPrimary), $"{AndroidEnvironment.DirectoryDownloads}/{Folder}"),
        };

        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, name);
        values.Put(MediaStore.IMediaColumns.MimeType, contentType);
        values.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        var item = resolver.Insert(collection!, values) ?? throw new IOException("MediaStore insert failed");
        try
        {
            await using (var output = resolver.OpenOutputStream(item) ?? throw new IOException("MediaStore stream unavailable"))
                await content.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            var done = new ContentValues();
            done.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(item, done, null, null);
            return true;
        }
        catch
        {
            try
            {
                resolver.Delete(item, null, null);
            }
            catch (Exception)
            {
            }
            throw;
        }
    }

    private async Task<bool> SaveLegacyAsync(string name, string contentType, Stream content, Target target, CancellationToken cancellationToken)
    {
        var state = await permissions.RequestAsync(AppPermission.Photos).ConfigureAwait(false);
        if (state is not (PermissionState.Granted or PermissionState.NotApplicable)) return false;

#pragma warning disable CA1422 // Public directories are the API 26–28 way to save.
        var root = AndroidEnvironment.GetExternalStoragePublicDirectory(target switch
        {
            Target.Pictures => AndroidEnvironment.DirectoryPictures,
            Target.Movies => AndroidEnvironment.DirectoryMovies,
            _ => AndroidEnvironment.DirectoryDownloads,
        })!.AbsolutePath;
#pragma warning restore CA1422
        var dir = Path.Combine(root, Folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileNames.Unique(name, candidate => File.Exists(Path.Combine(dir, candidate))));
        await using (var output = File.Create(path))
            await content.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        MediaScannerConnection.ScanFile(Android.App.Application.Context, [path], [contentType], null);
        return true;
    }

    private enum Target
    {
        Downloads,
        Pictures,
        Movies,
    }
}
