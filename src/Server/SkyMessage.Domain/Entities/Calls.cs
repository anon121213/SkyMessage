using SkyMessage.Contracts;

namespace SkyMessage.Domain.Entities;

public sealed class Call
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ChatId { get; set; }
    public Chat? Chat { get; set; }
    public CallKind Kind { get; set; }
    public bool IsVideo { get; set; }
    public CallStatus Status { get; set; }
    public Guid InitiatorId { get; set; }
    /// <summary>LiveKit room name, "call-{Id:N}".</summary>
    public string RoomName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Call-log / system message posted in the chat for this call.</summary>
    public Guid? MessageId { get; set; }

    public List<CallParticipant> Participants { get; set; } = [];

    public bool IsFinished => Status is not (CallStatus.Ringing or CallStatus.Active);
}

public sealed class CallParticipant
{
    public Guid CallId { get; set; }
    public Call? Call { get; set; }
    public Guid UserId { get; set; }
    public CallParticipantStatus Status { get; set; }
    public DateTimeOffset InvitedAt { get; set; }
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}
