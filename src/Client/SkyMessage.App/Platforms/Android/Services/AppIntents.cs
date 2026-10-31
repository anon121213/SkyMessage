using Android.App;
using Android.Content;

namespace SkyMessage.App.Droid;

/// <summary>Intent actions/extras shared by notifications, MainActivity, receivers and services.</summary>
internal static class AppIntents
{
    public const string ActionIncomingCall = "com.skymessage.action.INCOMING_CALL";
    public const string ActionCallAccept = "com.skymessage.action.CALL_ACCEPT";
    public const string ActionCallAcceptVideo = "com.skymessage.action.CALL_ACCEPT_VIDEO";
    public const string ActionCallDecline = "com.skymessage.action.CALL_DECLINE";
    public const string ActionOngoingCall = "com.skymessage.action.ONGOING_CALL";
    public const string ActionCallHangup = "com.skymessage.action.CALL_HANGUP";
    public const string ActionCallToggleMute = "com.skymessage.action.CALL_TOGGLE_MUTE";
    public const string ExtraCallId = "com.skymessage.extra.CALL_ID";

    public const int IncomingCallNotificationId = 2;
    public const int OngoingCallNotificationId = 3;
    public const int BackgroundNotificationId = 4;
    /// <summary>"SkyMessage updated, tap to open" (AndroidAppUpdateInstaller); also its PendingIntent request code.</summary>
    public const int UpdatedNotificationId = 5;
    public const int MessagesSummaryNotificationId = 1;

    public static PendingIntentFlags ImmutableUpdate => PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;

    /// <summary>Brings MainActivity forward (singleTask) with an action and/or data.</summary>
    public static Intent ActivityIntent(Context context, string? action, Guid? callId = null, Android.Net.Uri? data = null)
    {
        var intent = new Intent(context, typeof(MainActivity));
        if (action is not null) intent.SetAction(action);
        if (data is not null) intent.SetData(data);
        if (callId is { } id) intent.PutExtra(ExtraCallId, id.ToString());
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return intent;
    }

    public static PendingIntent ActivityPendingIntent(Context context, int requestCode, Intent intent) =>
        PendingIntent.GetActivity(context, requestCode, intent, ImmutableUpdate)!;

    public static PendingIntent BroadcastPendingIntent(Context context, int requestCode, string action, Guid? callId = null)
    {
        var intent = new Intent(context, typeof(CallActionReceiver)).SetAction(action);
        if (callId is { } id) intent.PutExtra(ExtraCallId, id.ToString());
        return PendingIntent.GetBroadcast(context, requestCode, intent, ImmutableUpdate)!;
    }

    public static Guid? CallIdOf(Intent? intent) =>
        Guid.TryParse(intent?.GetStringExtra(ExtraCallId), out var id) ? id : null;

    /// <summary>Stable positive request code / notification id for a GUID.</summary>
    public static int CodeOf(Guid id, int salt = 0) => 0x10000 + ((id.GetHashCode() ^ salt) & 0x0FFFFFFF);
}
