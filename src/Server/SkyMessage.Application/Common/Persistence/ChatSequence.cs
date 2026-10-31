using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;

namespace SkyMessage.Application.Common.Persistence;

/// <summary>
/// Gap-free per-chat message sequence. Must run inside an explicit transaction that also inserts the
/// message (roll back = no gap). The UPDATE takes the chat row lock, which also serializes other
/// aggregate changes of that chat (LastMessageId, MemberCount) done in the same transaction.
/// <code>
/// await using var tx = await db.Database.BeginTransactionAsync(ct);
/// var seq = await ChatSequence.NextAsync(db, chatId, now, ct);
/// // insert message with Seq = seq, update members ... ; await db.SaveChangesAsync(ct);
/// await tx.CommitAsync(ct);   // then publish realtime events
/// </code>
/// </summary>
public static class ChatSequence
{
    public static async Task<long> NextAsync(IAppDbContext db, Guid chatId, DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("ChatSequence.NextAsync requires an explicit transaction.");

        // SqlQuery over non-composable SQL: materialize with ToListAsync, never Single/First (they compose).
        var rows = await db.Database
            .SqlQuery<long>($"UPDATE chats SET last_message_seq = last_message_seq + 1, last_activity_at = {now}, updated_at = {now} WHERE id = {chatId} RETURNING last_message_seq AS \"Value\"")
            .ToListAsync(ct);
        return rows.Count == 1 ? rows[0] : throw new Exceptions.NotFoundException(Contracts.ErrorCodes.ChatNotFound);
    }

    /// <summary>Locks the chat row for the rest of the transaction (use before read-modify-write of chat aggregates).</summary>
    public static async Task LockChatAsync(IAppDbContext db, Guid chatId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("ChatSequence.LockChatAsync requires an explicit transaction.");
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM chats WHERE id = {chatId} FOR UPDATE").ToListAsync(ct);
    }
}
