using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Kit;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Calls;

/// <summary>
/// Small bridge to the call screen for other components (the group-call banner, a chat header "return to call"
/// button): <see cref="Expand"/> brings the minimized call screen back. The rendered <c>CallOverlay</c> subscribes.
/// </summary>
public static class CallUi
{
    public static event Action? ExpandRequested;

    /// <summary>True while the full-size call screen covers the app: a chat underneath must not mark messages read.</summary>
    public static bool IsCovering { get; private set; }

    public static event Action? CoveringChanged;

    /// <summary>Set by <c>CallOverlay</c> after every render.</summary>
    internal static void SetCovering(bool value)
    {
        if (IsCovering == value) return;
        IsCovering = value;
        foreach (var handler in CoveringChanged?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch { /* a disposed subscriber must not break the call screen */ }
        }
    }

    /// <summary>Shows the call screen full size (no-op when there is no call).</summary>
    public static void Expand()
    {
        foreach (var handler in ExpandRequested?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch { /* a disposed call screen must not break the caller */ }
        }
    }

    /// <summary>"Call ended / Busy / No answer…" for a finished call.</summary>
    public static string EndText(ILocalizer l, CallStatus? status, bool outgoing) => status switch
    {
        CallStatus.Busy => l["calls.end.busy"],
        CallStatus.Declined => outgoing ? l["calls.end.declinedByPeer"] : l["calls.end.declined"],
        CallStatus.Missed => outgoing ? l["calls.end.noAnswer"] : l["calls.end.missed"],
        CallStatus.Cancelled => l["calls.end.cancelled"],
        CallStatus.Failed => l["calls.end.failed"],
        _ => l["calls.end.ended"],
    };

    /// <summary>Icon for an audio route.</summary>
    public static string RouteIcon(AudioRouteKind? kind) => kind switch
    {
        AudioRouteKind.Earpiece => IconNames.Ear,
        AudioRouteKind.Bluetooth => IconNames.Bluetooth,
        AudioRouteKind.Wired => IconNames.Headphones,
        _ => IconNames.Volume2,
    };

    /// <summary>Localized name of an audio route (device names for headsets when the platform knows them).</summary>
    public static string RouteName(ILocalizer l, AudioRoute route) => route.Kind switch
    {
        AudioRouteKind.Earpiece => l["calls.route.earpiece"],
        AudioRouteKind.Speaker => l["calls.route.speaker"],
        AudioRouteKind.Bluetooth => string.IsNullOrWhiteSpace(route.Name) ? l["calls.route.bluetooth"] : route.Name,
        _ => string.IsNullOrWhiteSpace(route.Name) ? l["calls.route.wired"] : route.Name,
    };

    /// <summary>Signal icon for a LiveKit connection quality (3 excellent … 0 lost); null when unknown.</summary>
    public static string? QualityIcon(int quality) => quality switch
    {
        >= 3 => IconNames.Signal,
        2 => IconNames.SignalHigh,
        1 => IconNames.SignalMedium,
        0 => IconNames.SignalLow,
        _ => null,
    };

    /// <summary>A call I did not pick up (incoming missed/cancelled/busy-declined, or a group call I never joined).</summary>
    public static bool IsMissed(CallHistoryItemDto item, Guid me)
    {
        if (item.IsOutgoing) return false;
        if (item.Call.Kind == CallKind.Private)
            return item.Call.Status is CallStatus.Missed or CallStatus.Cancelled or CallStatus.Busy;
        var mine = item.Call.Participants.FirstOrDefault(p => p.UserId == me);
        var finished = item.Call.Status is not (CallStatus.Ringing or CallStatus.Active);
        return finished && (mine is null || mine.Status is CallParticipantStatus.Missed or CallParticipantStatus.Invited or CallParticipantStatus.Ringing);
    }

    /// <summary>True while a call is ringing, being set up or in progress (not ended).</summary>
    public static bool IsLive(CallSession? session) => session is not null && session.Phase != CallPhase.Ended;
}
