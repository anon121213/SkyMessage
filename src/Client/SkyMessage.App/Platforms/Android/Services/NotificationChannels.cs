using Android.App;
using Android.Content;
using Android.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using AndroidUri = Android.Net.Uri;

namespace SkyMessage.App.Droid;

/// <summary>
/// Notification channels. A channel's sound/vibration cannot change after creation, so there is one channel per
/// (sound, vibration) choice (<see cref="SoundChoice.Id"/>: pack and key, or the id of the user's own file); when the
/// user picks another sound, pack or file the matching channel is created and the stale ones are deleted. Pack sounds
/// are our synthesized Ogg Vorbis files in res/raw (tone_&lt;pack&gt;_&lt;key&gt;); the user's own sound is shared with the
/// notification system through the FileProvider declared in AndroidManifest.xml (a read grant to the packages that
/// play notification sounds, renewed whenever the channel is used, since grants do not survive a reboot). When that
/// file is gone, or the system's media framework cannot play it (<see cref="SystemSoundCheck"/>), the pack's sound
/// stands in.
/// </summary>
public sealed class NotificationChannels(IServiceProvider services, NativeStrings strings, ILogger<NotificationChannels> logger)
{
    public const string OngoingCall = "call.ongoing.v1";
    public const string Background = "service.connection.v1";
    public const string MessagesSilent = "msg.silent.v1";

    /// <summary>Matches the &lt;provider&gt; in AndroidManifest.xml (the application id is fixed: com.skymessage.messenger).</summary>
    public const string SoundAuthority = "com.skymessage.messenger.sounds";

    private const string GroupMessages = "messages";
    private const string GroupCalls = "calls";
    private const string GroupService = "service";

    /// <summary>
    /// Who opens a channel's sound: SystemUI's ringtone player (Android 8+) and the system server. Since Android 9 the
    /// system also grants itself the sound of a posted notification, as long as the app may grant it.
    /// </summary>
    private static readonly string[] SoundPlayers = ["com.android.systemui", "android"];

    private static readonly long[] MessageVibration = [0, 180, 120, 180];
    private static readonly long[] CallVibration = [0, 900, 700, 900, 700, 900, 700];

    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _playable = new(StringComparer.Ordinal);   // custom sound id -> the system plays it
    private string? _language;

    private NotificationManager Manager => (NotificationManager)Android.App.Application.Context.GetSystemService(Context.NotificationService)!;

    private NotificationSettings Settings => services.GetService<ISettingsService>()?.Current.Notifications ?? new NotificationSettings();

    /// <summary>Channel for a message notification under the current settings.</summary>
    public string Messages(bool silent)
    {
        EnsureBase();
        var settings = Settings;
        if (silent || !settings.Sound && !settings.Vibrate) return MessagesSilent;
        lock (_gate)
        {
            var (sound, uri) = Resolve(settings.Sound ? SoundCatalog.MessageSound(settings) : null);
            var id = $"msg.{sound?.Id ?? SoundCatalog.None}.{(settings.Vibrate ? "v" : "n")}.v2";
            var name = $"{strings["platform.channel.messages"]} · {SoundName(sound)}{(settings.Vibrate ? ", " + strings["platform.channel.withVibration"] : "")}";
            var channel = new NotificationChannel(id, name, NotificationImportance.High)
            {
                Description = strings["platform.channel.messages.description"],
                Group = GroupMessages,
                LockscreenVisibility = NotificationVisibility.Private,
            };
            channel.SetShowBadge(true);
            channel.EnableLights(true);
            channel.LightColor = unchecked((int)0xFF6A4BFF);
            ApplySound(channel, uri, AudioUsageKind.Notification);
            channel.EnableVibration(settings.Vibrate);
            if (settings.Vibrate) channel.SetVibrationPattern(MessageVibration);
            Create(channel);
            DeleteStale("msg.", id, MessagesSilent);
            return id;
        }
    }

    /// <summary>Ringing channel for incoming calls under the current settings.</summary>
    public string IncomingCall()
    {
        EnsureBase();
        var settings = Settings;
        lock (_gate)
        {
            var (sound, uri) = Resolve(settings.Sound ? SoundCatalog.RingtoneSound(settings) : null);
            var id = $"call.{sound?.Id ?? SoundCatalog.None}.{(settings.Vibrate ? "v" : "n")}.v2";
            var channel = new NotificationChannel(id, $"{strings["platform.channel.calls"]} · {SoundName(sound)}", NotificationImportance.High)
            {
                Description = strings["platform.channel.calls.description"],
                Group = GroupCalls,
                LockscreenVisibility = NotificationVisibility.Public,
            };
            channel.SetShowBadge(false);
            ApplySound(channel, uri, AudioUsageKind.NotificationRingtone);
            channel.EnableVibration(settings.Vibrate);
            if (settings.Vibrate) channel.SetVibrationPattern(CallVibration);
            Create(channel);
            DeleteStale("call.", id, OngoingCall);
            return id;
        }
    }

    /// <summary>Static channels (ongoing call, background connection) and groups; refreshes names after a language change.</summary>
    public void EnsureBase()
    {
        var language = strings.Language;
        lock (_gate)
        {
            if (_language == language) return;
            _language = language;
            try
            {
                Manager.CreateNotificationChannelGroup(new NotificationChannelGroup(GroupMessages, strings["platform.channel.messages"]));
                Manager.CreateNotificationChannelGroup(new NotificationChannelGroup(GroupCalls, strings["platform.channel.calls"]));
                Manager.CreateNotificationChannelGroup(new NotificationChannelGroup(GroupService, strings["platform.channel.background"]));

                var silent = new NotificationChannel(MessagesSilent, strings["platform.channel.messagesSilent"], NotificationImportance.Low)
                {
                    Description = strings["platform.channel.messages.description"],
                    Group = GroupMessages,
                    LockscreenVisibility = NotificationVisibility.Private,
                };
                silent.SetSound(null, null);
                silent.EnableVibration(false);
                Create(silent);

                var ongoing = new NotificationChannel(OngoingCall, strings["platform.channel.ongoingCall"], NotificationImportance.Low)
                {
                    Description = strings["platform.channel.ongoingCall.description"],
                    Group = GroupCalls,
                    LockscreenVisibility = NotificationVisibility.Public,
                };
                ongoing.SetSound(null, null);
                ongoing.EnableVibration(false);
                ongoing.SetShowBadge(false);
                Create(ongoing);

                var background = new NotificationChannel(Background, strings["platform.channel.background"], NotificationImportance.Min)
                {
                    Description = strings["platform.channel.background.description"],
                    Group = GroupService,
                    LockscreenVisibility = NotificationVisibility.Secret,
                };
                background.SetSound(null, null);
                background.EnableVibration(false);
                background.SetShowBadge(false);
                Create(background);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Creating notification channels failed");
            }
        }
    }

    /// <summary>The sound a channel can really play and its URI: the user's own file when it is there and can be shared, else the pack's.</summary>
    private (SoundChoice? Sound, AndroidUri? Uri) Resolve(SoundChoice? sound)
    {
        if (sound is null) return (null, null);
        if (sound.Custom is { } custom && SharedUri(custom) is { } shared) return (sound, shared);
        var pack = sound.Fallback;
        var context = Android.App.Application.Context;
        return (pack, AndroidUri.Parse($"android.resource://{context.PackageName}/raw/{SoundCatalog.ResourceName(pack.Pack, pack.Key)}"));
    }

    private AndroidUri? SharedUri(CustomSound sound)
    {
        var path = CustomSoundStore.PathOf(sound);
        if (!File.Exists(path)) return null;
        // Import refuses such files; this keeps the channel audible if one gets here anyway. Once per file and process.
        if (!_playable.TryGetValue(sound.Id, out var playable)) _playable[sound.Id] = playable = SystemSoundCheck.CanPlay(path);
        if (!playable)
        {
            logger.LogWarning("The system cannot play the custom sound; its channel rings with the pack's sound");
            return null;
        }
        var context = Android.App.Application.Context;
        AndroidUri uri;
        try
        {
            uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, SoundAuthority, new Java.IO.File(path))!;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The custom sound cannot be shared with the notification system");
            return null;
        }
        foreach (var player in SoundPlayers)
        {
            try
            {
                context.GrantUriPermission(player, uri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "No read grant for {Package}", player);
            }
        }
        return uri;
    }

    private static void ApplySound(NotificationChannel channel, AndroidUri? sound, AudioUsageKind usage)
    {
        if (sound is null)
        {
            channel.SetSound(null, null);
            return;
        }
        var attributes = new AudioAttributes.Builder()
            .SetUsage(usage)!
            .SetContentType(AudioContentType.Sonification)!
            .Build();
        channel.SetSound(sound, attributes);
    }

    private void Create(NotificationChannel channel)
    {
        try
        {
            Manager.CreateNotificationChannel(channel);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification channel {Channel} could not be created", channel.Id);
        }
    }

    private void DeleteStale(string prefix, string keep, string alsoKeep)
    {
        try
        {
            foreach (var existing in Manager.NotificationChannels ?? [])
            {
                var id = existing.Id;
                if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal) || id == keep || id == alsoKeep) continue;
                Manager.DeleteNotificationChannel(id);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Stale channel cleanup failed");
        }
    }

    /// <summary>"Перезвон (дерево)", "свой звук", "без звука": how the system settings list the channel.</summary>
    private string SoundName(SoundChoice? sound) => sound switch
    {
        null => strings["platform.tone.none"],
        { IsCustom: true } => strings["platform.tone.custom"],
        _ => $"{strings[$"platform.tone.{sound.Key}"]} ({strings[$"platform.pack.{sound.Pack}"]})",
    };
}
