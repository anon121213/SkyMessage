using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

// Edits, deletes, reactions and pins: applied optimistically, confirmed by the server copy (UpdatedAt rule), rolled back
// and rethrown (ApiException) when the server refuses. A rollback never overwrites a newer server copy.
internal sealed partial class MessageStore
{
    public async Task EditAsync(Guid chatId, Guid messageId, string text, IReadOnlyList<MessageEntityDto>? entities = null)
    {
        var pendingEdited = await MutateAsync(changes =>
        {
            // Still in the outbox: change what will be sent.
            var entry = _outbox.FirstOrDefault(e => e.Key == messageId && e.ChatId == chatId && !e.Removed);
            if (entry is null || entry.Record.Kind != OutboxKind.Message || entry.State == MessageSendState.Sending) return false;
            entry.Record = entry.Record with { Text = text, Entities = entities?.ToList() };
            entry.CachedItem = null;
            changes.Chats.Add(chatId);
            changes.Outbox = true;
            return true;
        }).ConfigureAwait(false);
        if (pendingEdited) return;

        var before = await MutateAsync(changes =>
        {
            if (!_windows.TryGetValue(chatId, out var window) || window.Get(messageId) is not { } held) return null;
            window.Upsert(held with { Text = text, Entities = entities, EditedAt = _time.GetUtcNow() });
            Touch(window, changes);
            return held;
        }).ConfigureAwait(false);

        try
        {
            var updated = await _api.EditMessageAsync(chatId, messageId, new EditMessageRequest { Text = text, Entities = entities }).ConfigureAwait(false);
            await ApplyServerCopyAsync(updated).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            if (before is not null) await RollbackAsync(before).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DeleteAsync(Guid chatId, IReadOnlyList<Guid> messageIds, bool forEveryone)
    {
        if (messageIds.Count == 0) return;
        var pendingKeys = await _queue.RunAsync(() => _outbox
                .Where(e => e.ChatId == chatId && messageIds.Contains(e.Key))
                .Select(e => e.Key)
                .ToList())
            .ConfigureAwait(false);
        foreach (var key in pendingKeys) await CancelAsync(chatId, key).ConfigureAwait(false);

        var confirmed = messageIds.Where(id => !pendingKeys.Contains(id)).Distinct().ToList();
        if (confirmed.Count == 0) return;

        var removed = await MutateAsync(changes =>
        {
            var list = new List<MessageDto>();
            if (_windows.TryGetValue(chatId, out var window))
            {
                foreach (var id in confirmed)
                {
                    if (window.Get(id) is { } held && window.Remove(id)) list.Add(held);
                }
                if (list.Count > 0) Touch(window, changes);
            }
            RemovePinnedCopies(chatId, confirmed, changes);
            return list;
        }).ConfigureAwait(false);

        // The server takes at most MaxMessagesPerBatch ids per request: a larger selection goes in chunks. After a
        // refused chunk the rest is not sent; only the messages that were not deleted come back.
        var deleted = 0;
        try
        {
            foreach (var chunk in confirmed.Chunk(Limits.MaxMessagesPerBatch))
            {
                await _api.DeleteMessagesAsync(chatId, new DeleteMessagesRequest { MessageIds = chunk, ForEveryone = forEveryone }).ConfigureAwait(false);
                deleted += chunk.Length;
            }
        }
        catch (ApiException)
        {
            var kept = confirmed.Skip(deleted).ToHashSet();
            await MutateAsync(changes =>
            {
                if (!_windows.TryGetValue(chatId, out var window)) return;
                foreach (var message in removed)
                    if (kept.Contains(message.Id) && window.BelongsInWindow(message.Seq)) window.Upsert(message);
                Touch(window, changes);
            }).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SetReactionAsync(Guid chatId, Guid messageId, string? emoji)
    {
        var next = string.IsNullOrWhiteSpace(emoji) ? null : emoji.Trim();
        if (Me is not { } me) return;

        var snapshot = await MutateAsync(changes =>
        {
            if (!_windows.TryGetValue(chatId, out var window) || window.Get(messageId) is not { } held) return null;
            var previous = window.MyReactions.GetValueOrDefault(messageId);
            if (previous == next) return null;
            window.Upsert(held with { Reactions = MessageRules.ApplyMyReaction(held.Reactions, me, previous, next) });
            if (next is null) window.MyReactions.Remove(messageId);
            else window.MyReactions[messageId] = next;
            Touch(window, changes);
            return new ReactionSnapshot(held, previous);
        }).ConfigureAwait(false);

        try
        {
            var updated = next is null
                ? await _api.RemoveReactionAsync(chatId, messageId).ConfigureAwait(false)
                : await _api.SetReactionAsync(chatId, messageId, next).ConfigureAwait(false);
            await MutateAsync(changes =>
            {
                // Only a held copy is refreshed: the window may have been replaced meanwhile (jump), and inserting the
                // message there would stretch MinSeq/MaxSeq over messages that were never loaded.
                if (!_windows.TryGetValue(chatId, out var window) || !window.Contains(messageId)) return;
                window.Upsert(updated);
                if (window.Contains(messageId))
                {
                    if (next is null) window.MyReactions.Remove(messageId);
                    else window.MyReactions[messageId] = next;
                }
                Touch(window, changes);
            }).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            if (snapshot is not null)
            {
                await MutateAsync(changes =>
                {
                    if (!_windows.TryGetValue(chatId, out var window) || window.Get(messageId) is not { } current) return;
                    if (current.UpdatedAt > snapshot.Message.UpdatedAt) return;
                    window.Upsert(snapshot.Message);
                    if (snapshot.Previous is null) window.MyReactions.Remove(messageId);
                    else window.MyReactions[messageId] = snapshot.Previous;
                    Touch(window, changes);
                }).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task SetPinnedAsync(Guid chatId, Guid messageId, bool pinned)
    {
        var before = await MutateAsync(changes =>
        {
            if (!_windows.TryGetValue(chatId, out var window) || window.Get(messageId) is not { } held || held.IsPinned == pinned) return null;
            var optimistic = held with { IsPinned = pinned };
            window.Upsert(optimistic);
            UpdatePinnedCopy(optimistic, changes);
            Touch(window, changes);
            return held;
        }).ConfigureAwait(false);

        try
        {
            if (pinned) await _api.PinMessageAsync(chatId, messageId).ConfigureAwait(false);
            else await _api.UnpinMessageAsync(chatId, messageId).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            if (before is not null) await RollbackAsync(before).ConfigureAwait(false);
            throw;
        }
        // The server follows up with MessageUpdated + PinnedMessagesChanged (system message included).
    }

    private Task ApplyServerCopyAsync(MessageDto message) => MutateAsync(changes =>
    {
        UpdatePinnedCopy(message, changes);
        if (!_windows.TryGetValue(message.ChatId, out var window)) return;
        ApplyUpdated(window, message, changes);
    });

    /// <summary>Restores <paramref name="original"/> unless a newer server copy arrived meanwhile.</summary>
    private Task RollbackAsync(MessageDto original) => MutateAsync(changes =>
    {
        if (!_windows.TryGetValue(original.ChatId, out var window) || window.Get(original.Id) is not { } current) return;
        if (current.UpdatedAt > original.UpdatedAt) return;
        window.Upsert(original);
        UpdatePinnedCopy(original, changes);
        Touch(window, changes);
    });

    private sealed record ReactionSnapshot(MessageDto Message, string? Previous);
}
