using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.App.Services;

/// <summary>
/// The user's own sounds as files in the app's private data dir (<see cref="AppPaths.Sounds"/>; allowBackup=false),
/// picked with the system picker filtered to audio files. Android: NotificationChannels hands a file to the
/// notification system through a FileProvider. Apple: in-app playback only (iOS notification sounds must be CAF/WAV
/// in the bundle or Library/Sounds, so notifications keep the pack's sound).
/// </summary>
public sealed class CustomSoundStore(ILogger<CustomSoundStore> logger) : ICustomSoundStore
{
    private static readonly FilePickerFileType AudioFiles = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.Android] = CustomSoundRules.PickerContentTypes,
        [DevicePlatform.iOS] = ["public.audio"],
        [DevicePlatform.MacCatalyst] = ["public.audio"],
    });

    public bool IsSupported => true;

    public static string PathOf(CustomSound sound) => Path.Combine(AppPaths.Sounds, sound.FileName);

    public async Task<PickedFile?> PickAsync(CancellationToken cancellationToken = default)
    {
        FileResult? result;
        try
        {
            result = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickAsync(new PickOptions { FileTypes = AudioFiles }))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Audio file picker failed");
            return null;
        }
        if (result is null) return null;

        long size = -1;
        try
        {
            if (File.Exists(result.FullPath)) size = new FileInfo(result.FullPath).Length;
        }
        catch (Exception)
        {
            // Unknown size: the bytes are checked while reading.
        }
        return new PickedFile
        {
            FileName = result.FileName,
            ContentType = result.ContentType ?? "",
            Size = size,
            LocalPath = result.FullPath,
            OpenReadAsync = _ => result.OpenReadAsync(),
        };
    }

    public async Task SaveAsync(CustomSound sound, byte[] content, CancellationToken cancellationToken = default)
    {
        var path = PathOf(sound);
        var partial = path + ".part";
        await File.WriteAllBytesAsync(partial, content, cancellationToken).ConfigureAwait(false);
        File.Move(partial, path, overwrite: true);
    }

    public async Task<byte[]?> ReadAsync(CustomSound sound, CancellationToken cancellationToken = default)
    {
        var path = PathOf(sound);
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not read a custom sound");
            return null;
        }
    }

    /// <summary>
    /// Android: the media framework that plays notification channel sounds (SystemSoundCheck) opens a copy of the file.
    /// Apple: notifications keep the pack's sound, so nothing outside the app plays the file.
    /// </summary>
    public Task<bool> IsSystemPlayableAsync(byte[] content, string extension, CancellationToken cancellationToken = default) =>
#if ANDROID
        CheckWithAndroidAsync(content, extension, cancellationToken);
#else
        Task.FromResult(true);
#endif

#if ANDROID
    private static async Task<bool> CheckWithAndroidAsync(byte[] content, string extension, CancellationToken cancellationToken)
    {
        var path = AppPaths.NewTempFile(extension);
        try
        {
            await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => SkyMessage.App.Droid.SystemSoundCheck.CanPlay(path), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // The temp folder is cleaned up with the cache.
            }
        }
    }
#endif

    public Task DeleteAsync(CustomSound sound)
    {
        try
        {
            File.Delete(PathOf(sound));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete a custom sound");
        }
        return Task.CompletedTask;
    }
}
