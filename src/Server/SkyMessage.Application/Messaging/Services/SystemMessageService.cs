using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>
/// Posts service messages and call logs. Works in a fresh DI scope (own DbContext) so the caller's change
/// tracker is never flushed or polluted, runs its own transaction (seq + insert + chat aggregate), commits and
/// only then broadcasts MessageCreated (plus ChatAdded for members it unhides).
/// </summary>
internal sealed class SystemMessageService(
    IAppDbContext callerDb,
    IServiceScopeFactory scopeFactory,
    IRealtimeNotifier notifier,
    IClock clock,
    ILogger<SystemMessageService> logger) : ISystemMessageService
{
    public Task<MessageDto> PostSystemEventAsync(Guid chatId, SystemEventDto systemEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);
        var payload = new SystemEvent
        {
            Type = systemEvent.Type,
            ActorId = systemEvent.ActorId,
            TargetUserIds = systemEvent.TargetUserIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [],
            Text = systemEvent.Text,
            MessageId = systemEvent.MessageId,
            CallId = systemEvent.CallId,
        };
        return PostAsync(chatId, MessageType.System, senderId: null, readerId: systemEvent.ActorId,
            m => m.SystemEvent = payload, callLogId: null, cancellationToken);
    }

    public Task<MessageDto> PostCallLogAsync(Guid chatId, CallInfoDto call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var log = new CallLog
        {
            CallId = call.CallId,
            IsVideo = call.IsVideo,
            Status = call.Status,
            DurationSeconds = Math.Max(0, call.DurationSeconds),
            InitiatorId = call.InitiatorId,
        };
        return PostAsync(chatId, MessageType.Call, senderId: call.InitiatorId, readerId: call.InitiatorId,
            m => m.CallLog = log, callLogId: call.CallId, cancellationToken);
    }

    private async Task<MessageDto> PostAsync(
        Guid chatId,
        MessageType type,
        Guid? senderId,
        Guid? readerId,
        Action<Message> fill,
        Guid? callLogId,
        CancellationToken ct)
    {
        if (callerDb.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException(
                "ISystemMessageService must be called after your transaction committed (it runs its own transaction).");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var loader = new MessageDtoLoader(db);

        var now = clock.UtcNow;
        Guid messageId;
        List<Guid> unhidden = [];
        var created = false;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var seq = await ChatSequence.NextAsync(db, chatId, now, ct);

            // A call may be logged twice (hang-up racing a webhook): the chat row lock taken above serializes
            // writers, so the check below sees a committed earlier log.
            Guid? existing = null;
            if (callLogId is { } callId)
            {
                existing = await db.Messages.AsNoTracking()
                    .Where(m => m.ChatId == chatId && m.Type == MessageType.Call && m.CallLog != null && m.CallLog.CallId == callId)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefaultAsync(ct);
            }

            if (existing is { } existingId)
            {
                await tx.RollbackAsync(ct);
                messageId = existingId;
            }
            else
            {
                var message = new Message
                {
                    ChatId = chatId,
                    Seq = seq,
                    SenderId = senderId,
                    Type = type,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                fill(message);
                db.Messages.Add(message);
                await db.SaveChangesAsync(ct);
                messageId = message.Id;

                await db.Chats.Where(c => c.Id == chatId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastMessageId, messageId), ct);

                // The actor caused this entry: it is not unread for them (same as a sender's own message).
                if (readerId is { } reader)
                {
                    await db.ChatMembers.Where(m => m.ChatId == chatId && m.UserId == reader && m.LastReadSeq < seq)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, seq), ct);
                }

                // Like any new message, a call log brings a hidden private chat back.
                unhidden = await db.ChatMembers.Where(m => m.ChatId == chatId && m.IsHidden).Select(m => m.UserId).ToListAsync(ct);
                if (unhidden.Count > 0)
                {
                    await db.ChatMembers.Where(m => m.ChatId == chatId && m.IsHidden)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsHidden, false), ct);
                }

                await tx.CommitAsync(ct);
                created = true;
            }
        }

        var dto = await loader.LoadOneAsync(messageId, CancellationToken.None)
                  ?? throw new InvalidOperationException($"System message {messageId} vanished after commit.");
        if (!created) return dto;

        var memberIds = await db.ChatMembers.AsNoTracking().Where(m => m.ChatId == chatId).Select(m => m.UserId).ToListAsync(CancellationToken.None);
        await PublishAsync(memberIds, "MessageCreated", c => c.MessageCreated(dto));
        await PublishAsync(unhidden, "ChatAdded", c => c.ChatAdded(new ChatRefEvent { ChatId = chatId }));
        return dto;
    }

    private async Task PublishAsync(IReadOnlyCollection<Guid> userIds, string eventName, Func<IRealtimeClient, Task> send)
    {
        if (userIds.Count == 0) return;
        try
        {
            await send(notifier.Users(userIds));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Realtime event {Event} for a system message could not be published", eventName);
        }
    }
}
