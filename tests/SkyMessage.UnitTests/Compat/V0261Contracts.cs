// GENERATED from the released 0.2.61 Contracts (git a110481: src/Shared/SkyMessage.Contracts/Enums.cs and
// Dtos/MessageDtos.cs), kept frozen on purpose: the message shapes an app on 0.2.61 reads, with all their members
// (docs dropped; MessagesPageDto.Users left out, untouched by stickers and GIFs). Never update it to newer Contracts:
// it is what the phones out there run. Read with OldAppJson (0.2.61's SkyJson).
using System.Text.Json;
using System.Text.Json.Serialization;
using SkyMessage.Contracts;

namespace SkyMessage.UnitTests.Compat.V0261;

/// <summary>0.2.61's SkyJson: web defaults, enums as strings, only SystemEventType tolerant of unknown names.</summary>
internal static class OldAppJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Insert(0, new TolerantEnumConverter<SystemEventType>());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal enum MessageType
{
    Text = 0,
    Photo = 1,
    Video = 2,
    Album = 3,
    File = 4,
    Voice = 5,
    VideoNote = 6,
    Audio = 7,
    System = 8,
    Call = 9,
}

internal enum MessageEntityType
{
    Bold = 1,
    Italic = 2,
    Underline = 3,
    Strikethrough = 4,
    Code = 5,
    Pre = 6,
    TextLink = 7,
    Url = 8,
    Mention = 9,
    Spoiler = 10,
    Email = 11,
    Phone = 12,
    Quote = 13,
}

internal enum MediaKind
{
    Photo = 1,
    Video = 2,
    File = 3,
    Voice = 4,
    VideoNote = 5,
    Audio = 6,
}

internal enum MediaStatus
{
    Uploading = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3,
}

internal enum SystemEventType
{
    Unknown = 0,
    ChatCreated = 1,
    MembersAdded = 2,
    MemberLeft = 3,
    MemberRemoved = 4,
    MemberJoinedByLink = 5,
    TitleChanged = 6,
    PhotoChanged = 7,
    PhotoRemoved = 8,
    DescriptionChanged = 9,
    MessagePinned = 10,
    GroupCallStarted = 11,
    GroupCallEnded = 12,
    ContentProtectionEnabled = 13,
    ContentProtectionDisabled = 14,
}

internal enum CallStatus
{
    Ringing = 0,
    Active = 1,
    Ended = 2,
    Missed = 3,
    Declined = 4,
    Cancelled = 5,
    Busy = 6,
    Failed = 7,
}

internal sealed record MessageEntityDto
{
    public MessageEntityType Type { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; }
    public string? Url { get; init; }
    public Guid? UserId { get; init; }
    public string? Language { get; init; }
}

internal sealed record MediaDto
{
    public Guid Id { get; init; }
    public MediaKind Kind { get; init; }
    public MediaStatus Status { get; init; }
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
    public long SizeBytes { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationMs { get; init; }
    public byte[]? Waveform { get; init; }
    public byte[]? MiniThumbnail { get; init; }
    public bool HasThumbnail { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed record ReplyPreviewDto
{
    public Guid MessageId { get; init; }
    public long Seq { get; init; }
    public Guid? SenderId { get; init; }
    public MessageType Type { get; init; }
    public string? Text { get; init; }
    public MediaDto? Media { get; init; }
    public bool IsDeleted { get; init; }
}

internal sealed record ForwardInfoDto
{
    public Guid? FromUserId { get; init; }
    public Guid? FromChatId { get; init; }
    public string SenderName { get; init; } = "";
    public DateTimeOffset OriginalDate { get; init; }
}

internal sealed record ReactionDto
{
    public string Emoji { get; init; } = "";
    public int Count { get; init; }
    public IReadOnlyList<Guid> UserIds { get; init; } = [];
}

internal sealed record SystemEventDto
{
    public SystemEventType Type { get; init; }
    public Guid? ActorId { get; init; }
    public IReadOnlyList<Guid> TargetUserIds { get; init; } = [];
    public string? Text { get; init; }
    public Guid? MessageId { get; init; }
    public Guid? CallId { get; init; }
}

internal sealed record CallInfoDto
{
    public Guid CallId { get; init; }
    public bool IsVideo { get; init; }
    public CallStatus Status { get; init; }
    public int DurationSeconds { get; init; }
    public Guid InitiatorId { get; init; }
}

internal sealed record MessageDto
{
    public Guid Id { get; init; }
    public Guid ChatId { get; init; }
    public long Seq { get; init; }
    public Guid? SenderId { get; init; }
    public MessageType Type { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<MessageEntityDto>? Entities { get; init; }
    public IReadOnlyList<MediaDto> Attachments { get; init; } = [];
    public ReplyPreviewDto? ReplyTo { get; init; }
    public ForwardInfoDto? Forward { get; init; }
    public IReadOnlyList<ReactionDto> Reactions { get; init; } = [];
    public SystemEventDto? SystemEvent { get; init; }
    public CallInfoDto? Call { get; init; }
    public bool IsPinned { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? EditedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public Guid? ClientMessageId { get; init; }
}

internal sealed record MessageChangesDto
{
    public IReadOnlyList<MessageDto> Messages { get; init; } = [];
    public IReadOnlyList<Guid> HiddenMessageIds { get; init; } = [];
    public long ClearedUpToSeq { get; init; }
    public string NextCursor { get; init; } = "";
    public bool HasMore { get; init; }
}

internal sealed record MessagesPageDto
{
    public IReadOnlyList<MessageDto> Messages { get; init; } = [];
    public bool HasMoreBefore { get; init; }
    public bool HasMoreAfter { get; init; }
    public string SyncCursor { get; init; } = "";
    public IReadOnlyDictionary<Guid, string> MyReactions { get; init; } = new Dictionary<Guid, string>();
}
