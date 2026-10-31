using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// <see cref="IAudioFocusCoordinator"/>: decides again (<see cref="AudioFocusPolicy"/>) on every call change, settings
/// change and recording hold, and tells the platform only about changes — under one lock, so the platform sees the
/// decisions in order and a late event never takes focus again after a newer decision gave it back.
/// </summary>
internal sealed class AudioFocusCoordinator : IAudioFocusCoordinator
{
    private readonly IAudioFocusService _platform;
    private readonly ICallService _calls;
    private readonly ISettingsService _settings;
    private readonly ILogger<AudioFocusCoordinator> _logger;
    private readonly object _lock = new();
    private int _recordings;
    private AudioFocusKind _held = AudioFocusKind.None;

    public AudioFocusCoordinator(IAudioFocusService platform, ICallService calls, ISettingsService settings, ILogger<AudioFocusCoordinator> logger)
    {
        _platform = platform;
        _calls = calls;
        _settings = settings;
        _logger = logger;
        calls.Changed += Update;
        settings.Changed += Update;
    }

    public bool IsSupported => _platform.IsSupported;

    /// <summary>What other apps are paused for right now.</summary>
    internal AudioFocusKind Held
    {
        get
        {
            lock (_lock) return _held;
        }
    }

    public IDisposable HoldForRecording()
    {
        lock (_lock) _recordings++;
        Update();
        return new RecordingHold(this);
    }

    private void EndRecording()
    {
        lock (_lock)
        {
            if (_recordings > 0) _recordings--;
        }
        Update();
    }

    private void Update()
    {
        if (!_platform.IsSupported) return;
        lock (_lock)
        {
            var settings = _settings.Current;
            var wanted = AudioFocusPolicy.Decide(settings.Devices.PauseOtherAudio, _calls.Current, _calls.Incoming,
                settings.Notifications.CallsEnabled, _recordings > 0);
            if (wanted == _held) return;
            _held = wanted;
            try
            {
                _platform.SetFocus(wanted);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Changing the audio focus failed");
            }
        }
    }

    private sealed class RecordingHold(AudioFocusCoordinator owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndRecording();
        }
    }
}
