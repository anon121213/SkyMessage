using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

/// <summary>
/// The loaded slice of one chat's history (mutable; only touched inside the message store's queue). Holds the merge
/// rules: a message replaces the held copy only when <c>incoming.UpdatedAt &gt;= held.UpdatedAt</c>, tombstones and
/// "hidden for me" ids remove, and nothing at or below <see cref="ClearedUpToSeq"/> survives.
/// </summary>
internal sealed class MessageWindow(Guid chatId)
{
    private readonly SortedList<long, MessageDto> _bySeq = new();
    private readonly Dictionary<Guid, long> _seqById = [];

    public Guid ChatId { get; } = chatId;
    public bool IsInitialized { get; set; }
    public bool HasMoreBefore { get; set; }
    public bool HasMoreAfter { get; set; }
    public bool IsLoadingBefore { get; set; }
    public bool IsLoadingAfter { get; set; }
    public long? FirstUnreadSeq { get; set; }
    /// <summary>Cursor for GET .../messages/changes: the oldest read time of everything held.</summary>
    public string? SyncCursor { get; set; }
    public long ClearedUpToSeq { get; private set; }
    /// <summary>Restored from the cache (or resync happened while not on screen): catch up via /changes when opened.</summary>
    public bool NeedsCatchUp { get; set; }
    public bool CacheChecked { get; set; }
    public Dictionary<Guid, string> MyReactions { get; } = [];
    /// <summary>Message id → ClientMessageId for messages sent from this device (stable render keys).</summary>
    public Dictionary<Guid, Guid> LocalKeys { get; } = [];
    /// <summary>Loads in flight; realtime mutations are buffered meanwhile and replayed afterwards.</summary>
    public int LoadDepth { get; set; }
    /// <summary>When the chat stopped being the one on screen (TimeProvider timestamp); null = not since this window exists.</summary>
    public long? LeftScreenAt { get; set; }
    /// <summary>
    /// Bumped when a replacing load (open, jump, reset) starts: results of loads started earlier are dropped, so the
    /// most recent request wins even when responses arrive out of order.
    /// </summary>
    public int Epoch { get; private set; }

    public int BeginReplace() => ++Epoch;

    public int Count => _bySeq.Count;
    public IList<MessageDto> Messages => _bySeq.Values;
    public long? MinSeq => _bySeq.Count > 0 ? _bySeq.Keys[0] : null;
    public long? MaxSeq => _bySeq.Count > 0 ? _bySeq.Keys[^1] : null;

    public MessageDto? Get(Guid messageId) => _seqById.TryGetValue(messageId, out var seq) ? _bySeq[seq] : null;

    public bool Contains(Guid messageId) => _seqById.ContainsKey(messageId);

    /// <summary>Empties the window before a replacing page is applied (initial load, jump, reset).</summary>
    public void Reset()
    {
        _bySeq.Clear();
        _seqById.Clear();
        MyReactions.Clear();
        HasMoreBefore = false;
        HasMoreAfter = false;
        SyncCursor = null;
        NeedsCatchUp = false;
    }

    /// <summary>Inserts or merges one confirmed message. Returns true when the window changed.</summary>
    public bool Upsert(MessageDto message)
    {
        if (message.ChatId != ChatId && message.ChatId != Guid.Empty) return false;
        if (message.IsDeleted) return Remove(message.Id);
        if (message.Seq <= ClearedUpToSeq) return Remove(message.Id);

        if (_seqById.TryGetValue(message.Id, out var heldSeq))
        {
            var held = _bySeq[heldSeq];
            if (message.UpdatedAt < held.UpdatedAt) return false;
            if (message == held) return false;
            if (heldSeq != message.Seq)
            {
                _bySeq.Remove(heldSeq);
                _seqById[message.Id] = message.Seq;
            }
            _bySeq[message.Seq] = message;
            return true;
        }

        if (_bySeq.TryGetValue(message.Seq, out var occupant) && occupant.Id != message.Id)
        {
            // Seqs are unique per chat: the newer copy wins.
            if (message.UpdatedAt < occupant.UpdatedAt) return false;
            _seqById.Remove(occupant.Id);
        }
        _bySeq[message.Seq] = message;
        _seqById[message.Id] = message.Seq;
        return true;
    }

    public bool Remove(Guid messageId)
    {
        if (!_seqById.Remove(messageId, out var seq)) return false;
        _bySeq.Remove(seq);
        MyReactions.Remove(messageId);
        return true;
    }

    /// <summary>
    /// Keeps only the newest <paramref name="keep"/> messages (older ones load again when scrolled to) and forgets the
    /// reactions and render keys of everything no longer held. True when messages were dropped.
    /// </summary>
    public bool TrimToNewest(int keep)
    {
        if (_bySeq.Count <= keep) return false;
        while (_bySeq.Count > keep)
        {
            var message = _bySeq.Values[0];
            _bySeq.RemoveAt(0);
            _seqById.Remove(message.Id);
            MyReactions.Remove(message.Id);
        }
        // Render keys of messages confirmed while the window was elsewhere would otherwise stay forever.
        foreach (var id in LocalKeys.Keys.Where(id => !_seqById.ContainsKey(id)).ToList()) LocalKeys.Remove(id);
        HasMoreBefore = true;
        return true;
    }

    /// <summary>"Clear history": drops everything with Seq &lt;= <paramref name="upToSeq"/> (watermark only moves up).</summary>
    public bool ClearUpTo(long upToSeq)
    {
        if (upToSeq <= ClearedUpToSeq) return false;
        ClearedUpToSeq = upToSeq;
        var removed = false;
        while (_bySeq.Count > 0 && _bySeq.Keys[0] <= upToSeq)
        {
            var message = _bySeq.Values[0];
            _bySeq.RemoveAt(0);
            _seqById.Remove(message.Id);
            MyReactions.Remove(message.Id);
            removed = true;
        }
        if (_bySeq.Count == 0) HasMoreBefore = false;
        return removed;
    }

    /// <summary>
    /// Applies a page that is contiguous with the window (or replaces it after <see cref="Reset"/>). Messages already
    /// held merge by UpdatedAt.
    /// </summary>
    public void ApplyPage(MessagesPageDto page, bool older, bool newer)
    {
        foreach (var message in page.Messages)
            Upsert(message);
        // The page's reaction map is authoritative for its messages (reactor lists are capped).
        foreach (var message in page.Messages)
        {
            if (!Contains(message.Id)) continue;
            if (page.MyReactions.TryGetValue(message.Id, out var emoji)) MyReactions[message.Id] = emoji;
            else MyReactions.Remove(message.Id);
        }
        if (older) HasMoreBefore = page.HasMoreBefore;
        if (newer) HasMoreAfter = page.HasMoreAfter;
        IsInitialized = true;
    }

    /// <summary>
    /// Applies GET .../changes: tombstones and hidden ids remove, held messages merge, new messages are taken when the
    /// window is at the newest message, the clear-history watermark drops old ones, and reply quotes follow the
    /// messages they quote.
    /// </summary>
    public bool ApplyChanges(MessageChangesDto changes, Guid me)
    {
        var changed = ClearUpTo(changes.ClearedUpToSeq);
        foreach (var message in changes.Messages)
        {
            if (message.IsDeleted)
            {
                changed |= Remove(message.Id);
                continue;
            }
            if (Contains(message.Id) || BelongsInWindow(message.Seq))
            {
                var previous = MyReactions.GetValueOrDefault(message.Id);
                changed |= Upsert(message);
                if (Contains(message.Id))
                {
                    var mine = MessageRules.MyReaction(message, me, previous);
                    if (mine is null) MyReactions.Remove(message.Id);
                    else MyReactions[message.Id] = mine;
                }
            }
        }
        foreach (var hidden in changes.HiddenMessageIds)
            changed |= Remove(hidden);
        changed |= RefreshQuotes(changes.Messages);
        return changed;
    }

    /// <summary>
    /// Quotes of held replies follow the quoted messages (edited, or deleted for everyone, held here or not): the
    /// replies themselves did not change, so a delta does not bring them. Same result as the realtime path; a message
    /// hidden only for me stays quoted (the server quotes it too).
    /// </summary>
    private bool RefreshQuotes(IReadOnlyList<MessageDto> quoted)
    {
        if (quoted.Count == 0 || _bySeq.Count == 0) return false;
        var byId = new Dictionary<Guid, MessageDto>(quoted.Count);
        foreach (var message in quoted) byId[message.Id] = message;
        var changed = false;
        foreach (var held in _bySeq.Values.ToList())
        {
            if (held.ReplyTo is not { } reply || !byId.TryGetValue(reply.MessageId, out var original)) continue;
            var preview = original.IsDeleted ? reply with { IsDeleted = true, Text = null, Media = null } : MessageRules.ReplyPreview(original);
            if (preview == reply) continue;
            changed |= Upsert(held with { ReplyTo = preview });
        }
        return changed;
    }

    /// <summary>Would a message with this seq be inside the loaded range (or extend it at the newest end)?</summary>
    public bool BelongsInWindow(long seq)
    {
        if (seq <= ClearedUpToSeq) return false;
        if (_bySeq.Count == 0) return IsInitialized && !HasMoreAfter;
        var min = _bySeq.Keys[0];
        var max = _bySeq.Keys[^1];
        if (seq >= min && seq <= max) return true;
        if (seq > max) return !HasMoreAfter;
        return !HasMoreBefore;
    }

    /// <summary>Messages after the newest held one are missing (realtime event out of order or a missed one).</summary>
    public bool HasGapBefore(long seq) => MaxSeq is { } max && seq > max + 1;
}
