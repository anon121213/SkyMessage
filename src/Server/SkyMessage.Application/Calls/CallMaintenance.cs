using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Calls;

internal sealed class CallMaintenance(
    IAppDbContext db,
    IClock clock,
    CallLifecycle lifecycle,
    ILiveKitRoomService liveKit,
    ILiveKitTokenService tokens,
    CallPresenceTracker tracker,
    ILogger<CallMaintenance> logger) : ICallMaintenance
{
    private const int BatchSize = 100;

    public async Task ExpireRingingAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.UtcNow - CallStateMachine.RingTimeout;

        var ringing = await db.Calls.AsNoTracking()
            .Where(c => c.Status == CallStatus.Ringing && c.CreatedAt <= cutoff)
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var callId in ringing)
            await ApplyAsync(callId, CallStateMachine.ExpireRinging, cancellationToken);

        var invited = await db.Calls.AsNoTracking()
            .Where(c => c.Kind == CallKind.Group && c.Status == CallStatus.Active
                        && c.Participants.Any(p => p.Status == CallParticipantStatus.Invited && p.InvitedAt <= cutoff))
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var callId in invited)
            await ApplyAsync(callId, CallStateMachine.ExpireInvites, cancellationToken);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var active = await db.Calls.AsNoTracking()
            .Where(c => c.Status == CallStatus.Active)
            .Select(c => new { c.Id, c.ChatId, c.Kind, c.RoomName })
            .ToListAsync(cancellationToken);
        tracker.Retain(active.Select(c => c.Id).ToHashSet());
        if (active.Count == 0 || !tokens.IsConfigured) return;

        HashSet<string> rooms;
        try
        {
            var listed = await liveKit.ListRoomsAsync(active.Select(c => c.RoomName).ToList(), cancellationToken);
            rooms = listed.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (LiveKitUnavailableException ex)
        {
            // Unknown is not empty: never end calls because LiveKit cannot be asked.
            if (tracker.SetLiveKitReachable(false))
                logger.LogWarning("LiveKit is unreachable, call reconciliation is paused: {Reason}", ex.Message);
            return;
        }
        if (tracker.SetLiveKitReachable(true))
            logger.LogInformation("LiveKit is reachable, call reconciliation resumed");

        foreach (var call in active)
        {
            IReadOnlyList<LiveKitParticipantInfo> participants;
            if (!rooms.Contains(call.RoomName))
            {
                participants = [];
            }
            else
            {
                try
                {
                    participants = await liveKit.ListParticipantsAsync(call.RoomName, cancellationToken);
                }
                catch (LiveKitUnavailableException ex)
                {
                    logger.LogDebug("Participants of call {CallId} unknown: {Reason}", call.Id, ex.Message);
                    continue;
                }
            }

            var devices = participants
                .Where(p => p.IsConnected)
                .Select(p => (p.Identity, UserId: LiveKitNames.TryParseIdentity(p.Identity, out var userId, out _) ? userId : (Guid?)null))
                .Where(d => d.UserId is not null)
                .Select(d => (d.Identity, UserId: d.UserId!.Value))
                .ToList();
            var present = devices.Select(d => d.UserId).ToHashSet();

            if (call.Kind == CallKind.Group && present.Count > 0)
            {
                // Removed members (e.g. while LiveKit was unreachable) must not stay in the room on an old token.
                var presentIds = present.ToArray();
                var members = await db.ChatMembers.AsNoTracking()
                    .Where(m => m.ChatId == call.ChatId && presentIds.Contains(m.UserId))
                    .Select(m => m.UserId)
                    .ToListAsync(cancellationToken);
                present.IntersectWith(members);
                var outsiders = devices.Where(d => !present.Contains(d.UserId)).Select(d => d.Identity).ToList();
                if (outsiders.Count > 0) lifecycle.KickInBackground(call.RoomName, outsiders);
            }

            await ApplyAsync(call.Id, (c, now) => CallStateMachine.Reconcile(c, present, tracker, now), cancellationToken);
        }
    }

    private async Task ApplyAsync(Guid callId, Func<Call, DateTimeOffset, CallOutcome> transition, CancellationToken cancellationToken)
    {
        try
        {
            await lifecycle.MutateAsync(callId, transition, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Call maintenance failed for call {CallId}", callId);
        }
    }
}
