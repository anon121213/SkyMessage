namespace SkyMessage.Application.Calls;

/// <summary>
/// LiveKit naming. Room = <c>call-{callId:N}</c>; participant identity = <c>{userId:N}.{sessionId:N}</c>, so
/// every signed-in device of a user is a separate LiveKit participant (clients map people by metadata.userId).
/// </summary>
public static class LiveKitNames
{
    private const string RoomPrefix = "call-";
    private const int GuidChars = 32;

    public static string RoomName(Guid callId) => $"{RoomPrefix}{callId:N}";

    public static bool TryParseRoomName(string? roomName, out Guid callId)
    {
        callId = Guid.Empty;
        return roomName is not null
               && roomName.Length == RoomPrefix.Length + GuidChars
               && roomName.StartsWith(RoomPrefix, StringComparison.Ordinal)
               && Guid.TryParseExact(roomName.AsSpan(RoomPrefix.Length), "N", out callId);
    }

    public static string Identity(Guid userId, Guid sessionId) => $"{userId:N}.{sessionId:N}";

    public static bool TryParseIdentity(string? identity, out Guid userId, out Guid sessionId)
    {
        userId = Guid.Empty;
        sessionId = Guid.Empty;
        return identity is not null
               && identity.Length == GuidChars * 2 + 1
               && identity[GuidChars] == '.'
               && Guid.TryParseExact(identity.AsSpan(0, GuidChars), "N", out userId)
               && Guid.TryParseExact(identity.AsSpan(GuidChars + 1), "N", out sessionId);
    }

    public static bool BelongsTo(string? identity, Guid userId) =>
        TryParseIdentity(identity, out var owner, out _) && owner == userId;
}

/// <summary>What a LiveKit join token grants: one room, publish + subscribe + data, as one device of a user.</summary>
public sealed record LiveKitJoinGrant(string RoomName, Guid UserId, Guid SessionId, string DisplayName, Guid? AvatarMediaId);

/// <summary>LiveKit access tokens (HS256 with the API secret). Infrastructure.</summary>
public interface ILiveKitTokenService
{
    /// <summary>Calls are enabled and an API key and a usable secret are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// JWT with <c>iss</c> = API key, <c>sub</c> = <see cref="LiveKitNames.Identity"/>, <c>name</c>, <c>nbf/exp</c>
    /// (<c>LiveKit:TokenTtlMinutes</c>), <c>video</c> grant object and <c>metadata</c> = JSON <c>{userId, avatarMediaId}</c>.
    /// </summary>
    string CreateJoinToken(LiveKitJoinGrant grant);
}

public sealed record LiveKitRoomInfo(string Name, string? Sid, int NumParticipants);

/// <summary>A participant as reported by LiveKit; <see cref="State"/> is the protobuf enum name (JOINING, JOINED, ACTIVE, DISCONNECTED).</summary>
public sealed record LiveKitParticipantInfo(string Identity, string? Sid, string? State)
{
    public bool IsConnected => !string.Equals(State, "DISCONNECTED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>LiveKit could not be asked (down, timeout, misconfigured keys): the room state is unknown, not empty.</summary>
public sealed class LiveKitUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Server-side LiveKit RoomService (Twirp over <c>LiveKit:InternalUrl</c>). Singleton, safe to use after the
/// request ended. Every method throws <see cref="LiveKitUnavailableException"/> when LiveKit cannot answer.
/// </summary>
public interface ILiveKitRoomService
{
    /// <summary>Existing rooms, optionally filtered by name.</summary>
    Task<IReadOnlyList<LiveKitRoomInfo>> ListRoomsAsync(IReadOnlyCollection<string>? names, CancellationToken cancellationToken = default);

    /// <summary>Participants of a room; empty when the room does not exist.</summary>
    Task<IReadOnlyList<LiveKitParticipantInfo>> ListParticipantsAsync(string roomName, CancellationToken cancellationToken = default);

    /// <summary>Closes the room and disconnects everyone; false when it did not exist.</summary>
    Task<bool> DeleteRoomAsync(string roomName, CancellationToken cancellationToken = default);

    /// <summary>Disconnects one participant (device); false when it was not in the room.</summary>
    Task<bool> RemoveParticipantAsync(string roomName, string identity, CancellationToken cancellationToken = default);
}

/// <summary>Verifies LiveKit webhook requests. Infrastructure.</summary>
public interface ILiveKitWebhookVerifier
{
    /// <summary>
    /// True when <paramref name="authorization"/> is a JWT signed with the API secret (HS256, <c>iss</c> = API key,
    /// within its lifetime) whose <c>sha256</c> claim equals base64(SHA-256(<paramref name="body"/>)).
    /// </summary>
    Task<bool> VerifyAsync(ReadOnlyMemory<byte> body, string? authorization);
}

/// <summary>Runs <see cref="ICallGraceHandler.RunGraceCheckAsync"/> later, in its own DI scope. Infrastructure.</summary>
public interface ICallGraceScheduler
{
    /// <summary>One pending check per (call, user): scheduling again only moves its due time later.</summary>
    void Schedule(Guid callId, Guid userId, TimeSpan delay);
}

/// <summary>The delayed check after a device left a private call (see <see cref="ICallGraceScheduler"/>).</summary>
public interface ICallGraceHandler
{
    /// <summary>Ends the call when no device of the user came back; returns a delay when it should be checked again.</summary>
    Task<TimeSpan?> RunGraceCheckAsync(Guid callId, Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Periodic call housekeeping, driven by the Infrastructure background service.</summary>
public interface ICallMaintenance
{
    /// <summary>Ringing private calls past the ring timeout → Missed; unanswered group invitations → Missed.</summary>
    Task ExpireRingingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares Active calls with LiveKit (ListRooms/ListParticipants): ends calls whose room is gone or empty for
    /// longer than a minute and fixes participant states. Does nothing while LiveKit is unreachable.
    /// </summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
