namespace SkyMessage.Contracts;

public sealed record CreateUploadRequest
{
    public MediaKind Kind { get; init; }
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
    public long SizeBytes { get; init; }
    /// <summary>Optional hex SHA-256 of the whole file, verified on complete.</summary>
    public string? Sha256 { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationMs { get; init; }
    /// <summary>Optional client-computed waveform for voice messages (the server may recompute it).</summary>
    public byte[]? Waveform { get; init; }
}

public sealed record UploadSessionDto
{
    public Guid MediaId { get; init; }
    public int ChunkSize { get; init; }
    public int ChunkCount { get; init; }
    /// <summary>Chunk indexes already stored (for resuming an interrupted upload).</summary>
    public IReadOnlyList<int> ReceivedChunks { get; init; } = [];
    public MediaStatus Status { get; init; }
}

public sealed record StartCallRequest
{
    public Guid ChatId { get; init; }
    public bool IsVideo { get; init; }
}

public sealed record DeclineCallRequest
{
    /// <summary>Declined automatically because this user is already in another call (caller sees "busy").</summary>
    public bool Busy { get; init; }
}

public sealed record CallParticipantDto
{
    public Guid UserId { get; init; }
    public CallParticipantStatus Status { get; init; }
    public DateTimeOffset? JoinedAt { get; init; }
    public DateTimeOffset? LeftAt { get; init; }
}

public sealed record CallDto
{
    public Guid Id { get; init; }
    public Guid ChatId { get; init; }
    public CallKind Kind { get; init; }
    public bool IsVideo { get; init; }
    public CallStatus Status { get; init; }
    public Guid InitiatorId { get; init; }
    public IReadOnlyList<CallParticipantDto> Participants { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? AnsweredAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}

public sealed record CallJoinInfoDto
{
    public CallDto Call { get; init; } = new();
    /// <summary>
    /// LiveKit signaling URL. Either absolute (ws/wss) or a path such as "/livekit" which the
    /// client resolves against its API base URL (http→ws, https→wss).
    /// </summary>
    public string LiveKitUrl { get; init; } = HubRoutes.LiveKitProxy;
    public string LiveKitToken { get; init; } = "";
}

public sealed record CallHistoryItemDto
{
    public CallDto Call { get; init; } = new();
    public ChatType ChatType { get; init; }
    public string ChatTitle { get; init; } = "";
    public Guid? ChatAvatarMediaId { get; init; }
    /// <summary>The other side of a private call.</summary>
    public UserSummaryDto? Peer { get; init; }
    public bool IsOutgoing { get; init; }
    public int DurationSeconds { get; init; }
}
