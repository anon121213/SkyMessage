using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

// Windows: open, paging, jumps and the lazy catch-up (afterSeq pages + /changes from the stored cursor).
internal sealed partial class MessageStore
{
    /// <summary>More new messages than this since the cached window: start over around the first unread instead.</summary>
    private const long MaxCatchUpGap = 300;
    private const int MaxCatchUpPages = 3;
    private const int MaxChangesPages = 20;

    // Guarded by _lock.
    private readonly HashSet<Guid> _gapFills = [];
    /// <summary>The catch-up running per chat (completes when it ended); later callers wait for it instead of fetching twice.</summary>
    private readonly Dictionary<Guid, Task> _catchUps = [];
    /// <summary>The JumpToLatestAsync load running per chat (same single-flight rule).</summary>
    private readonly Dictionary<Guid, Task> _latestLoads = [];

    private enum OpenPlan
    {
        None,
        Load,
        CatchUp,
    }

    public async Task OpenAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        await EnsureWindowAsync(chatId).ConfigureAwait(false);
        var chat = _chats.Get(chatId);
        if (chat is null)
        {
            try
            {
                chat = await _chats.GetOrFetchAsync(chatId, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                _logger.LogDebug("Chat {ChatId} summary unavailable ({Code})", chatId, ex.Code);
            }
        }

        var plan = await MutateAsync(changes =>
        {
            var window = EnsureWindow(chatId);
            if (chat is not null) window.ClearUpTo(chat.ClearedUpToSeq);
            window.FirstUnreadSeq = FirstUnread(window, chat);
            changes.Chats.Add(chatId);
            if (!window.IsInitialized || window.Count == 0 || window.HasMoreAfter) return OpenPlan.Load;
            if (chat is not null && window.MaxSeq is { } max && chat.LastMessageSeq - max > MaxCatchUpGap) return OpenPlan.Load;
            if (window.NeedsCatchUp || window.SyncCursor is null) return OpenPlan.CatchUp;
            return OpenPlan.None;
        }).ConfigureAwait(false);

        switch (plan)
        {
            case OpenPlan.Load:
                var around = chat is { UnreadCount: > 0, MyLastReadSeq: > 0 } ? chat.MyLastReadSeq + 1 : (long?)null;
                await LoadReplacingAsync(chatId, around, latest: around is null, cancellationToken).ConfigureAwait(false);
                break;
            case OpenPlan.CatchUp:
                await CatchUpAsync(chatId, fromResync: false, cancellationToken).ConfigureAwait(false);
                break;
        }

        if (plan != OpenPlan.None)
        {
            await MutateAsync(changes =>
            {
                if (!_windows.TryGetValue(chatId, out var window)) return;
                window.FirstUnreadSeq = FirstUnread(window, _chats.Get(chatId) ?? chat);
                changes.Chats.Add(chatId);
            }).ConfigureAwait(false);
        }
    }

    public async Task LoadOlderAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var start = await MutateAsync(changes =>
        {
            if (!_windows.TryGetValue(chatId, out var window) || !window.IsInitialized || !window.HasMoreBefore
                || window.IsLoadingBefore || window.MinSeq is not { } min)
            {
                return ((long Seq, int Epoch)?)null;
            }
            window.IsLoadingBefore = true;
            window.LoadDepth++;
            changes.Chats.Add(chatId);
            return (min, window.Epoch);
        }).ConfigureAwait(false);
        if (start is not { } s) return;

        MessagesPageDto? page = null;
        using var link = Link(cancellationToken);
        try
        {
            page = await _api.GetMessagesAsync(chatId, beforeSeq: s.Seq, ct: link.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            _logger.LogDebug("Older messages of {ChatId} not loaded", chatId);
        }
        finally
        {
            await MutateAsync(changes =>
            {
                if (!_windows.TryGetValue(chatId, out var window)) return;
                window.IsLoadingBefore = false;
                if (page is not null && window.Epoch == s.Epoch) window.ApplyPage(page, older: true, newer: false);
                EndLoad(window, changes);
                Touch(window, changes);
            }).ConfigureAwait(false);
        }
        if (page is not null) _users.Upsert(page.Users);
    }

    public async Task LoadNewerAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var start = await MutateAsync(changes =>
        {
            if (!_windows.TryGetValue(chatId, out var window) || !window.IsInitialized || !window.HasMoreAfter
                || window.IsLoadingAfter || window.MaxSeq is not { } max)
            {
                return ((long Seq, int Epoch)?)null;
            }
            window.IsLoadingAfter = true;
            window.LoadDepth++;
            changes.Chats.Add(chatId);
            return (max, window.Epoch);
        }).ConfigureAwait(false);
        if (start is not { } s) return;

        MessagesPageDto? page = null;
        using var link = Link(cancellationToken);
        try
        {
            page = await _api.GetMessagesAsync(chatId, afterSeq: s.Seq, ct: link.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            _logger.LogDebug("Newer messages of {ChatId} not loaded", chatId);
        }
        finally
        {
            await MutateAsync(changes =>
            {
                if (!_windows.TryGetValue(chatId, out var window)) return;
                window.IsLoadingAfter = false;
                if (page is not null && window.Epoch == s.Epoch) window.ApplyPage(page, older: false, newer: true);
                EndLoad(window, changes);
                Touch(window, changes);
            }).ConfigureAwait(false);
        }
        if (page is not null) _users.Upsert(page.Users);
    }

    public async Task JumpToAsync(Guid chatId, long seq, CancellationToken cancellationToken = default)
    {
        await EnsureWindowAsync(chatId).ConfigureAwait(false);
        var newest = _chats.Get(chatId)?.LastMessageSeq;
        var loaded = await MutateAsync(changes =>
        {
            if (!_windows.TryGetValue(chatId, out var window) || !window.IsInitialized) return false;
            if (window.MinSeq is not { } min || window.MaxSeq is not { } max) return false;
            if (seq < min || seq > max) return false;
            // A cached window far behind the chat is not worth a catch-up: a fresh page around the target replaces it.
            if (window.NeedsCatchUp && !window.HasMoreAfter && newest - max > MaxCatchUpGap) return false;
            changes.Jumps.Add((chatId, seq));
            return true;
        }).ConfigureAwait(false);
        if (loaded)
        {
            // Opening a chat at a search hit never runs OpenAsync: a window restored from the cache (or left behind by
            // a resync) catches up here, after the jump, so a slow link does not hold the jump back.
            CatchUpIfPendingAsync(chatId).Forget(_logger);
            return;
        }

        if (await LoadReplacingAsync(chatId, seq, latest: false, cancellationToken).ConfigureAwait(false))
            await MutateAsync(changes => changes.Jumps.Add((chatId, seq))).ConfigureAwait(false);
    }

    public async Task JumpToLatestAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var behind = await _queue.RunAsync(() => _windows.TryGetValue(chatId, out var window) && (window.HasMoreAfter || !window.IsInitialized))
            .ConfigureAwait(false);
        if (!behind) return;

        // A send already moves the window there in the background, and the screen asks for the same right after it:
        // wait for that load instead of fetching the page twice.
        var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? running;
        lock (_lock)
        {
            if (!_latestLoads.TryGetValue(chatId, out running)) _latestLoads[chatId] = run.Task;
        }
        if (running is not null)
        {
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            await LoadReplacingAsync(chatId, null, latest: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (_latestLoads.TryGetValue(chatId, out var current) && current == run.Task) _latestLoads.Remove(chatId);
            }
            run.TrySetResult();
        }
    }

    public async Task LoadPinnedAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        lock (_lock) _pinnedLoaded.Add(chatId);
        var generation = _generation;
        IReadOnlyList<MessageDto> pinned;
        using var link = Link(cancellationToken);
        try
        {
            pinned = await _api.GetPinnedMessagesAsync(chatId, link.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            _logger.LogDebug("Pinned messages of {ChatId} not loaded", chatId);
            return;
        }
        if (generation != _generation) return;
        await MutateAsync(changes =>
        {
            _pinned[chatId] = pinned;
            changes.Chats.Add(chatId);
            changes.Windows.Add(chatId);
        }).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<long>> GetUnreadMentionsAsync(Guid chatId, CancellationToken cancellationToken = default) =>
        _api.GetUnreadMentionsAsync(chatId, cancellationToken);

    public async Task<MessagesPageDto> SearchAsync(Guid chatId, string query, CancellationToken cancellationToken = default)
    {
        var page = await _api.SearchInChatAsync(chatId, query, ct: cancellationToken).ConfigureAwait(false);
        _users.Upsert(page.Users);
        return page;
    }

    /// <summary>Replaces the window with a page around <paramref name="aroundSeq"/> (or the latest page). True when applied.</summary>
    private async Task<bool> LoadReplacingAsync(Guid chatId, long? aroundSeq, bool latest, CancellationToken cancellationToken)
    {
        var epoch = await MutateAsync(changes =>
        {
            var window = EnsureWindow(chatId);
            window.LoadDepth++;
            if (latest) window.IsLoadingAfter = true;
            else window.IsLoadingBefore = window.IsLoadingAfter = true;
            changes.Chats.Add(chatId);
            return window.BeginReplace();
        }).ConfigureAwait(false);

        MessagesPageDto? page = null;
        using (var link = Link(cancellationToken))
        {
            try
            {
                page = latest
                    ? await _api.GetMessagesAsync(chatId, ct: link.Token).ConfigureAwait(false)
                    : await _api.GetMessagesAsync(chatId, aroundSeq: aroundSeq, ct: link.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException)
            {
                _logger.LogDebug("Messages of {ChatId} not loaded ({Error})", chatId, (ex as ApiException)?.Code ?? "cancelled");
            }
            catch (Exception ex)
            {
                // Any other failure must still end the load below (a raised LoadDepth buffers events forever).
                _logger.LogWarning(ex, "Messages of {ChatId} not loaded", chatId);
            }
        }

        var applied = await MutateAsync(changes =>
        {
            if (!_windows.TryGetValue(chatId, out var window)) return false;
            window.IsLoadingBefore = window.IsLoadingAfter = false;
            var apply = page is not null && window.Epoch == epoch;
            if (apply)
            {
                window.Reset();
                window.ApplyPage(page!, older: true, newer: true);
                window.SyncCursor = page!.SyncCursor;
                foreach (var message in page.Messages)
                    if (message.ClientMessageId is not null) ConfirmPendingByServerCopy(message, window, changes);
            }
            EndLoad(window, changes);
            Touch(window, changes);
            return apply;
        }).ConfigureAwait(false);
        if (page is not null) _users.Upsert(page.Users);
        return applied;
    }

    /// <summary>
    /// Brings a window that sits at the newest message up to date (see <see cref="CatchUpOnceAsync"/>). One run per
    /// chat at a time: a caller arriving meanwhile waits for it; a resync then still starts its own run, because the
    /// one that ended may have fetched before the events the reconnect lost. A run that leaves the chat on screen
    /// behind (failed request) is retried with growing pauses while the chat stays on screen.
    /// </summary>
    private async Task CatchUpAsync(Guid chatId, bool fromResync, CancellationToken cancellationToken, int attempt = 1)
    {
        var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        while (true)
        {
            Task? running;
            lock (_lock)
            {
                if (!_catchUps.TryGetValue(chatId, out running)) _catchUps[chatId] = run.Task;
            }
            if (running is null) break;
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!fromResync) return;
        }

        var generation = _generation;
        try
        {
            await CatchUpOnceAsync(chatId, fromResync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (_catchUps.TryGetValue(chatId, out var current) && current == run.Task) _catchUps.Remove(chatId);
            }
            run.TrySetResult();
        }

        if (generation == _generation && _chats.ActiveChatId == chatId && await IsCatchUpPendingAsync(chatId).ConfigureAwait(false))
            RetryCatchUpLaterAsync(chatId, attempt).Forget(_logger);
    }

    /// <summary>The window sits at the newest message but still waits for a catch-up (cache restore, resync, failed run).</summary>
    private Task<bool> IsCatchUpPendingAsync(Guid chatId) => _queue.RunAsync(() =>
        _windows.TryGetValue(chatId, out var window) && window.IsInitialized && !window.HasMoreAfter && window.NeedsCatchUp);

    private async Task CatchUpIfPendingAsync(Guid chatId, int attempt = 1)
    {
        if (await IsCatchUpPendingAsync(chatId).ConfigureAwait(false))
            await CatchUpAsync(chatId, fromResync: false, CancellationToken.None, attempt).ConfigureAwait(false);
    }

    private async Task RetryCatchUpLaterAsync(Guid chatId, int attempt)
    {
        var generation = _generation;
        await Task.Delay(RetryBackoff[Math.Min(attempt, RetryBackoff.Length) - 1], _time).ConfigureAwait(false);
        if (generation != _generation || _chats.ActiveChatId != chatId) return;
        await CatchUpIfPendingAsync(chatId, attempt + 1).ConfigureAwait(false);
    }

    /// <summary>
    /// One catch-up run: afterSeq pages for new messages, then /changes from the stored cursor for edits, tombstones,
    /// "deleted for me" and the clear-history watermark (it also brings the messages of a hole inside the window).
    /// </summary>
    private async Task CatchUpOnceAsync(Guid chatId, bool fromResync, CancellationToken cancellationToken)
    {
        using var sync = fromResync ? _realtime.BeginSync() : null;
        var start = await MutateAsync(changes =>
        {
            if (!_windows.TryGetValue(chatId, out var window) || !window.IsInitialized || window.HasMoreAfter) return null;
            window.LoadDepth++;
            window.IsLoadingAfter = true;
            changes.Chats.Add(chatId);
            return new CatchUpStart(window.MaxSeq, window.SyncCursor, window.Epoch, _resyncs);
        }).ConfigureAwait(false);
        if (start is null)
        {
            // Not loaded yet (or behind after a jump): a normal open handles it.
            var known = await _queue.RunAsync(() => _windows.TryGetValue(chatId, out var w) && !w.IsInitialized).ConfigureAwait(false);
            if (known && fromResync) await LoadReplacingAsync(chatId, null, latest: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var link = Link(cancellationToken);
        var token = link.Token;
        var pages = new List<MessagesPageDto>();
        var changesPages = new List<MessageChangesDto>();
        var cursor = start.Cursor;
        var complete = false;
        var resetNeeded = start.MaxSeq is null || start.Cursor is null;
        try
        {
            if (!resetNeeded)
            {
                var after = start.MaxSeq!.Value;
                for (var i = 0; i < MaxCatchUpPages; i++)
                {
                    var page = await _api.GetMessagesAsync(chatId, afterSeq: after, limit: Limits.MessagesPageMax, ct: token).ConfigureAwait(false);
                    pages.Add(page);
                    if (page.Messages.Count > 0) after = page.Messages[^1].Seq;
                    if (!page.HasMoreAfter) break;
                }
                resetNeeded = pages.Count > 0 && pages[^1].HasMoreAfter;
            }

            if (!resetNeeded)
            {
                for (var i = 0; i < MaxChangesPages; i++)
                {
                    var changes = await _api.GetMessageChangesAsync(chatId, cursor!, token).ConfigureAwait(false);
                    changesPages.Add(changes);
                    cursor = changes.NextCursor;
                    if (!changes.HasMore)
                    {
                        complete = true;
                        break;
                    }
                }
            }
        }
        catch (ApiException ex) when (ex.StatusCode == 400)
        {
            // The cursor is no longer understood: start the window over.
            resetNeeded = true;
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            _logger.LogDebug("Catch-up of {ChatId} incomplete ({Error})", chatId, (ex as ApiException)?.Code ?? "cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catch-up of {ChatId} failed", chatId);
        }

        await MutateAsync(changes =>
        {
            if (!_windows.TryGetValue(chatId, out var window)) return;
            window.IsLoadingAfter = false;
            if (window.Epoch == start.Epoch && !resetNeeded)
            {
                foreach (var page in pages)
                {
                    window.ApplyPage(page, older: false, newer: true);
                    foreach (var message in page.Messages)
                        if (message.ClientMessageId is not null) ConfirmPendingByServerCopy(message, window, changes);
                }
                if (Me is { } me)
                    foreach (var delta in changesPages) window.ApplyChanges(delta, me);
                if (cursor is not null && changesPages.Count > 0) window.SyncCursor = cursor;
                // A resync during the run lost events after what it fetched: still due.
                window.NeedsCatchUp = !complete || _resyncs != start.Resyncs;
            }
            EndLoad(window, changes);
            Touch(window, changes);
        }).ConfigureAwait(false);

        foreach (var page in pages) _users.Upsert(page.Users);
        if (resetNeeded)
        {
            // Starting over at the newest page would skip everything in between (and the screen then marks it read).
            var newestSeen = pages.LastOrDefault(p => p.Messages.Count > 0)?.Messages[^1].Seq;
            var around = StartOverAt(chatId, newestSeen);
            await LoadReplacingAsync(chatId, around, latest: around is null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Where a window that starts over goes: around the first unread, as a fresh open does, else to the newest page.
    /// The summary may still be the cached one (cold start): messages fetched past its read mark mean unread ones
    /// whatever its count says.
    /// </summary>
    private long? StartOverAt(Guid chatId, long? newestSeen)
    {
        if (_chats.Get(chatId) is not { MyLastReadSeq: > 0 } chat) return null;
        var read = Math.Max(chat.MyLastReadSeq, chat.ClearedUpToSeq);
        return chat.UnreadCount > 0 || newestSeen > read ? read + 1 : null;
    }

    private sealed record CatchUpStart(long? MaxSeq, string? Cursor, int Epoch, int Resyncs = 0);

    /// <summary>A realtime message arrived past the newest held one: fetch what lies between.</summary>
    private void ScheduleGapFill(Guid chatId)
    {
        lock (_lock)
        {
            if (!_gapFills.Add(chatId)) return;
        }
        FillGapAsync(chatId).Forget(_logger);
    }

    private async Task FillGapAsync(Guid chatId)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), _time).ConfigureAwait(false);
            var start = await MutateAsync(changes =>
            {
                if (!_windows.TryGetValue(chatId, out var window) || !window.IsInitialized || window.HasMoreAfter) return null;
                var messages = window.Messages;
                // The first hole from the newest end: the message right before it is where paging starts.
                long? from = null;
                for (var i = messages.Count - 1; i > 0; i--)
                {
                    if (messages[i].Seq - messages[i - 1].Seq > 1)
                    {
                        from = messages[i - 1].Seq;
                        break;
                    }
                }
                if (from is null) return null;
                window.LoadDepth++;
                return new CatchUpStart(from, null, window.Epoch);
            }).ConfigureAwait(false);
            if (start?.MaxSeq is not { } fromSeq) return;

            MessagesPageDto? page = null;
            try
            {
                page = await _api.GetMessagesAsync(chatId, afterSeq: fromSeq, limit: Limits.MessagesPageMax, ct: _lifecycle.AccountToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException)
            {
                _logger.LogDebug("Gap fill of {ChatId} failed", chatId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gap fill of {ChatId} failed", chatId);
            }

            await MutateAsync(changes =>
            {
                if (!_windows.TryGetValue(chatId, out var window)) return;
                if (page is not null && window.Epoch == start.Epoch)
                {
                    foreach (var message in page.Messages)
                    {
                        if (message.ClientMessageId is not null) ConfirmPendingByServerCopy(message, window, changes);
                        window.Upsert(message);
                    }
                    foreach (var (id, emoji) in page.MyReactions)
                        if (window.Contains(id)) window.MyReactions[id] = emoji;
                }
                EndLoad(window, changes);
                Touch(window, changes);
            }).ConfigureAwait(false);
            if (page is not null) _users.Upsert(page.Users);
        }
        finally
        {
            lock (_lock) _gapFills.Remove(chatId);
        }
    }

    /// <summary>
    /// First unread message for the "Unread messages" divider (fixed while the chat stays open). A held message past
    /// the read mark counts even when the summary has no unread: it may still be the cached one (cold start).
    /// </summary>
    private long? FirstUnread(MessageWindow window, ChatSummaryDto? chat)
    {
        if (chat is null) return null;
        var read = Math.Max(chat.MyLastReadSeq, chat.ClearedUpToSeq);
        var me = Me;
        foreach (var message in window.Messages)
            if (message.Seq > read && !MessageRules.IsMine(message, me)) return message.Seq;
        return chat.UnreadCount > 0 ? read + 1 : null;
    }

    /// <summary>The caller's token combined with the account token (disposed by the caller: it registers on the account token).</summary>
    private LinkedToken Link(CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? new LinkedToken(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifecycle.AccountToken))
            : new LinkedToken(null, _lifecycle.AccountToken);

    private readonly struct LinkedToken : IDisposable
    {
        private readonly CancellationTokenSource? _source;

        public LinkedToken(CancellationTokenSource source)
        {
            _source = source;
            Token = source.Token;
        }

        public LinkedToken(CancellationTokenSource? source, CancellationToken token)
        {
            _source = source;
            Token = token;
        }

        public CancellationToken Token { get; }

        public void Dispose() => _source?.Dispose();
    }
}
