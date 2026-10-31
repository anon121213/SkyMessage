using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace SkyMessage.Client.UI.Services;

public enum ToneKind
{
    Message,
    Ringtone,
    CallProgress,
}

/// <summary>A synthesized tone. <see cref="NameKey"/> is a localization key.</summary>
public sealed record ToneInfo(string Key, string NameKey, ToneKind Kind);

/// <summary>
/// Our own tones, synthesized with WebAudio by js/sounds.js (no sound files, no third-party sounds). The same
/// keys are rendered to WAV for OS notification channels by tools/tones/render-tones.cs. Singleton, no JS.
/// </summary>
public sealed class ToneCatalog
{
    public const string None = "none";
    public const string Ringback = "ringback";
    public const string Busy = "busy";
    public const string CallEnd = "end";

    public IReadOnlyList<ToneInfo> MessageTones { get; } =
    [
        new("chime", "sound.tone.chime", ToneKind.Message),
        new("pop", "sound.tone.pop", ToneKind.Message),
        new("glass", "sound.tone.glass", ToneKind.Message),
    ];

    public IReadOnlyList<ToneInfo> Ringtones { get; } =
    [
        new("classic", "sound.tone.classic", ToneKind.Ringtone),
        new("soft", "sound.tone.soft", ToneKind.Ringtone),
        new("bright", "sound.tone.bright", ToneKind.Ringtone),
    ];

    public IReadOnlyList<ToneInfo> CallProgress { get; } =
    [
        new(Ringback, "sound.tone.ringback", ToneKind.CallProgress),
        new(Busy, "sound.tone.busy", ToneKind.CallProgress),
        new(CallEnd, "sound.tone.end", ToneKind.CallProgress),
    ];

    public string DefaultMessageTone => MessageTones[0].Key;

    public string DefaultRingtone => Ringtones[0].Key;

    public ToneInfo? Find(string? key) =>
        MessageTones.Concat(Ringtones).Concat(CallProgress).FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    public bool IsMessageTone(string? key) => MessageTones.Any(t => t.Key == key);

    public bool IsRingtone(string? key) => Ringtones.Any(t => t.Key == key);
}

/// <summary>
/// In-app sounds. Only one looping sound (ringtone or ringback) plays at a time; starting another replaces it.
/// Callers decide whether a sound is wanted (settings: Notifications.InAppSounds, Sound, CallsEnabled).
/// Methods never throw. Scoped (JS wrapper over js/sounds.js).
/// </summary>
public interface ISoundService
{
    ToneCatalog Tones { get; }
    /// <summary>One short message tone (unknown keys fall back to the default; "none" is silent).</summary>
    void PlayMessageTone(string key);
    /// <summary>Loops a ringtone until <see cref="Stop"/>.</summary>
    void StartRingtone(string key);
    /// <summary>Loops the outgoing-call ringback until <see cref="Stop"/>.</summary>
    void StartRingback();
    /// <summary>Plays the busy signal once (stops any loop).</summary>
    void PlayBusy();
    /// <summary>Plays the "call ended" tone once (stops any loop).</summary>
    void PlayCallEnd();
    /// <summary>Settings preview: message tones once, ringtones for one cycle.</summary>
    void Preview(string key);
    /// <summary>Stops loops and previews.</summary>
    void Stop();
}

public sealed class SoundService(IJSRuntime js, ToneCatalog tones, ILogger<SoundService> logger) : ISoundService, IAsyncDisposable
{
    public const string ModulePath = "./_content/SkyMessage.Client.UI/js/sounds.js";

    private Task<IJSObjectReference>? _module;
    private bool _disposed;

    public ToneCatalog Tones => tones;

    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    public void PlayMessageTone(string key)
    {
        if (key == ToneCatalog.None) return;
        Run("play", tones.IsMessageTone(key) ? key : tones.DefaultMessageTone);
    }

    public void StartRingtone(string key)
    {
        if (key == ToneCatalog.None) return;
        Run("startLoop", tones.IsRingtone(key) ? key : tones.DefaultRingtone);
    }

    public void StartRingback() => Run("startLoop", ToneCatalog.Ringback);

    public void PlayBusy() => Run("playOnce", ToneCatalog.Busy);

    public void PlayCallEnd() => Run("playOnce", ToneCatalog.CallEnd);

    public void Preview(string key)
    {
        if (key == ToneCatalog.None || tones.Find(key) is null)
        {
            Stop();
            return;
        }
        Run("playOnce", key);
    }

    public void Stop() => Run("stop");

    private void Run(string function, params object?[] args) => _ = RunAsync(function, args);

    private async Task RunAsync(string function, object?[] args)
    {
        if (_disposed) return;
        try
        {
            var module = await Module;
            await module.InvokeVoidAsync(function, args);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Sound {Function} failed", function);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_module is null) return;
        try
        {
            var module = await _module;
            await module.InvokeVoidAsync("stop");
            await module.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException or JSException)
        {
        }
    }
}
