using Android.App;
using Android.Content;
using Android.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;

namespace SkyMessage.App.Droid;

/// <summary>
/// Notification channels. A channel's sound/vibration cannot change after creation, so there is one channel
/// per (tone, vibration) choice; when the user picks another tone the matching channel is created and the
/// stale ones are deleted. Sounds are our synthesized WAVs in res/raw (tone_&lt;key&gt;).
/// </summary>
public sealed class NotificationChannels(IServiceProvider services, NativeStrings strings, ILogger<NotificationChannels> logger)
{
    public const string OngoingCall = "call.ongoing.v1";
    public const string Background = "service.connection.v1";
    public const string MessagesSilent = "msg.silent.v1";

    private const string GroupMessages = "messages";
    private const string GroupCalls = "calls";
    private const string GroupService = "service";

    private static readonly string[] MessageTones = ["chime", "pop", "glass", "none"];
    private static readonly string[] Ringtones = ["classic", "soft", "bright", "none"];
    private static readonly long[] MessageVibration = [0, 180, 120, 180];
    private static readonly long[] CallVibration = [0, 900, 700, 900, 700, 900, 700];

    private readonly object _gate = new();
    private string? _language;

    private NotificationManager Manager => (NotificationManager)Android.App.Application.Context.GetSystemService(Context.NotificationService)!;

    private NotificationSettings Settings => services.GetService<ISettingsService>()?.Current.Notifications ?? new NotificationSettings();

    /// <summary>Channel for a message notification under the current settings.</summary>
    public string Messages(bool silent)
    {
        EnsureBase();
        var settings = Settings;
        if (silent || !settings.Sound && !settings.Vibrate) return MessagesSilent;
        var tone = settings.Sound ? Pick(settings.MessageTone, MessageTones, "chime") : "none";
        var id = $"msg.{tone}.{(settings.Vibrate ? "v" : "n")}.v1";
        lock (_gate)
        {
            var name = $"{strings["platform.channel.messages"]} · {ToneName(tone)}{(settings.Vibrate ? ", " + strings["platform.channel.withVibration"] : "")}";
            var channel = new NotificationChannel(id, name, NotificationImportance.High)
            {
                Description = strings["platform.channel.messages.description"],
                Group = GroupMessages,
                LockscreenVisibility = NotificationVisibility.Private,
            };
            channel.SetShowBadge(true);
            channel.EnableLights(true);
            channel.LightColor = unchecked((int)0xFF6A4BFF);
            ApplySound(channel, tone, AudioUsageKind.Notification);
            channel.EnableVibration(settings.Vibrate);
            if (settings.Vibrate) channel.SetVibrationPattern(MessageVibration);
            Create(channel);
            DeleteStale("msg.", id, MessagesSilent);
        }
        return id;
    }

    /// <summary>Ringing channel for incoming calls under the current settings.</summary>
    public string IncomingCall()
    {
        EnsureBase();
        var settings = Settings;
        var tone = settings.Sound ? Pick(settings.Ringtone, Ringtones, "classic") : "none";
        var id = $"call.{tone}.{(settings.Vibrate ? "v" : "n")}.v1";
        lock (_gate)
        {
            var channel = new NotificationChannel(id, $"{strings["platform.channel.calls"]} · {ToneName(tone)}", NotificationImportance.High)
            {
                Description = strings["platform.channel.calls.description"],
                Group = GroupCalls,
                LockscreenVisibility = NotificationVisibility.Public,
            };
            channel.SetShowBadge(false);
            ApplySound(channel, tone, AudioUsageKind.NotificationRingtone);
            channel.EnableVibration(settings.Vibrate);
            if (settings.Vibrate) channel.SetVibrationPattern(CallVibration);
            Create(channel);
            DeleteStale("call.", id, OngoingCall);
        }
        return id;
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

    private void ApplySound(NotificationChannel channel, string tone, AudioUsageKind usage)
    {
        if (tone == "none")
        {
            channel.SetSound(null, null);
            return;
        }
        var context = Android.App.Application.Context;
        var uri = Android.Net.Uri.Parse($"android.resource://{context.PackageName}/raw/tone_{tone}");
        var attributes = new AudioAttributes.Builder()
            .SetUsage(usage)!
            .SetContentType(AudioContentType.Sonification)!
            .Build();
        channel.SetSound(uri, attributes);
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

    private string ToneName(string tone) => strings[$"platform.tone.{tone}"];

    private static string Pick(string? value, string[] allowed, string fallback) =>
        value is not null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase) ? value.ToLowerInvariant() : fallback;
}
