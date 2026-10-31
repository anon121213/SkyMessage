using SkyMessage.Contracts;

namespace SkyMessage.Application.Common.Interfaces;

/// <summary>The authenticated caller of the current HTTP request (implemented in the Api layer).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    /// <summary>Throws <see cref="Exceptions.UnauthorizedException"/> when not authenticated.</summary>
    Guid UserId { get; }
    Guid SessionId { get; }
    string? IpAddress { get; }
}

/// <summary>UTC time truncated to whole microseconds (PostgreSQL timestamptz precision), so values round-trip exactly.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Pushes real-time events to connected clients. Implemented in the Api layer on top of
/// <c>IHubContext&lt;RealtimeHub, IRealtimeClient&gt;</c>; SignalR user id = user Guid ("D" format).
/// Sending to users with no open connection is a cheap no-op. Publish only after your transaction
/// committed (or SaveChangesAsync succeeded when there is no explicit transaction).
/// </summary>
public interface IRealtimeNotifier
{
    IRealtimeClient User(Guid userId);
    IRealtimeClient Users(IReadOnlyCollection<Guid> userIds);
    /// <summary>All connections of one signed-in device.</summary>
    IRealtimeClient Session(Guid sessionId);
}

/// <summary>Builds viewer-dependent <see cref="UserSummaryDto"/>s (privacy, blocks, contact names, presence). Identity feature.</summary>
public interface IUserSummaryService
{
    Task<IReadOnlyDictionary<Guid, UserSummaryDto>> GetAsync(Guid viewerId, IEnumerable<Guid> userIds, CancellationToken cancellationToken = default);
    Task<UserSummaryDto?> GetOneAsync(Guid viewerId, Guid userId, CancellationToken cancellationToken = default);
    Task<PresenceDto> GetPresenceAsync(Guid viewerId, Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Privacy and block rules shared by all features. Identity feature. If the owner blocked the viewer,
/// every visibility level behaves as Nobody for that viewer.
/// </summary>
public interface IPrivacyService
{
    /// <summary>True when either user blocked the other.</summary>
    Task<bool> IsBlockedEitherWayAsync(Guid userA, Guid userB, CancellationToken cancellationToken = default);
    /// <summary>Messages privacy + blocks: may <paramref name="fromUserId"/> start/continue a private chat with <paramref name="toUserId"/>?</summary>
    Task<bool> CanMessageAsync(Guid fromUserId, Guid toUserId, CancellationToken cancellationToken = default);
    Task<bool> CanCallAsync(Guid fromUserId, Guid toUserId, CancellationToken cancellationToken = default);
    Task<bool> CanAddToGroupAsync(Guid actorId, Guid targetUserId, CancellationToken cancellationToken = default);
    /// <summary>ProfilePhoto level + blocks (owners always see their own).</summary>
    Task<bool> CanSeeProfilePhotoAsync(Guid viewerId, Guid ownerId, CancellationToken cancellationToken = default);
    /// <summary>Has <paramref name="ownerId"/> saved <paramref name="userId"/> to contacts?</summary>
    Task<bool> IsInContactsOfAsync(Guid ownerId, Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>In-memory online tracking fed by the realtime hub. Identity feature.</summary>
public interface IPresenceService
{
    bool IsOnline(Guid userId);
    /// <summary>Has at least one open realtime connection (even if the app is in background).</summary>
    bool IsConnected(Guid userId);
}

/// <summary>Session checks used by authentication and media URLs. Identity feature.</summary>
public interface ISessionValidator
{
    /// <summary>True only for an active session that belongs to <paramref name="userId"/>. Cached briefly; evicted on revoke.</summary>
    Task<bool> IsActiveAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Verifies a <c>?t=</c> media signature (<see cref="MediaSignature"/>) for <paramref name="mediaId"/>
    /// against the session's key. Returns the session's owner when valid and the session is active.
    /// </summary>
    Task<(Guid UserId, Guid SessionId)?> ValidateMediaSignatureAsync(string token, Guid mediaId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Records that the client got the refresh response an access token came with (its <c>rtb</c> claim matches the
    /// session's current refresh token), which ends lenient retries with the previous token. One write per rotation.
    /// </summary>
    Task ConfirmRefreshAsync(Guid sessionId, string binding, CancellationToken cancellationToken = default);
}

/// <summary>
/// Posts service messages into chats. Messaging feature.
/// Call it only after your own changes are saved and committed, with no ambient transaction: it runs its
/// own transaction (seq + insert + chat update), commits, then broadcasts MessageCreated. It throws
/// <see cref="InvalidOperationException"/> when <c>Database.CurrentTransaction</c> is not null.
/// </summary>
public interface ISystemMessageService
{
    Task<MessageDto> PostSystemEventAsync(Guid chatId, SystemEventDto systemEvent, CancellationToken cancellationToken = default);
    /// <summary>Call log entry in a private chat; the sender is the call initiator.</summary>
    Task<MessageDto> PostCallLogAsync(Guid chatId, CallInfoDto call, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hooks other features into membership changes (called by Messaging after commit). The Calls feature uses
/// it to drop removed members from an ongoing group call. All registered observers are invoked.
/// </summary>
public interface IChatMembershipObserver
{
    Task OnMemberRemovedAsync(Guid chatId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole chat is about to be hard-deleted (its rows cascade away right after). Called before the
    /// delete, outside any transaction, so observers can still read and finish related state (e.g. end calls).
    /// </summary>
    Task OnChatDeletingAsync(Guid chatId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Mobile push (FCM/APNs). Implementations decide whom to push (e.g. only users without live connections).</summary>
public interface IPushNotificationService
{
    Task NotifyNewMessageAsync(MessageDto message, IReadOnlyCollection<Guid> recipientIds, CancellationToken cancellationToken = default);
    Task NotifyIncomingCallAsync(CallDto call, IReadOnlyCollection<Guid> recipientIds, CancellationToken cancellationToken = default);
    Task NotifyCallEndedAsync(CallDto call, IReadOnlyCollection<Guid> recipientIds, CancellationToken cancellationToken = default);
}
