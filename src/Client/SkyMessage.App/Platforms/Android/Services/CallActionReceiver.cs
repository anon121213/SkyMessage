using Android.App;
using Android.Content;
using SkyMessage.App.Services;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>Notification buttons that must not open the app: decline an incoming call, mute / hang up an ongoing one.</summary>
[BroadcastReceiver(Name = "com.skymessage.messenger.CallActionReceiver", Exported = false, Enabled = true)]
public sealed class CallActionReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        switch (intent?.Action)
        {
            case AppIntents.ActionCallDecline:
                if (AppIntents.CallIdOf(intent) is { } callId)
                    (AppServices.Get<ILocalNotificationService>() as AndroidNotificationService)?.OnCallAction(callId, CallNotificationAction.Decline);
                break;
            case AppIntents.ActionCallHangup:
                (AppServices.Get<ICallPlatformService>() as AndroidCallPlatformService)?.RaiseCommand(CallCommand.Hangup);
                break;
            case AppIntents.ActionCallToggleMute:
                (AppServices.Get<ICallPlatformService>() as AndroidCallPlatformService)?.RaiseCommand(CallCommand.ToggleMicrophone);
                break;
        }
    }
}
