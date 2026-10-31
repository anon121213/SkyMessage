using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.Core.Platform;

/// <summary>
/// The user's own sounds on this device: the system picker for audio files and where the picked files are kept
/// (per device, like the settings that describe them; not part of the offline cache, so clearing it keeps them).
/// MAUI: the app's private files dir, sounds/ (Android shares a file with the notification system through a
/// FileProvider, see NotificationChannels); browser: IndexedDB. Implementations never throw for a missing file.
/// </summary>
public interface ICustomSoundStore
{
    bool IsSupported { get; }

    /// <summary>The system picker, filtered to audio files. Null when cancelled.</summary>
    Task<PickedFile?> PickAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CustomSound sound, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>The file's bytes; null when it is gone.</summary>
    Task<byte[]?> ReadAsync(CustomSound sound, CancellationToken cancellationToken = default);

    Task DeleteAsync(CustomSound sound);
}
