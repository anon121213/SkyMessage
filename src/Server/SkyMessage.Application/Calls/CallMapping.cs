using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Calls;

internal static class CallMapping
{
    /// <summary>Viewer-independent: the same payload goes to every recipient. Initiator first, then by invitation time.</summary>
    public static CallDto ToDto(Call call) => new()
    {
        Id = call.Id,
        ChatId = call.ChatId,
        Kind = call.Kind,
        IsVideo = call.IsVideo,
        Status = call.Status,
        InitiatorId = call.InitiatorId,
        Participants = call.Participants
            .OrderBy(p => p.UserId == call.InitiatorId ? 0 : 1)
            .ThenBy(p => p.InvitedAt)
            .ThenBy(p => p.UserId)
            .Select(p => new CallParticipantDto
            {
                UserId = p.UserId,
                Status = p.Status,
                JoinedAt = p.JoinedAt,
                LeftAt = p.LeftAt,
            })
            .ToList(),
        CreatedAt = call.CreatedAt,
        AnsweredAt = call.AnsweredAt,
        EndedAt = call.EndedAt,
    };

    public static CallInfoDto ToCallInfo(Call call) => new()
    {
        CallId = call.Id,
        IsVideo = call.IsVideo,
        Status = call.Status,
        DurationSeconds = CallStateMachine.DurationSeconds(call.AnsweredAt, call.EndedAt),
        InitiatorId = call.InitiatorId,
    };

    public static ChatPermissionsDto ToDto(GroupPermissions permissions) => new()
    {
        SendMessages = permissions.SendMessages,
        SendMedia = permissions.SendMedia,
        AddMembers = permissions.AddMembers,
        PinMessages = permissions.PinMessages,
        ChangeInfo = permissions.ChangeInfo,
    };
}
