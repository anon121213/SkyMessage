using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// The user's own message tone and ringtone: picking a file, checking it (<see cref="CustomSoundRules"/>: type and size
/// first, then whether the player decodes it and how long it is), keeping it on this device and selecting it. The
/// file of a sound that is replaced or reset is deleted. Scoped (decoding goes through <see cref="ISoundService"/>).
/// </summary>
public sealed class CustomSoundService(ICustomSoundStore store, ISoundService sounds, ISettingsService settings, ILogger<CustomSoundService> logger)
{
    public bool IsSupported => store.IsSupported;

    /// <summary>
    /// Lets the user pick an audio file for <paramref name="slot"/>; on success it is kept and selected. Returns null
    /// when the picker was cancelled, else <see cref="CustomSoundProblem.None"/> or why the file was refused. Storage
    /// failures throw.
    /// </summary>
    public async Task<CustomSoundProblem?> ImportAsync(CustomSoundSlot slot, CancellationToken cancellationToken = default)
    {
        var file = await store.PickAsync(cancellationToken);
        if (file is null) return null;

        var problem = CustomSoundRules.CheckFile(file.FileName, file.ContentType, file.Size);
        if (problem != CustomSoundProblem.None) return problem;
        var extension = CustomSoundRules.ExtensionOf(file.FileName, file.ContentType)!;

        var bytes = await ReadAsync(file, cancellationToken);
        if (bytes is null) return CustomSoundProblem.TooLarge;
        if (bytes.Length == 0) return CustomSoundProblem.Empty;

        var seconds = await sounds.MeasureAsync(bytes, cancellationToken);
        problem = CustomSoundRules.CheckDuration(slot, seconds);
        if (problem != CustomSoundProblem.None) return problem;

        var sound = CustomSoundRules.Describe(file.FileName, extension, bytes.Length, seconds!.Value);
        await store.SaveAsync(sound, bytes, cancellationToken);
        CustomSound? previous = null;
        await settings.UpdateAsync(s =>
        {
            var n = s.Notifications;
            if (slot == CustomSoundSlot.Message)
            {
                previous = n.CustomMessageTone;
                n.CustomMessageTone = sound;
                n.MessageTone = SoundCatalog.Custom;
            }
            else
            {
                previous = n.CustomRingtone;
                n.CustomRingtone = sound;
                n.Ringtone = SoundCatalog.Custom;
            }
        });
        if (previous is not null && previous.Id != sound.Id) await DeleteQuietlyAsync(previous);
        return CustomSoundProblem.None;
    }

    /// <summary>Forgets the user's sound for <paramref name="slot"/> and goes back to the pack's default tone if it was selected.</summary>
    public async Task ResetAsync(CustomSoundSlot slot)
    {
        CustomSound? previous = null;
        await settings.UpdateAsync(s =>
        {
            var n = s.Notifications;
            if (slot == CustomSoundSlot.Message)
            {
                previous = n.CustomMessageTone;
                n.CustomMessageTone = null;
                if (n.MessageTone == SoundCatalog.Custom) n.MessageTone = SoundCatalog.DefaultMessageTone;
            }
            else
            {
                previous = n.CustomRingtone;
                n.CustomRingtone = null;
                if (n.Ringtone == SoundCatalog.Custom) n.Ringtone = SoundCatalog.DefaultRingtone;
            }
        });
        if (previous is not null) await DeleteQuietlyAsync(previous);
    }

    /// <summary>The file's bytes, or null when it is larger than <see cref="CustomSoundRules.MaxBytes"/> (its size may have been unknown).</summary>
    private static async Task<byte[]?> ReadAsync(PickedFile file, CancellationToken cancellationToken)
    {
        await using var stream = await file.OpenReadAsync(cancellationToken);
        using var buffer = new MemoryStream(file.Size is > 0 and <= CustomSoundRules.MaxBytes ? (int)file.Size : 64 * 1024);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > CustomSoundRules.MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task DeleteQuietlyAsync(CustomSound sound)
    {
        try
        {
            await store.DeleteAsync(sound);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Deleting a replaced custom sound failed");
        }
    }
}
