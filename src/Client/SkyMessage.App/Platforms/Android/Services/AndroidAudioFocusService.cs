using Android.Content;
using Android.Media;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>
/// Transient audio focus (AudioFocusRequest): music and video apps pause while a call rings (ringtone attributes) or
/// runs (voice communication) and while a note is recorded (exclusive, so no notification sound ends up in it), and
/// play on when the focus is abandoned. The WebView takes no audio focus by itself, which is why this exists.
/// Every request gets its own listener: Android tells focus owners apart by their listener, and with a shared one
/// abandoning the old request would drop the new one too. A switch requests the new focus before abandoning the old,
/// so nothing resumes in between. Focus lost to another app (the user starts music, a phone call) is left alone.
/// </summary>
public sealed class AndroidAudioFocusService(ILogger<AndroidAudioFocusService> logger) : IAudioFocusService
{
    private readonly AudioManager? _audio = Android.App.Application.Context.GetSystemService(Context.AudioService) as AudioManager;
    private readonly object _gate = new();
    private AudioFocusKind _kind;
    private AudioFocusRequestClass? _held;

    public bool IsSupported => _audio is not null;

    public void SetFocus(AudioFocusKind kind)
    {
        if (_audio is null) return;
        lock (_gate)
        {
            if (kind == _kind) return;
            _kind = kind;
            var previous = _held;
            _held = null;
            if (kind != AudioFocusKind.None)
            {
                try
                {
                    var request = Build(kind);
                    if (_audio.RequestAudioFocus(request) == AudioFocusRequest.Granted) _held = request;
                    else logger.LogDebug("Audio focus for {Kind} was not granted", kind);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Requesting audio focus failed");
                }
            }
            if (previous is null) return;
            try
            {
                _audio.AbandonAudioFocusRequest(previous);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Abandoning audio focus failed");
            }
        }
    }

    private static AudioFocusRequestClass Build(AudioFocusKind kind)
    {
        var (gain, usage, content) = kind switch
        {
            AudioFocusKind.Ringtone => (AudioFocus.GainTransient, AudioUsageKind.NotificationRingtone, AudioContentType.Sonification),
            AudioFocusKind.Recording => (AudioFocus.GainTransientExclusive, AudioUsageKind.Media, AudioContentType.Speech),
            _ => (AudioFocus.GainTransient, AudioUsageKind.VoiceCommunication, AudioContentType.Speech),
        };
        var attributes = new AudioAttributes.Builder().SetUsage(usage)!.SetContentType(content)!.Build()!;
        return new AudioFocusRequestClass.Builder(gain)
            .SetAudioAttributes(attributes)!
            .SetAcceptsDelayedFocusGain(false)!
            .SetWillPauseWhenDucked(false)!
            .SetOnAudioFocusChangeListener(new FocusListener())!
            .Build()!;
    }

    /// <summary>The focus identity of one request; the changes themselves need no reaction.</summary>
    private sealed class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange)
        {
        }
    }
}
