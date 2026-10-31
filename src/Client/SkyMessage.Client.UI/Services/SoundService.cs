using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.UI.Services;

public enum ToneKind
{
    Message,
    Ringtone,
    CallProgress,
}

/// <summary>A sound every pack has. <see cref="NameKey"/> is a localization key.</summary>
public sealed record ToneInfo(string Key, string NameKey, ToneKind Kind);

/// <summary>A sound pack. <see cref="NameKey"/> and <see cref="DescriptionKey"/> are localization keys.</summary>
public sealed record SoundPackInfo(string Key, string NameKey, string DescriptionKey);

/// <summary>
/// Our own sounds: three packs (<see cref="SoundCatalog"/>), synthesized from first principles by tools/tones (no
/// samples, no third-party sounds) and rendered to wwwroot/sounds/&lt;pack&gt;/&lt;key&gt;.mp3, which js/sounds.js plays; Android's
/// notification channels use Ogg copies of the same sounds from res/raw. Every pack has the same keys. Singleton, no JS.
/// </summary>
public sealed class ToneCatalog
{
    public const string None = SoundCatalog.None;
    public const string Custom = SoundCatalog.Custom;
    public const string Ringback = SoundCatalog.Ringback;
    public const string Busy = SoundCatalog.Busy;
    public const string CallEnd = SoundCatalog.CallEnd;

    public IReadOnlyList<SoundPackInfo> Packs { get; } =
        SoundCatalog.Packs.Select(p => new SoundPackInfo(p, $"sound.pack.{p}", $"sound.pack.{p}.hint")).ToList();

    public IReadOnlyList<ToneInfo> MessageTones { get; } =
        SoundCatalog.MessageTones.Select(k => new ToneInfo(k, $"sound.tone.{k}", ToneKind.Message)).ToList();

    public IReadOnlyList<ToneInfo> Ringtones { get; } =
        SoundCatalog.Ringtones.Select(k => new ToneInfo(k, $"sound.tone.{k}", ToneKind.Ringtone)).ToList();

    public IReadOnlyList<ToneInfo> CallProgress { get; } =
        SoundCatalog.CallProgress.Select(k => new ToneInfo(k, $"sound.tone.{k}", ToneKind.CallProgress)).ToList();

    public string DefaultPack => SoundCatalog.DefaultPack;

    public string DefaultMessageTone => SoundCatalog.DefaultMessageTone;

    public string DefaultRingtone => SoundCatalog.DefaultRingtone;

    public ToneInfo? Find(string? key) =>
        MessageTones.Concat(Ringtones).Concat(CallProgress).FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    public SoundPackInfo? FindPack(string? key) => Packs.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    public bool IsMessageTone(string? key) => MessageTones.Any(t => t.Key == key);

    public bool IsRingtone(string? key) => Ringtones.Any(t => t.Key == key);
}

/// <summary>
/// In-app sounds, from the pack selected in the settings (Notifications.SoundPack) or the user's own sounds. Only one
/// looping sound (ringtone or ringback) plays at a time; starting another replaces it. Callers decide whether a sound
/// is wanted (settings: Notifications.InAppSounds, Sound, CallsEnabled). Methods never throw. Scoped (JS wrapper over
/// js/sounds.js).
/// </summary>
public interface ISoundService : IAudioDecoder
{
    ToneCatalog Tones { get; }
    /// <summary>One short message tone of the selected pack ("custom" = the user's own; "none" is silent; unknown keys fall back to the default).</summary>
    void PlayMessageTone(string key);
    /// <summary>Loops a ringtone of the selected pack ("custom" = the user's own) until <see cref="Stop"/>.</summary>
    void StartRingtone(string key);
    /// <summary>Loops the outgoing-call ringback until <see cref="Stop"/>.</summary>
    void StartRingback();
    /// <summary>Plays the busy signal once (stops any loop).</summary>
    void PlayBusy();
    /// <summary>Plays the "call ended" tone once (stops any loop).</summary>
    void PlayCallEnd();
    /// <summary>Settings preview in the selected pack: message tones once, ringtones for one cycle ("none" stops).</summary>
    void Preview(string key);
    /// <summary>Settings preview of a pack: its version of the selected message tone.</summary>
    void PreviewPack(string pack);
    /// <summary>Settings preview of the user's own sound for a slot.</summary>
    void PreviewCustom(CustomSoundSlot slot);
    /// <summary>Ends a settings preview (playing or still loading) and nothing else; a ringing call keeps ringing.</summary>
    void StopPreview();
    /// <summary>Stops loops and previews.</summary>
    void Stop();
    /// <summary>
    /// Fetches and decodes what the selected pack plays first (message tone, ringtone, ringback) ahead of its first use,
    /// and again whenever the pack or those tones change; from then on it also lets go of the user's sound for a slot
    /// once it is replaced or removed. The signed-in shell calls it once.
    /// </summary>
    void Preload();
}

public sealed class SoundService(IJSRuntime js, ToneCatalog tones, ISettingsService settings, ICustomSoundStore customSounds,
    ILogger<SoundService> logger) : ISoundService, IAsyncDisposable
{
    public const string ModulePath = "./_content/SkyMessage.Client.UI/js/sounds.js";

    /// <summary>A settings preview of the user's own sound fades out after this long (a ringtone may run a minute).</summary>
    public const double PreviewLimitSeconds = 12;

    private readonly object _gate = new();                               // guards the two maps (settings may change on any thread)
    private readonly Dictionary<CustomSoundSlot, string> _loaded = [];   // slot -> id of the custom sound sounds.js holds
    private readonly Dictionary<CustomSoundSlot, string> _broken = [];   // slot -> id that could not be read or decoded
    private Task<IJSObjectReference>? _module;
    private int _generation;
    private bool _previewing;                                            // the last loop or one-shot asked for is a settings preview
    private bool _watching;
    private string? _preloaded;
    private bool _disposed;

    public ToneCatalog Tones => tones;

    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    private NotificationSettings Notifications => settings.Current.Notifications;

    private string Pack => SoundCatalog.PackOrDefault(Notifications.SoundPack);

    public void PlayMessageTone(string key)
    {
        if (key == ToneCatalog.None) return;
        if (key == ToneCatalog.Custom && Notifications.CustomMessageTone is { } sound)
        {
            _ = PlayCustomAsync(CustomSoundSlot.Message, sound, "play", tones.DefaultMessageTone, cancellable: false);
            return;
        }
        Run("play", tones.IsMessageTone(key) ? key : tones.DefaultMessageTone, Pack);
    }

    public void StartRingtone(string key)
    {
        if (key == ToneCatalog.None) return;
        if (key == ToneCatalog.Custom && Notifications.CustomRingtone is { } sound)
        {
            _generation++;
            _previewing = false;
            _ = PlayCustomAsync(CustomSoundSlot.Ringtone, sound, "startLoop", tones.DefaultRingtone, cancellable: true);
            return;
        }
        Replace("startLoop", false, tones.IsRingtone(key) ? key : tones.DefaultRingtone, Pack);
    }

    public void StartRingback() => Replace("startLoop", false, ToneCatalog.Ringback, Pack);

    public void PlayBusy() => Replace("playOnce", false, ToneCatalog.Busy, Pack);

    public void PlayCallEnd() => Replace("playOnce", false, ToneCatalog.CallEnd, Pack);

    public void Preview(string key)
    {
        if (key == ToneCatalog.None || tones.Find(key) is null)
        {
            Stop();
            return;
        }
        Replace("playOnce", true, key, Pack, 0, true);
    }

    public void PreviewPack(string pack)
    {
        var key = tones.IsMessageTone(Notifications.MessageTone) ? Notifications.MessageTone : tones.DefaultMessageTone;
        Replace("playOnce", true, key, SoundCatalog.PackOrDefault(pack), 0, true);
    }

    public void PreviewCustom(CustomSoundSlot slot)
    {
        Stop();
        var sound = slot == CustomSoundSlot.Message ? Notifications.CustomMessageTone : Notifications.CustomRingtone;
        if (sound is null) return;
        _previewing = true;
        _ = PlayCustomAsync(slot, sound, "playOnce", slot == CustomSoundSlot.Message ? tones.DefaultMessageTone : tones.DefaultRingtone,
            cancellable: true, limitSeconds: PreviewLimitSeconds, preview: true);
    }

    public void StopPreview()
    {
        if (!_previewing) return;
        _generation++;
        _previewing = false;
        Run("stopPreview");
    }

    public void Preload()
    {
        if (_disposed) return;
        if (!_watching)
        {
            _watching = true;
            settings.Changed += OnSettingsChanged;
        }
        PreloadSelected();
    }

    private void OnSettingsChanged()
    {
        if (_disposed) return;
        ForgetReplacedCustom();
        PreloadSelected();
    }

    /// <summary>The pack sounds that may play first; with an own sound selected, the pack's stand-in for it.</summary>
    private void PreloadSelected()
    {
        var n = Notifications;
        var pack = Pack;
        var message = n.MessageTone == ToneCatalog.None ? null : tones.IsMessageTone(n.MessageTone) ? n.MessageTone : tones.DefaultMessageTone;
        var ringtone = tones.IsRingtone(n.Ringtone) ? n.Ringtone : tones.DefaultRingtone;
        string[] keys = message is null ? [ringtone, ToneCatalog.Ringback] : [message, ringtone, ToneCatalog.Ringback];
        var signature = $"{pack}:{string.Join(',', keys)}";
        if (signature == _preloaded) return;
        _preloaded = signature;
        Run("preload", keys, pack);
    }

    /// <summary>sounds.js lets go of the decoded copy of an own sound that was replaced or removed.</summary>
    private void ForgetReplacedCustom()
    {
        foreach (var slot in (CustomSoundSlot[])[CustomSoundSlot.Message, CustomSoundSlot.Ringtone])
        {
            var current = (slot == CustomSoundSlot.Message ? Notifications.CustomMessageTone : Notifications.CustomRingtone)?.Id;
            bool stale;
            lock (_gate)
            {
                stale = _loaded.TryGetValue(slot, out var loaded) && loaded != current;
                if (stale) _loaded.Remove(slot);
                if (_broken.TryGetValue(slot, out var broken) && broken != current) _broken.Remove(slot);
            }
            if (stale) Run("clearCustom", SlotName(slot));
        }
    }

    public async Task<double?> MeasureAsync(byte[] audio, CancellationToken cancellationToken = default)
    {
        if (_disposed || audio.Length == 0) return null;
        try
        {
            var module = await Module;
            return await module.InvokeAsync<double?>("measure", cancellationToken, audio);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Measuring a sound failed");
            return null;
        }
    }

    public void Stop()
    {
        _generation++;
        _previewing = false;
        Run("stop");
    }

    /// <summary>
    /// The user's own sound: hands the file to sounds.js once (by id), then plays it; the pack's
    /// <paramref name="fallbackKey"/> plays when the file is gone or cannot be decoded. A loop or preview that is
    /// still loading when <see cref="Stop"/> is called does not start.
    /// </summary>
    private async Task PlayCustomAsync(CustomSoundSlot slot, CustomSound sound, string function, string fallbackKey, bool cancellable,
        double limitSeconds = 0, bool preview = false)
    {
        var generation = _generation;
        var pack = Pack;
        var ready = await LoadCustomAsync(slot, sound);
        if (_disposed || cancellable && generation != _generation) return;
        await RunAsync(function, [ready ? $"custom:{SlotName(slot)}" : fallbackKey, pack, limitSeconds, preview]);
    }

    private async Task<bool> LoadCustomAsync(CustomSoundSlot slot, CustomSound sound)
    {
        lock (_gate)
        {
            if (_loaded.TryGetValue(slot, out var loaded) && loaded == sound.Id) return true;
            if (_broken.TryGetValue(slot, out var broken) && broken == sound.Id) return false;
        }
        try
        {
            var bytes = await customSounds.ReadAsync(sound);
            double? seconds = null;
            if (bytes is { Length: > 0 })
            {
                var module = await Module;
                seconds = await module.InvokeAsync<double?>("setCustom", SlotName(slot), sound.Id, bytes);
            }
            if (seconds is > 0)
            {
                lock (_gate)
                {
                    _loaded[slot] = sound.Id;
                    _broken.Remove(slot);
                }
                return true;
            }
            logger.LogInformation("The custom {Slot} sound is {State}; the pack's sound plays instead", slot, bytes is null ? "gone" : "not playable");
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Loading the custom {Slot} sound failed", slot);
        }
        lock (_gate)
        {
            _broken[slot] = sound.Id;
            _loaded.Remove(slot);
        }
        return false;
    }

    private static string SlotName(CustomSoundSlot slot) => slot == CustomSoundSlot.Message ? "message" : "ringtone";

    private void Run(string function, params object?[] args) => _ = RunAsync(function, args);

    /// <summary>Starts a loop or a one-shot that replaces the current one (and any custom sound still loading for it).</summary>
    private void Replace(string function, bool preview, params object?[] args)
    {
        _generation++;
        _previewing = preview;
        Run(function, args);
    }

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
        if (_watching) settings.Changed -= OnSettingsChanged;
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
