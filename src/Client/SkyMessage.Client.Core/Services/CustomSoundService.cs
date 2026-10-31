using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.Core.Services;

/// <summary>The in-app player as a decoder (the UI's ISoundService): a file's length in seconds when it decodes, else null.</summary>
public interface IAudioDecoder
{
    /// <summary>Decodes a file the way the player would: its length in seconds, or null when it cannot be played.</summary>
    Task<double?> MeasureAsync(byte[] audio, CancellationToken cancellationToken = default);
}

/// <summary>
/// The user's own message tone and ringtone: picking a file, checking it (<see cref="CustomSoundRules"/>), keeping it on
/// this device and selecting it. The checks run cheapest first: type and size before the file is read; container and
/// length from its headers before it is decoded (a decoder turns the whole file into raw samples at once, so an hour of
/// low-bitrate audio must never get that far); the system player where it rings notifications; then the decode, for
/// the exact length. The file of a sound that is replaced or reset is deleted. Scoped (decoding goes through the UI's
/// player).
/// </summary>
public sealed class CustomSoundService(ICustomSoundStore store, IAudioDecoder decoder, ISettingsService settings, ILogger<CustomSoundService> logger)
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

        var info = AudioFileProbe.Probe(bytes);
        problem = CustomSoundRules.CheckHeaders(slot, info);
        if (problem != CustomSoundProblem.None)
        {
            logger.LogInformation("Custom sound refused before decoding: {Problem} ({Container}, {Codec}, {Seconds} s)",
                problem, info.Container, info.Codec, info.Seconds);
            return problem;
        }
        if (!await store.IsSystemPlayableAsync(bytes, extension, cancellationToken))
        {
            logger.LogInformation("Custom sound refused: the system player cannot play it ({Container}, {Codec})", info.Container, info.Codec);
            return CustomSoundProblem.SystemCannotPlay;
        }

        var seconds = await decoder.MeasureAsync(bytes, cancellationToken);
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
