using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Stores;

// The outbox: pending items, preparation (probe → compression → durable copy), chunked upload and ordered sending.
// Per chat, ready items are sent in creation order; an item still uploading does not hold back the ones behind it
// (except the rest of its own long caption), a ready item waiting to retry does. Transient failures → Queued (retried
// with backoff, at once when the network or realtime comes back); permanent ones → Failed.
internal sealed partial class MessageStore
{
    /// <summary>Items compressing at a time (CPU and memory); nothing else waits for these slots.</summary>
    private const int MaxConcurrentPreparations = 2;
    /// <summary>Attachments uploading at a time (each with up to 3 chunks in flight); voice and video notes never wait.</summary>
    private const int MaxConcurrentUploads = 2;
    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    ];

    private readonly SemaphoreSlim _preparationSlots = new(MaxConcurrentPreparations, MaxConcurrentPreparations);
    private readonly SemaphoreSlim _uploadSlots = new(MaxConcurrentUploads, MaxConcurrentUploads);

    // Guarded by _lock.
    private readonly HashSet<Guid> _draining = [];
    private readonly HashSet<Guid> _drainAgain = [];

    public async Task SendTextAsync(Guid chatId, string text, Guid? replyToMessageId = null, IReadOnlyList<MessageEntityDto>? entities = null)
    {
        if (string.IsNullOrWhiteSpace(text) || Me is null) return;
        var parts = TextSplitter.Split(text, entities);
        var offline = !_network.IsConnected;
        await MutateAsync(changes =>
        {
            var reply = replyToMessageId is { } replyId ? FindHeld(chatId, replyId) : null;
            for (var i = 0; i < parts.Count; i++)
            {
                var entry = NewEntry(chatId, OutboxKind.Message, MessageType.Text, parts[i].Text, parts[i].Entities,
                    i == 0 ? replyToMessageId : null, i == 0 && reply is not null ? MessageRules.ReplyPreview(reply) : null, []);
                entry.State = offline ? MessageSendState.Queued : MessageSendState.Sending;
                AddEntry(entry, changes);
            }
        }).ConfigureAwait(false);
        await _persistOutbox.FlushAsync().ConfigureAwait(false);
        SendWhenLatestAsync(chatId).Forget(_logger);
    }

    public async Task SendAttachmentsAsync(Guid chatId, IReadOnlyList<OutgoingAttachment> attachments, string? caption = null, Guid? replyToMessageId = null)
    {
        if (Me is null) return;
        if (attachments.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(caption)) await SendTextAsync(chatId, caption, replyToMessageId).ConfigureAwait(false);
            return;
        }

        var groups = GroupAttachments(attachments);
        var captionParts = string.IsNullOrWhiteSpace(caption) ? [] : TextSplitter.Split(caption, null);
        await MutateAsync(changes =>
        {
            var reply = replyToMessageId is { } replyId ? FindHeld(chatId, replyId) : null;
            Guid? captionKey = null;
            for (var i = 0; i < groups.Count; i++)
            {
                var items = groups[i].Select(ToOutboxAttachment).ToList();
                var type = MessageRules.InferType(items.Select(a => a.Kind).ToList());
                var entry = NewEntry(chatId, OutboxKind.Message, type,
                    i == 0 && captionParts.Count > 0 ? captionParts[0].Text : null, null,
                    i == 0 ? replyToMessageId : null, i == 0 && reply is not null ? MessageRules.ReplyPreview(reply) : null, items);
                entry.State = MessageSendState.Preparing;
                AddEntry(entry, changes);
                captionKey ??= entry.Key;
            }
            // A caption longer than one message follows as text, after the album that carries its beginning (ready
            // text would otherwise overtake the album still uploading).
            for (var i = 1; i < captionParts.Count; i++)
            {
                var entry = NewEntry(chatId, OutboxKind.Message, MessageType.Text, captionParts[i].Text, null, null, null, []);
                entry.Record = entry.Record with { SendAfterKey = captionKey };
                AddEntry(entry, changes);
            }
        }).ConfigureAwait(false);
        await _persistOutbox.FlushAsync().ConfigureAwait(false);
        // Preparation starts at once (preview, compression, the durable copy); the message itself only goes out after
        // its upload, by which time the window has moved to the newest messages.
        Kick(chatId);
        EnsureLatestAsync(chatId).Forget(_logger);
    }

    public async Task ForwardAsync(Guid fromChatId, IReadOnlyList<Guid> messageIds, Guid toChatId)
    {
        if (Me is null || messageIds.Count == 0) return;
        await MutateAsync(changes =>
        {
            var sources = messageIds.Distinct()
                .Select(id => (Id: id, Message: FindHeld(fromChatId, id)))
                .Where(s => s.Message is null || (!s.Message.IsDeleted && s.Message.Type is not (MessageType.System or MessageType.Call) && s.Message.Seq > 0))
                .OrderBy(s => s.Message?.Seq ?? long.MaxValue)
                .ToList();
            foreach (var (id, message) in sources)
            {
                var entry = NewEntry(toChatId, OutboxKind.Forward, message?.Type ?? MessageType.Text, message?.Text, message?.Entities?.ToList(),
                    null, null, []);
                entry.Record = entry.Record with { ForwardFromChatId = fromChatId, ForwardMessageId = id, ForwardSource = message };
                AddEntry(entry, changes);
            }
        }).ConfigureAwait(false);
        await _persistOutbox.FlushAsync().ConfigureAwait(false);
        SendWhenLatestAsync(toChatId).Forget(_logger);
    }

    public async Task RetryAsync(Guid chatId, Guid key)
    {
        var found = await MutateAsync(changes =>
        {
            var entry = _outbox.FirstOrDefault(e => e.Key == key && e.ChatId == chatId);
            if (entry is null || entry.State is not (MessageSendState.Failed or MessageSendState.Queued)) return false;
            var missing = entry.Attachments.Any(a => a.Media is not { Status: MediaStatus.Ready } && a.Source is null);
            if (missing)
            {
                MarkFailed(entry, CoreErrorCodes.AttachmentUnavailable, changes);
                return false;
            }
            entry.State = entry.ReadyToSend ? MessageSendState.Sending : entry.Attachments.All(a => a.Prepared) ? MessageSendState.Uploading : MessageSendState.Preparing;
            entry.Error = null;
            entry.Attempts = 0;
            entry.NextAttemptAt = 0;
            entry.CachedItem = null;
            changes.Chats.Add(chatId);
            changes.Outbox = true;
            return true;
        }).ConfigureAwait(false);
        if (found) Kick(chatId);
    }

    public async Task CancelAsync(Guid chatId, Guid key)
    {
        var entry = await MutateAsync(changes =>
        {
            var match = _outbox.FirstOrDefault(e => e.Key == key && e.ChatId == chatId);
            if (match is not null) RemoveEntry(match, changes);
            return match;
        }).ConfigureAwait(false);
        if (entry is null) return;
        await entry.Cancellation.CancelAsync().ConfigureAwait(false);
        await DeleteFilesAsync(entry).ConfigureAwait(false);
        if (entry.Attachments.Count > 0) _typing.NotifyLocalActivity(chatId, TypingAction.Cancel);
        // Items that waited for this one (the rest of a caption) may go now.
        Kick(chatId);
    }

    // Draining

    private void Kick(Guid chatId)
    {
        lock (_lock)
        {
            if (!_draining.Add(chatId))
            {
                _drainAgain.Add(chatId);
                return;
            }
        }
        DrainLoopAsync(chatId, _generation).Forget(_logger);
    }

    private void KickAll()
    {
        KickAllAsync().Forget(_logger);

        async Task KickAllAsync()
        {
            var chats = await _queue.RunAsync(() => _outbox.Select(e => e.ChatId).Distinct().ToList()).ConfigureAwait(false);
            foreach (var chatId in chats) Kick(chatId);
        }
    }

    private async Task DrainLoopAsync(Guid chatId, int generation)
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                await DrainOnceAsync(chatId, generation).ConfigureAwait(false);
                lock (_lock)
                {
                    if (!_drainAgain.Remove(chatId))
                    {
                        _draining.Remove(chatId);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outbox drain of {ChatId} failed", chatId);
            lock (_lock)
            {
                _draining.Remove(chatId);
                _drainAgain.Remove(chatId);
            }
        }
    }

    private async Task DrainOnceAsync(Guid chatId, int generation)
    {
        while (generation == _generation)
        {
            var next = await MutateAsync(changes => PickNext(chatId, changes)).ConfigureAwait(false);
            if (next is null) return;
            if (!await SendEntryAsync(next, generation).ConfigureAwait(false)) return;
        }
    }

    /// <summary>The next item to send in this chat, starting preparations of the items still waiting for uploads.</summary>
    private OutboxEntry? PickNext(Guid chatId, Changes changes)
    {
        foreach (var entry in _outbox.Where(e => e.ChatId == chatId && !e.Removed).ToList())
        {
            if (entry.State == MessageSendState.Failed) continue;
            // Waits until the item it follows was sent (or cancelled; a failed one is retried or cancelled by the user).
            if (entry.Record.SendAfterKey is { } after && _outbox.Any(e => e.Key == after && !e.Removed)) continue;
            if (!entry.ReadyToSend)
            {
                if (!entry.PreparationRunning && IsDue(entry)) StartPreparation(entry);
                else if (!entry.PreparationRunning) ScheduleRetry(entry);
                continue;
            }
            if (!IsDue(entry))
            {
                // A ready item waiting for its retry keeps the order: later ones wait too.
                ScheduleRetry(entry);
                return null;
            }
            if (entry.State != MessageSendState.Sending)
            {
                entry.State = MessageSendState.Sending;
                entry.CachedItem = null;
                changes.Chats.Add(chatId);
            }
            return entry;
        }
        return null;
    }

    /// <summary>Sends one ready item. False when the chat's drain must pause (transient failure).</summary>
    private async Task<bool> SendEntryAsync(OutboxEntry entry, int generation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, _lifecycle.AccountToken);
        var ct = linked.Token;
        var record = entry.Record;
        try
        {
            MessageDto? confirmed;
            if (record.Kind == OutboxKind.Forward)
            {
                var forwarded = await _api.ForwardMessagesAsync(record.ChatId, new ForwardMessagesRequest
                {
                    FromChatId = record.ForwardFromChatId ?? Guid.Empty,
                    MessageIds = [record.ForwardMessageId ?? Guid.Empty],
                    ClientMessageIds = [record.Key],
                }, ct).ConfigureAwait(false);
                confirmed = forwarded.FirstOrDefault();
            }
            else
            {
                confirmed = await _api.SendMessageAsync(record.ChatId, new SendMessageRequest
                {
                    ClientMessageId = record.Key,
                    Text = record.Text,
                    Entities = record.Entities,
                    ReplyToMessageId = record.ReplyToMessageId,
                    MediaIds = entry.Attachments.Select(a => a.Media!.Id).ToList(),
                }, ct).ConfigureAwait(false);
            }
            if (generation != _generation) return false;
            await ConfirmAsync(entry, confirmed).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return generation == _generation;
        }
        catch (ApiException ex) when (ex.IsTransient)
        {
            await MutateAsync(changes => MarkQueued(entry, ex.Code, changes)).ConfigureAwait(false);
            return false;
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.MessageNotFound && record.Kind == OutboxKind.Message && record.ReplyToMessageId is not null)
        {
            // The replied-to message is gone: the text still matters more than the reply.
            await MutateAsync(changes =>
            {
                entry.Record = entry.Record with { ReplyToMessageId = null, ReplyPreview = null };
                entry.CachedItem = null;
                changes.Chats.Add(entry.ChatId);
                changes.Outbox = true;
            }).ConfigureAwait(false);
            return true;
        }
        catch (ApiException ex) when (ex.Code is ErrorCodes.MediaNotFound or ErrorCodes.MediaNotReady
                                      && entry.Reuploads == 0 && entry.Attachments.All(a => a.Source is not null))
        {
            // The uploaded media vanished server-side (cleanup): upload once more from the durable copy.
            await MutateAsync(changes =>
            {
                entry.Reuploads++;
                foreach (var attachment in entry.Attachments)
                {
                    attachment.Media = null;
                    attachment.MediaId = null;
                }
                entry.State = MessageSendState.Uploading;
                entry.CachedItem = null;
                changes.Chats.Add(entry.ChatId);
                changes.Outbox = true;
            }).ConfigureAwait(false);
            return true;
        }
        catch (ApiException ex)
        {
            await MutateAsync(changes => MarkFailed(entry, ex.Code, changes)).ConfigureAwait(false);
            return true;
        }
    }

    private async Task ConfirmAsync(OutboxEntry entry, MessageDto? confirmed)
    {
        await MutateAsync(changes =>
        {
            if (!entry.Removed) RemoveEntry(entry, changes);
            if (confirmed is null || !_windows.TryGetValue(confirmed.ChatId, out var window)) return;
            window.LocalKeys[confirmed.Id] = entry.Key;
            RunOrBuffer(window, changes, c => ApplyCreated(window, confirmed, c));
        }).ConfigureAwait(false);
        await DeleteFilesAsync(entry).ConfigureAwait(false);
    }

    // Preparation and upload

    private void StartPreparation(OutboxEntry entry)
    {
        entry.PreparationRunning = true;
        PrepareAndUploadAsync(entry, _generation).Forget(_logger);
    }

    private async Task PrepareAndUploadAsync(OutboxEntry entry, int generation)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, _lifecycle.AccountToken);
        var ct = linked.Token;
        var preparing = false;
        var uploading = false;
        var ready = false;
        try
        {
            // Only compression takes a preparation slot, and only while it runs: a voice note or a file never waits
            // behind videos being compressed or uploaded.
            if (entry.Attachments.Any(a => a.Compress && !a.Prepared && a.Media is not { Status: MediaStatus.Ready }))
            {
                await _preparationSlots.WaitAsync(ct).ConfigureAwait(false);
                preparing = true;
            }

            foreach (var attachment in entry.Attachments)
            {
                if (attachment.Media is { Status: MediaStatus.Ready } || attachment.Prepared) continue;
                await SetEntryProgressAsync(entry, MessageSendState.Preparing, 0).ConfigureAwait(false);
                await PrepareAttachmentAsync(entry, attachment, ct).ConfigureAwait(false);
                await MutateAsync(changes =>
                {
                    entry.CachedItem = null;
                    changes.Chats.Add(entry.ChatId);
                    changes.Outbox = true;
                }).ConfigureAwait(false);
                // The record must say "durable copy exists" before anything can make the item Queued.
                await _persistOutbox.FlushAsync().ConfigureAwait(false);
            }
            if (preparing)
            {
                preparing = false;
                _preparationSlots.Release();
            }

            var uploads = entry.Attachments.Where(a => a.Media is not { Status: MediaStatus.Ready }).ToList();
            if (uploads.Any(a => a.Source is null))
                throw new ApiException(400, CoreErrorCodes.AttachmentUnavailable, "The attachment is no longer available.");

            var total = Math.Max(1d, uploads.Sum(a => (double)Math.Max(1, a.Size)));
            var done = 0d;
            var throttle = new ProgressThrottle(_time);
            // Small and expected at once: notes skip the upload slots that long videos may hold for a while.
            var note = entry.Record.Type is MessageType.Voice or MessageType.VideoNote;
            await SetEntryProgressAsync(entry, MessageSendState.Uploading, 0).ConfigureAwait(false);
            foreach (var attachment in uploads)
            {
                var size = (double)Math.Max(1, attachment.Size);
                var offset = done;
                var typing = UploadTypingAction(attachment.Kind);
                var source = attachment.Source!;
                var request = new MediaUploadRequest
                {
                    Kind = attachment.Kind,
                    FileName = attachment.FileName,
                    ContentType = attachment.ContentType,
                    Size = attachment.Size,
                    OpenReadAsync = source.OpenReadAsync,
                    Width = attachment.Width,
                    Height = attachment.Height,
                    DurationMs = attachment.DurationMs,
                    Waveform = attachment.Waveform,
                };
                var progress = new SyncProgress(value =>
                {
                    _typing.NotifyLocalActivity(entry.ChatId, typing);
                    if (throttle.ShouldReport()) SetEntryProgressAsync(entry, null, (offset + value * size) / total).Forget(_logger);
                });
                if (!note)
                {
                    await _uploadSlots.WaitAsync(ct).ConfigureAwait(false);
                    uploading = true;
                }
                var media = await _uploader.UploadAsync(request, attachment.MediaId,
                    async id =>
                    {
                        await MutateAsync(changes =>
                        {
                            attachment.MediaId = id;
                            changes.Outbox = true;
                        }).ConfigureAwait(false);
                        // Remembered at once so a restart resumes this upload instead of starting over.
                        await _persistOutbox.FlushAsync().ConfigureAwait(false);
                    },
                    progress, ct,
                    // The uplink is free once the bytes are sent; the server's processing does not hold the slot.
                    onSent: ReleaseUploadSlot).ConfigureAwait(false);
                ReleaseUploadSlot();
                await MutateAsync(changes =>
                {
                    attachment.Media = media;
                    changes.Outbox = true;
                }).ConfigureAwait(false);
                done += size;
            }

            await MutateAsync(changes =>
            {
                entry.PreparationRunning = false;
                entry.State = MessageSendState.Sending;
                entry.Progress = 1;
                entry.CachedItem = null;
                changes.Chats.Add(entry.ChatId);
            }).ConfigureAwait(false);
            ready = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MutateAsync(_ => entry.PreparationRunning = false).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.IsTransient)
        {
            await MutateAsync(changes =>
            {
                entry.PreparationRunning = false;
                MarkQueued(entry, ex.Code, changes);
            }).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            await MutateAsync(changes =>
            {
                entry.PreparationRunning = false;
                MarkFailed(entry, ex.Code, changes);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preparing attachments of an outbox item failed");
            var code = ex is IOException or UnauthorizedAccessException or InvalidDataException ? CoreErrorCodes.AttachmentUnavailable : ClientErrorCodes.Unknown;
            await MutateAsync(changes =>
            {
                entry.PreparationRunning = false;
                MarkFailed(entry, code, changes);
            }).ConfigureAwait(false);
        }
        finally
        {
            if (preparing) _preparationSlots.Release();
            ReleaseUploadSlot();
            _typing.NotifyLocalActivity(entry.ChatId, TypingAction.Cancel);
        }

        if (ready && generation == _generation) Kick(entry.ChatId);

        // Once per acquisition: after the bytes are sent (onSent), else after the upload or when it failed.
        void ReleaseUploadSlot()
        {
            if (!uploading) return;
            uploading = false;
            _uploadSlots.Release();
        }
    }

    /// <summary>Probe (instant preview) → compression per DataSettings → durable copy in the outbox file store.</summary>
    private async Task PrepareAttachmentAsync(OutboxEntry entry, OutboxAttachment attachment, CancellationToken ct)
    {
        var source = attachment.Source ?? throw new ApiException(400, CoreErrorCodes.AttachmentUnavailable, "The attachment is no longer available.");
        var visual = attachment.Kind is MediaKind.Photo or MediaKind.Video or MediaKind.VideoNote || source.IsImage || source.IsVideo;

        if (!attachment.Probed && visual)
        {
            try
            {
                var probe = await _compressor.ProbeAsync(source, ct).ConfigureAwait(false);
                await MutateAsync(changes =>
                {
                    attachment.Width ??= probe.Width;
                    attachment.Height ??= probe.Height;
                    attachment.DurationMs ??= probe.DurationMs;
                    if (attachment.Size <= 0 && probe.Size > 0) attachment.Size = probe.Size;
                    attachment.PreviewUrl ??= DataUrl(probe.PreviewJpeg);
                    attachment.Probed = true;
                    entry.CachedItem = null;
                    changes.Chats.Add(entry.ChatId);
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Probing an attachment failed");
                attachment.Probed = true;
            }
        }

        var data = _settings.Current.Data;
        CompressedMedia? payload = null;
        try
        {
            if (attachment.Compress && attachment.Kind == MediaKind.Photo && _compressor.CanCompressImages && !IsAnimated(source.ContentType))
            {
                payload = await _compressor.CompressImageAsync(source, ImageOptions(data.PhotoQuality), ct).ConfigureAwait(false);
            }
            else if (attachment.Compress && attachment.Kind == MediaKind.Video && _compressor.CanCompressVideos)
            {
                var throttle = new ProgressThrottle(_time);
                var progress = new SyncProgress(value =>
                {
                    if (throttle.ShouldReport()) SetEntryProgressAsync(entry, null, Math.Clamp(value, 0, 1)).Forget(_logger);
                });
                payload = await _compressor.CompressVideoAsync(source, VideoOptions(data.VideoQuality), progress, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The original still goes out (the server converts what it must).
            _logger.LogInformation(ex, "Compression failed; sending the original");
            payload = null;
        }

        if (payload is not null && payload.Size > 0)
        {
            var compressed = payload;
            attachment.Source = new PickedFile
            {
                FileName = compressed.FileName,
                ContentType = compressed.ContentType,
                Size = compressed.Size,
                OpenReadAsync = compressed.OpenReadAsync,
                LocalPath = compressed.TempFilePath,
            };
            attachment.FileName = compressed.FileName;
            attachment.ContentType = compressed.ContentType;
            attachment.Size = compressed.Size;
            attachment.Width = compressed.Width ?? attachment.Width;
            attachment.Height = compressed.Height ?? attachment.Height;
            attachment.DurationMs = compressed.DurationMs ?? attachment.DurationMs;
            attachment.PreviewUrl ??= DataUrl(compressed.PreviewJpeg);
            attachment.TempFilePath = compressed.TempFilePath;
        }

        if (attachment.Size <= 0)
            attachment.Size = await MeasureAsync(attachment.Source!, ct).ConfigureAwait(false);
        if (attachment.Size <= 0)
            throw new ApiException(400, ErrorCodes.Validation, "The file is empty.");

        if (_outboxFiles.IsSupported && !attachment.Persisted)
        {
            try
            {
                var durable = await _outboxFiles.PersistAsync(attachment.Key, attachment.Source!, ct).ConfigureAwait(false);
                attachment.Source = durable;
                attachment.Persisted = true;
                TryDeleteTempFile(attachment);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Keeps working from memory; only a restart before the upload would lose it.
                _logger.LogWarning(ex, "Could not keep a durable copy of an attachment");
            }
        }
        attachment.Prepared = true;
    }

    // State helpers (inside the queue unless noted)

    private OutboxEntry NewEntry(Guid chatId, OutboxKind kind, MessageType type, string? text, List<MessageEntityDto>? entities,
        Guid? replyTo, ReplyPreviewDto? replyPreview, List<OutboxAttachment> attachments) => new()
    {
        Record = new OutboxRecord
        {
            Key = Guid.CreateVersion7(),
            ChatId = chatId,
            Kind = kind,
            CreatedAt = _time.GetUtcNow(),
            Type = type,
            Text = text,
            Entities = entities is { Count: > 0 } ? entities : null,
            ReplyToMessageId = replyTo,
            ReplyPreview = replyPreview,
        },
        Attachments = attachments,
    };

    private void AddEntry(OutboxEntry entry, Changes changes)
    {
        _outbox.Add(entry);
        EnsureWindow(entry.ChatId);
        changes.Chats.Add(entry.ChatId);
        changes.Outbox = true;
    }

    private void RemoveEntry(OutboxEntry entry, Changes changes)
    {
        entry.Removed = true;
        _outbox.Remove(entry);
        changes.Chats.Add(entry.ChatId);
        changes.Outbox = true;
    }

    private void MarkQueued(OutboxEntry entry, string code, Changes changes)
    {
        if (entry.Removed) return;
        entry.State = MessageSendState.Queued;
        entry.Error = code;
        entry.Attempts++;
        var delay = RetryBackoff[Math.Min(entry.Attempts, RetryBackoff.Length) - 1];
        entry.NextAttemptAt = _time.GetTimestamp() + (long)(delay.TotalSeconds * _time.TimestampFrequency);
        entry.CachedItem = null;
        changes.Chats.Add(entry.ChatId);
        changes.Outbox = true;
        ScheduleRetry(entry);
    }

    private void MarkFailed(OutboxEntry entry, string code, Changes changes)
    {
        if (entry.Removed) return;
        _logger.LogInformation("Outbox item failed permanently ({Code})", code);
        entry.State = MessageSendState.Failed;
        entry.Error = code;
        entry.CachedItem = null;
        changes.Chats.Add(entry.ChatId);
        changes.Outbox = true;
    }

    private void ResetBackoff()
    {
        foreach (var entry in _outbox)
        {
            if (entry.State != MessageSendState.Queued) continue;
            entry.NextAttemptAt = 0;
        }
    }

    private bool IsDue(OutboxEntry entry) => entry.NextAttemptAt == 0 || _time.GetTimestamp() >= entry.NextAttemptAt;

    private void ScheduleRetry(OutboxEntry entry)
    {
        if (entry.NextAttemptAt == 0 || entry.RetryScheduledAt == entry.NextAttemptAt) return;
        entry.RetryScheduledAt = entry.NextAttemptAt;
        var wait = TimeSpan.FromSeconds(Math.Max(0, (entry.NextAttemptAt - _time.GetTimestamp()) / (double)_time.TimestampFrequency));
        var generation = _generation;
        RetryLaterAsync().Forget(_logger);

        async Task RetryLaterAsync()
        {
            await Task.Delay(wait + TimeSpan.FromMilliseconds(50), _time).ConfigureAwait(false);
            if (generation == _generation && !entry.Removed) Kick(entry.ChatId);
        }
    }

    private Task SetEntryProgressAsync(OutboxEntry entry, MessageSendState? state, double progress) => MutateAsync(changes =>
    {
        if (entry.Removed) return;
        if (state is { } s) entry.State = s;
        else if (entry.State is not (MessageSendState.Preparing or MessageSendState.Uploading)) return;
        entry.Progress = Math.Clamp(progress, 0, 1);
        entry.CachedItem = null;
        changes.Chats.Add(entry.ChatId);
    });

    private MessageDto? FindHeld(Guid chatId, Guid messageId)
    {
        if (_windows.TryGetValue(chatId, out var window) && window.Get(messageId) is { } held) return held;
        if (_pinned.TryGetValue(chatId, out var pinned)) return pinned.FirstOrDefault(m => m.Id == messageId);
        return null;
    }

    /// <summary>
    /// Runs once the new pending items exist and are persisted, so a slow page load never holds back the send call:
    /// a window that is behind (after a jump) moves to the newest messages first, so the confirmed copy lands at its end.
    /// </summary>
    private async Task SendWhenLatestAsync(Guid chatId)
    {
        await EnsureLatestAsync(chatId).ConfigureAwait(false);
        Kick(chatId);
    }

    private async Task EnsureLatestAsync(Guid chatId)
    {
        var behind = await _queue.RunAsync(() => _windows.TryGetValue(chatId, out var window) && window.IsInitialized && window.HasMoreAfter)
            .ConfigureAwait(false);
        if (!behind) return;
        try
        {
            await JumpToLatestAsync(chatId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not move to the newest messages before sending");
        }
    }

    private async Task<OutboxEntry> RestoreEntryAsync(OutboxRecord record)
    {
        var attachments = record.Attachments.Select(OutboxAttachment.FromRecord).ToList();
        var unavailable = false;
        foreach (var attachment in attachments)
        {
            if (attachment.Media is { Status: MediaStatus.Ready }) continue;
            if (attachment.Persisted && _outboxFiles.IsSupported)
            {
                try
                {
                    attachment.Source = await _outboxFiles.TryOpenAsync(attachment.Key).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A durable attachment copy is unreadable");
                }
            }
            if (attachment.Source is null) unavailable = true;
        }

        var entry = new OutboxEntry { Record = record with { Attachments = [] }, Attachments = attachments };
        if (record.Failed)
        {
            entry.State = MessageSendState.Failed;
            entry.Error = record.Error ?? ClientErrorCodes.Unknown;
        }
        else if (unavailable)
        {
            entry.State = MessageSendState.Failed;
            entry.Error = CoreErrorCodes.AttachmentUnavailable;
        }
        else
        {
            entry.State = MessageSendState.Queued;
        }
        return entry;
    }

    private async Task PersistOutboxAsync(CancellationToken ct)
    {
        if (_lifecycle.ActiveUserId is not { } userId) return;
        var generation = _generation;
        var records = await _queue.RunAsync(() => _outbox.Where(e => !e.Removed).Select(e => e.ToRecord()).ToList()).ConfigureAwait(false);
        if (generation != _generation) return;
        await _cache.WriteAsync(LocalCache.AccountKey(userId, OutboxCacheName), records, ct).ConfigureAwait(false);
    }

    private async Task DeleteFilesAsync(OutboxEntry entry)
    {
        foreach (var attachment in entry.Attachments)
        {
            TryDeleteTempFile(attachment);
            if (!attachment.Persisted || !_outboxFiles.IsSupported) continue;
            try
            {
                await _outboxFiles.DeleteAsync(attachment.Key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete a durable attachment copy");
            }
        }
    }

    private void TryDeleteTempFile(OutboxAttachment attachment)
    {
        var path = attachment.TempFilePath;
        attachment.TempFilePath = null;
        // Temp files only exist on native hosts (their compressors create them).
        if (string.IsNullOrEmpty(path) || OperatingSystem.IsBrowser()) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete a temporary media file");
        }
    }

    private static List<List<OutgoingAttachment>> GroupAttachments(IReadOnlyList<OutgoingAttachment> attachments)
    {
        static bool IsVisual(OutgoingAttachment a) => a.Compress && a.Kind is MediaKind.Photo or MediaKind.Video;
        static bool IsNote(OutgoingAttachment a) => a.Kind is MediaKind.Voice or MediaKind.VideoNote;

        var groups = new List<List<OutgoingAttachment>>();
        var visual = attachments.Where(IsVisual).ToList();
        var files = attachments.Where(a => !IsVisual(a) && !IsNote(a)).ToList();
        var notes = attachments.Where(IsNote).ToList();
        groups.AddRange(visual.Chunk(Limits.MaxAttachmentsPerMessage).Select(c => c.ToList()));
        groups.AddRange(files.Chunk(Limits.MaxAttachmentsPerMessage).Select(c => c.ToList()));
        groups.AddRange(notes.Select(n => new List<OutgoingAttachment> { n }));
        return groups;
    }

    private static OutboxAttachment ToOutboxAttachment(OutgoingAttachment attachment)
    {
        // "Send without compression": photos/videos go out as files with their original bytes.
        var kind = !attachment.Compress && attachment.Kind is MediaKind.Photo or MediaKind.Video ? MediaKind.File : attachment.Kind;
        return new OutboxAttachment
        {
            Key = Guid.CreateVersion7(),
            Kind = kind,
            Compress = attachment.Compress && kind is MediaKind.Photo or MediaKind.Video,
            FileName = string.IsNullOrWhiteSpace(attachment.Source.FileName) ? "file" : attachment.Source.FileName,
            ContentType = string.IsNullOrWhiteSpace(attachment.Source.ContentType) ? "application/octet-stream" : attachment.Source.ContentType,
            Size = attachment.Source.Size,
            Width = attachment.Width,
            Height = attachment.Height,
            DurationMs = attachment.DurationMs,
            Waveform = attachment.Waveform,
            PreviewUrl = attachment.PreviewUrl,
            Probed = kind is not (MediaKind.Photo or MediaKind.Video or MediaKind.VideoNote),
            Source = attachment.Source,
        };
    }

    internal static ImageCompressionOptions ImageOptions(MediaQuality quality) => quality switch
    {
        MediaQuality.Low => new ImageCompressionOptions { MaxDimension = 1280, Quality = 75 },
        MediaQuality.High => new ImageCompressionOptions { MaxDimension = 4096, Quality = 90 },
        _ => new ImageCompressionOptions { MaxDimension = 2560, Quality = 82 },
    };

    internal static VideoCompressionOptions VideoOptions(MediaQuality quality) => quality switch
    {
        MediaQuality.Low => new VideoCompressionOptions { MaxDimension = 854, VideoBitrateKbps = 1200, AudioBitrateKbps = 96 },
        MediaQuality.High => new VideoCompressionOptions { MaxDimension = 1920, VideoBitrateKbps = 5000, AudioBitrateKbps = 128 },
        _ => new VideoCompressionOptions { MaxDimension = 1280, VideoBitrateKbps = 2500, AudioBitrateKbps = 128 },
    };

    private static TypingAction UploadTypingAction(MediaKind kind) => kind switch
    {
        MediaKind.Photo => TypingAction.UploadingPhoto,
        MediaKind.Video or MediaKind.VideoNote => TypingAction.UploadingVideo,
        _ => TypingAction.UploadingFile,
    };

    private static bool IsAnimated(string contentType) => contentType.StartsWith("image/gif", StringComparison.OrdinalIgnoreCase);

    private static string? DataUrl(byte[]? jpeg) => jpeg is { Length: > 0 } ? "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) : null;

    private static async Task<long> MeasureAsync(PickedFile file, CancellationToken ct)
    {
        await using var stream = await file.OpenReadAsync(ct).ConfigureAwait(false);
        if (stream.CanSeek) return stream.Length;
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) total += read;
        return total;
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
