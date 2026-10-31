using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

internal enum OutboxKind
{
    Message = 0,
    Forward = 1,
}

/// <summary>Persisted outbox entry ("outbox" in the per-account cache). Resent with the same ClientMessageId (= Key).</summary>
internal sealed record OutboxRecord
{
    public Guid Key { get; init; }
    public Guid ChatId { get; init; }
    public OutboxKind Kind { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public MessageType Type { get; init; }
    public string? Text { get; init; }
    public List<MessageEntityDto>? Entities { get; init; }
    public Guid? ReplyToMessageId { get; init; }
    public ReplyPreviewDto? ReplyPreview { get; init; }
    public List<OutboxAttachmentRecord> Attachments { get; init; } = [];
    public Guid? ForwardFromChatId { get; init; }
    public Guid? ForwardMessageId { get; init; }
    /// <summary>Copy of the forwarded message for the pending bubble.</summary>
    public MessageDto? ForwardSource { get; init; }
    /// <summary>Sent only once the item with this key left the outbox (the rest of a long caption follows its album).</summary>
    public Guid? SendAfterKey { get; init; }
    public bool Failed { get; init; }
    public string? Error { get; init; }
}

internal sealed record OutboxAttachmentRecord
{
    /// <summary>Key of the durable copy in <see cref="IOutboxFileStore"/>.</summary>
    public Guid Key { get; init; }
    /// <summary>Kind uploaded (File for "send without compression").</summary>
    public MediaKind Kind { get; init; }
    public bool Compress { get; init; }
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
    public long Size { get; init; } = -1;
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationMs { get; init; }
    public byte[]? Waveform { get; init; }
    /// <summary>Small data: URL only (large previews are not persisted).</summary>
    public string? PreviewUrl { get; init; }
    /// <summary>The payload is final (compressed or original); Size/ContentType/FileName describe it.</summary>
    public bool Prepared { get; init; }
    /// <summary>The final payload is stored in the outbox file store under <see cref="Key"/>.</summary>
    public bool Persisted { get; init; }
    /// <summary>Upload session, for resuming after a restart.</summary>
    public Guid? MediaId { get; init; }
    /// <summary>Set once the server reports the media Ready.</summary>
    public MediaDto? Media { get; init; }
}

/// <summary>Runtime state of an outbox entry (mutated only inside the message store's queue).</summary>
internal sealed class OutboxEntry
{
    public required OutboxRecord Record { get; set; }
    public List<OutboxAttachment> Attachments { get; init; } = [];
    public MessageSendState State { get; set; } = MessageSendState.Sending;
    public double Progress { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    /// <summary>Timestamp (TimeProvider) before which no retry happens; 0 = now.</summary>
    public long NextAttemptAt { get; set; }
    /// <summary>The <see cref="NextAttemptAt"/> a retry timer is already armed for (avoids duplicate timers).</summary>
    public long RetryScheduledAt { get; set; }
    /// <summary>Times the attachments were uploaded again because the server lost the media.</summary>
    public int Reuploads { get; set; }
    public bool PreparationRunning { get; set; }
    public bool Removed { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public MessageItem? CachedItem { get; set; }

    public Guid Key => Record.Key;
    public Guid ChatId => Record.ChatId;

    public bool ReadyToSend => Record.Kind == OutboxKind.Forward || Attachments.All(a => a.Media is { Status: MediaStatus.Ready });

    public OutboxRecord ToRecord() => Record with
    {
        Attachments = Attachments.Select(a => a.ToRecord()).ToList(),
        Failed = State == MessageSendState.Failed,
        Error = State == MessageSendState.Failed ? Error : null,
    };
}

internal sealed class OutboxAttachment
{
    private const int MaxPersistedPreviewChars = 48 * 1024;

    public required Guid Key { get; init; }
    public required MediaKind Kind { get; set; }
    public bool Compress { get; init; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; } = -1;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationMs { get; set; }
    public byte[]? Waveform { get; set; }
    public string? PreviewUrl { get; set; }
    public bool Prepared { get; set; }
    public bool Persisted { get; set; }
    public bool Probed { get; set; }
    public Guid? MediaId { get; set; }
    public MediaDto? Media { get; set; }
    /// <summary>The original pick, or after preparation the final payload (durable copy when persisted).</summary>
    public PickedFile? Source { get; set; }
    /// <summary>Platform temp file of a compressed payload, deleted once it is no longer needed.</summary>
    public string? TempFilePath { get; set; }

    public LocalAttachmentPreview ToPreview() => new(Kind, FileName, Size, PreviewUrl, Width, Height, DurationMs, Waveform);

    public OutboxAttachmentRecord ToRecord() => new()
    {
        Key = Key,
        Kind = Kind,
        Compress = Compress,
        FileName = FileName,
        ContentType = ContentType,
        Size = Size,
        Width = Width,
        Height = Height,
        DurationMs = DurationMs,
        Waveform = Waveform,
        PreviewUrl = PreviewUrl is { Length: <= MaxPersistedPreviewChars } ? PreviewUrl : null,
        Prepared = Prepared,
        Persisted = Persisted,
        MediaId = MediaId,
        Media = Media,
    };

    public static OutboxAttachment FromRecord(OutboxAttachmentRecord record) => new()
    {
        Key = record.Key,
        Kind = record.Kind,
        Compress = record.Compress,
        FileName = record.FileName,
        ContentType = record.ContentType,
        Size = record.Size,
        Width = record.Width,
        Height = record.Height,
        DurationMs = record.DurationMs,
        Waveform = record.Waveform,
        PreviewUrl = record.PreviewUrl,
        Prepared = record.Prepared,
        Persisted = record.Persisted,
        Probed = true,
        MediaId = record.MediaId,
        Media = record.Media,
    };
}

internal static class TextSplitter
{
    /// <summary>
    /// Splits text longer than <paramref name="maxLength"/> into several messages, preferring line breaks and spaces
    /// and never cutting a surrogate pair. Entities entirely inside a part move with it; ones crossing a cut are dropped.
    /// </summary>
    public static IReadOnlyList<(string Text, List<MessageEntityDto>? Entities)> Split(string text, IReadOnlyList<MessageEntityDto>? entities,
        int maxLength = Limits.MessageTextMaxLength)
    {
        if (text.Length <= maxLength) return [(text, entities?.ToList())];

        var parts = new List<(string, List<MessageEntityDto>?)>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + maxLength);
            if (end < text.Length)
            {
                var minCut = start + maxLength * 3 / 4;
                var cut = text.LastIndexOf('\n', end - 1, end - minCut);
                if (cut < 0) cut = text.LastIndexOf(' ', end - 1, end - minCut);
                if (cut >= minCut) end = cut + 1;
                if (char.IsHighSurrogate(text[end - 1])) end--;
            }

            var piece = text[start..end];
            List<MessageEntityDto>? pieceEntities = null;
            if (entities is { Count: > 0 })
            {
                pieceEntities = entities
                    .Where(e => e.Offset >= start && e.Offset + e.Length <= end)
                    .Select(e => e with { Offset = e.Offset - start })
                    .ToList();
            }

            var trimmed = piece.TrimEnd();
            if (trimmed.Length > 0) parts.Add((trimmed, pieceEntities?.Where(e => e.Offset + e.Length <= trimmed.Length).ToList()));
            start = end;
        }
        return parts;
    }
}
