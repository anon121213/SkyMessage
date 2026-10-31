using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>Builds a <see cref="MessageStore"/> over fakes; sharing <see cref="CacheStore"/>/<see cref="OutboxFiles"/> simulates a restart.</summary>
internal sealed class MessageStoreHarness
{
    public Guid Me { get; }
    public Guid ChatId { get; }
    public ISkyApi Api { get; } = Substitute.For<ISkyApi>();
    public IRealtimeService Realtime { get; } = Substitute.For<IRealtimeService>();
    public IChatListStore Chats { get; } = Substitute.For<IChatListStore>();
    public INetworkStatusService Network { get; } = Fakes.Network();
    public IMediaCompressor Compressor { get; } = Substitute.For<IMediaCompressor>();
    public InMemoryCacheStore CacheStore { get; }
    public InMemoryOutboxFiles OutboxFiles { get; }
    public AccountLifecycle Lifecycle { get; }
    public MessageStore Store { get; }

    public MessageStoreHarness(Guid? me = null, InMemoryCacheStore? cacheStore = null, InMemoryOutboxFiles? outboxFiles = null, Guid? chatId = null)
    {
        Me = me ?? Guid.CreateVersion7();
        ChatId = chatId ?? Guid.CreateVersion7();
        CacheStore = cacheStore ?? new InMemoryCacheStore();
        OutboxFiles = outboxFiles ?? new InMemoryOutboxFiles();
        var (cache, lifecycle) = Fakes.Account(CacheStore);
        Lifecycle = lifecycle;
        Chats.Get(ChatId).Returns(new ChatSummaryDto { Id = ChatId, Type = ChatType.Private, LastMessageSeq = 3 });
        Compressor.CanCompressImages.Returns(false);
        Compressor.CanCompressVideos.Returns(false);
        Realtime.BeginSync().Returns(Substitute.For<IDisposable>());

        // Instant backoff: upload retries would otherwise take seconds.
        var uploader = new MediaUploader(Api, Realtime, new InstantTime(), Log.For<MediaUploader>());
        Store = new MessageStore(Api, Realtime, Fakes.Auth(Me), Chats, Substitute.For<IUserStore>(), Substitute.For<ITypingStore>(), uploader,
            Compressor, OutboxFiles, Network, Fakes.Settings(), cache, lifecycle, TimeProvider.System, Log.For<MessageStore>());
    }

    public Task SignInAsync() => Lifecycle.ActivateAsync(Me);

    public string OutboxKey => LocalCache.AccountKey(Me, "outbox");

    public void RaiseCreated(MessageDto message) => Realtime.MessageCreated += Raise.Event<Action<MessageDto>>(message);

    public void RaiseUpdated(MessageDto message) => Realtime.MessageUpdated += Raise.Event<Action<MessageDto>>(message);

    public ChatMessagesState State => Store.GetState(ChatId);

    /// <summary>The same device after a crash: a fresh process over a copy of the storage.</summary>
    public MessageStoreHarness Restart() => new(Me, CacheStore.Clone(), OutboxFiles.Clone(), ChatId);

    public void ReturnsPage(MessagesPageDto page) =>
        Api.GetMessagesAsync(ChatId, Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(page);

    /// <summary>The page request for one position: (beforeSeq, afterSeq, aroundSeq), all null = the latest page.</summary>
    public Task<MessagesPageDto> PageRequest(long? before = null, long? after = null, long? around = null) =>
        Api.GetMessagesAsync(ChatId, Arg.Is<long?>(v => v == before), Arg.Is<long?>(v => v == after), Arg.Is<long?>(v => v == around),
            Arg.Any<int>(), Arg.Any<CancellationToken>());

    public int CallsTo(string method) => Api.ReceivedCalls().Count(c => c.GetMethodInfo().Name == method);

    public void WriteCachedWindow(IEnumerable<MessageDto> messages, string cursor) =>
        CacheStore.Entries[LocalCache.AccountKey(Me, $"msgs/{ChatId:N}")] = JsonSerializer.Serialize(new
        {
            messages,
            syncCursor = cursor,
            hasMoreBefore = false,
            myReactions = new Dictionary<Guid, string>(),
        }, SkyJson.Options);
}

public sealed class MessageStoreTests
{
    [Fact]
    public async Task Pending_message_is_confirmed_once_when_the_realtime_echo_arrives_before_the_response()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        h.ReturnsPage(Fakes.Page([Fakes.Message(h.ChatId, 1, other), Fakes.Message(h.ChatId, 2, other), Fakes.Message(h.ChatId, 3, other)]));
        var response = new TaskCompletionSource<MessageDto>();
        SendMessageRequest? sent = null;
        h.Api.SendMessageAsync(h.ChatId, Arg.Do<SendMessageRequest>(r => sent = r), Arg.Any<CancellationToken>()).Returns(response.Task);
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);

        await h.Store.SendTextAsync(h.ChatId, "hello");

        h.State.Items.Count.ShouldBe(4);
        var pending = h.State.Items[^1];
        pending.IsPending.ShouldBeTrue();
        pending.Message.Seq.ShouldBe(0);
        pending.Message.SenderId.ShouldBe(h.Me);
        await Wait.UntilAsync(() => sent is not null, "message posted");
        sent!.ClientMessageId.ShouldBe(pending.Key);

        var confirmed = Fakes.Message(h.ChatId, 4, h.Me, "hello", clientMessageId: pending.Key);
        h.RaiseCreated(confirmed);
        await Wait.UntilAsync(() => h.State.Items.All(i => !i.IsPending), "echo confirms the pending copy");
        response.SetResult(confirmed);
        await Task.Delay(100);

        var items = h.State.Items;
        items.Count.ShouldBe(4);
        items[^1].Message.Id.ShouldBe(confirmed.Id);
        items[^1].Key.ShouldBe(pending.Key, "the render key stays stable for messages sent from this device");
        items.Select(i => i.Message.Seq).ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Realtime_update_with_an_older_UpdatedAt_is_ignored()
    {
        var h = new MessageStoreHarness();
        var t0 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var original = Fakes.Message(h.ChatId, 1, text: "v1", updatedAt: t0);
        h.ReturnsPage(Fakes.Page([original]));
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);

        h.RaiseUpdated(original with { Text = "v3", UpdatedAt = t0.AddSeconds(20) });
        await Wait.UntilAsync(() => h.State.Items[0].Message.Text == "v3");
        h.RaiseUpdated(original with { Text = "v2", UpdatedAt = t0.AddSeconds(10) });
        await Task.Delay(100);

        h.State.Items.Single().Message.Text.ShouldBe("v3");
    }

    [Fact]
    public void Window_merge_follows_the_UpdatedAt_rule()
    {
        var chatId = Guid.CreateVersion7();
        var window = new MessageWindow(chatId) { IsInitialized = true };
        var t0 = DateTimeOffset.UnixEpoch.AddYears(56);
        var message = Fakes.Message(chatId, 5, text: "edited", updatedAt: t0.AddMinutes(2));

        window.Upsert(message).ShouldBeTrue();
        window.Upsert(message with { Text = "stale", UpdatedAt = t0.AddMinutes(1) }).ShouldBeFalse();
        window.Get(message.Id)!.Text.ShouldBe("edited");
        window.Upsert(message with { Text = "same instant wins", UpdatedAt = t0.AddMinutes(2) }).ShouldBeTrue();
        window.Get(message.Id)!.Text.ShouldBe("same instant wins");
        window.Upsert(message with { IsDeleted = true, UpdatedAt = t0.AddMinutes(3) }).ShouldBeTrue();
        window.Contains(message.Id).ShouldBeFalse("tombstones remove the message");
    }

    [Fact]
    public void Changes_apply_tombstones_hidden_ids_and_the_clear_watermark()
    {
        var chatId = Guid.CreateVersion7();
        var me = Guid.CreateVersion7();
        var window = new MessageWindow(chatId);
        var messages = Enumerable.Range(1, 5).Select(i => Fakes.Message(chatId, i)).ToList();
        window.ApplyPage(Fakes.Page(messages), older: true, newer: true);

        var changed = window.ApplyChanges(new MessageChangesDto
        {
            Messages =
            [
                messages[2] with { IsDeleted = true, Text = null, UpdatedAt = messages[2].UpdatedAt.AddHours(1) },
                messages[3] with { Text = "edited", UpdatedAt = messages[3].UpdatedAt.AddHours(1) },
                Fakes.Message(chatId, 6, text: "new"),
            ],
            HiddenMessageIds = [messages[4].Id],
            ClearedUpToSeq = 1,
            NextCursor = "next",
        }, me);

        changed.ShouldBeTrue();
        window.Messages.Select(m => m.Seq).ShouldBe([2, 4, 6]);
        window.Get(messages[3].Id)!.Text.ShouldBe("edited");
        window.ClearedUpToSeq.ShouldBe(1);
        window.Upsert(Fakes.Message(chatId, 1)).ShouldBeFalse("messages at or below the watermark never come back");
    }

    [Fact]
    public void Changes_bring_reply_quotes_in_line_with_the_messages_they_quote()
    {
        var chatId = Guid.CreateVersion7();
        var window = new MessageWindow(chatId);
        var old = Fakes.Message(chatId, 5, text: "something private");
        var typo = Fakes.Message(chatId, 11, text: "see you at 5");
        var quotesOld = Fakes.Message(chatId, 10) with { ReplyTo = MessageRules.ReplyPreview(old) };
        var quotesTypo = Fakes.Message(chatId, 12) with { ReplyTo = MessageRules.ReplyPreview(typo) };
        window.ApplyPage(Fakes.Page([quotesOld, typo, quotesTypo], hasMoreBefore: true), older: true, newer: true);

        // Deleted for everyone and edited while this device was offline: /changes has those two, not the replies.
        var changed = window.ApplyChanges(new MessageChangesDto
        {
            Messages =
            [
                old with { IsDeleted = true, Text = null, UpdatedAt = old.UpdatedAt.AddHours(1) },
                typo with { Text = "see you at 6", EditedAt = typo.UpdatedAt.AddHours(1), UpdatedAt = typo.UpdatedAt.AddHours(1) },
            ],
            NextCursor = "next",
        }, Guid.CreateVersion7());

        changed.ShouldBeTrue();
        window.Get(quotesOld.Id)!.ReplyTo!.IsDeleted.ShouldBeTrue("the quoted message is outside the window, the quote is not");
        window.Get(quotesOld.Id)!.ReplyTo!.Text.ShouldBeNull("deleted text must not stay readable in a quote");
        window.Get(quotesTypo.Id)!.ReplyTo!.Text.ShouldBe("see you at 6");
    }

    [Fact]
    public async Task Cached_window_catches_up_through_afterSeq_pages_and_changes()
    {
        var h = new MessageStoreHarness();
        var t0 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var cached = Enumerable.Range(1, 5).Select(i => Fakes.Message(h.ChatId, i, updatedAt: t0)).ToList();
        h.CacheStore.Entries[LocalCache.AccountKey(h.Me, $"msgs/{h.ChatId:N}")] = JsonSerializer.Serialize(new
        {
            messages = cached,
            syncCursor = "cursor-0",
            hasMoreBefore = false,
            myReactions = new Dictionary<Guid, string>(),
        }, SkyJson.Options);
        h.Chats.Get(h.ChatId).Returns(new ChatSummaryDto { Id = h.ChatId, Type = ChatType.Private, LastMessageSeq = 6 });

        var newMessage = Fakes.Message(h.ChatId, 6, text: "while offline");
        h.Api.GetMessagesAsync(h.ChatId, Arg.Is<long?>(v => v == null), Arg.Is<long?>(v => v == 5), Arg.Is<long?>(v => v == null),
            Arg.Is(Limits.MessagesPageMax), Arg.Any<CancellationToken>()).Returns(Fakes.Page([newMessage]));
        h.Api.GetMessageChangesAsync(h.ChatId, "cursor-0", Arg.Any<CancellationToken>()).Returns(new MessageChangesDto
        {
            Messages =
            [
                cached[1] with { IsDeleted = true, Text = null, UpdatedAt = t0.AddMinutes(5) },
                cached[2] with { Text = "edited elsewhere", UpdatedAt = t0.AddMinutes(6) },
            ],
            HiddenMessageIds = [cached[3].Id],
            ClearedUpToSeq = 0,
            NextCursor = "cursor-1",
        });
        await h.SignInAsync();

        await h.Store.OpenAsync(h.ChatId);

        var items = h.State.Items;
        items.Select(i => i.Message.Seq).ShouldBe([1, 3, 5, 6]);
        items.Single(i => i.Message.Seq == 3).Message.Text.ShouldBe("edited elsewhere");
        await h.Api.DidNotReceive().GetMessagesAsync(h.ChatId, Arg.Is<long?>(v => v == null), Arg.Is<long?>(v => v == null),
            Arg.Is<long?>(v => v == null), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Events_during_a_window_load_are_replayed_after_it()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        var page = new TaskCompletionSource<MessagesPageDto>();
        h.Api.GetMessagesAsync(h.ChatId, Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(page.Task);
        await h.SignInAsync();

        var open = h.Store.OpenAsync(h.ChatId);
        await Task.Delay(50);
        h.RaiseCreated(Fakes.Message(h.ChatId, 4, other, "arrived during the load"));
        await Task.Delay(50);
        page.SetResult(Fakes.Page([Fakes.Message(h.ChatId, 1, other), Fakes.Message(h.ChatId, 2, other), Fakes.Message(h.ChatId, 3, other)]));
        await open;

        h.State.Items.Select(i => i.Message.Seq).ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Outbox_survives_a_restart_and_is_resent_with_the_same_ClientMessageId()
    {
        var first = new MessageStoreHarness();
        first.Api.SendMessageAsync(Arg.Any<Guid>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(0, ClientErrorCodes.Network));
        await first.SignInAsync();

        await first.Store.SendTextAsync(first.ChatId, "written offline");
        await Wait.UntilAsync(() => first.State.Items.SingleOrDefault()?.SendState == MessageSendState.Queued, "transient failure → Queued");
        var key = first.State.Items.Single().Key;
        first.CacheStore.Entries.ContainsKey(first.OutboxKey).ShouldBeTrue("the outbox is persisted before sending");

        // "Restart": a new process over the storage the old one left behind.
        var second = first.Restart();
        SendMessageRequest? resent = null;
        second.Api.SendMessageAsync(Arg.Any<Guid>(), Arg.Do<SendMessageRequest>(r => resent = r), Arg.Any<CancellationToken>())
            .Returns(call => Fakes.Message(call.ArgAt<Guid>(0), 7, second.Me, "written offline", clientMessageId: call.ArgAt<SendMessageRequest>(1).ClientMessageId));
        await second.SignInAsync();

        await Wait.UntilAsync(() => resent is not null, "resent after the restart");
        resent!.ClientMessageId.ShouldBe(key);
        resent.Text.ShouldBe("written offline");
        await Wait.UntilAsync(() => second.State.Items.All(i => !i.IsPending), "confirmed after resend");
        await Wait.UntilAsync(() => !second.CacheStore.Entries[second.OutboxKey].Contains(key.ToString()), "outbox emptied");
    }

    [Fact]
    public async Task Voice_note_recorded_offline_is_kept_durably_and_uploaded_after_a_restart()
    {
        var first = new MessageStoreHarness();
        first.Api.CreateUploadAsync(Arg.Any<CreateUploadRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(0, ClientErrorCodes.Network));
        await first.SignInAsync();
        var audio = new byte[3000];
        Random.Shared.NextBytes(audio);
        var voice = new OutgoingAttachment
        {
            Kind = MediaKind.Voice,
            DurationMs = 1500,
            Source = new PickedFile
            {
                FileName = "voice.webm",
                ContentType = "audio/webm",
                Size = audio.Length,
                OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(audio)),
            },
        };

        await first.Store.SendAttachmentsAsync(first.ChatId, [voice]);
        await Wait.UntilAsync(() => first.State.Items.SingleOrDefault()?.SendState == MessageSendState.Queued, "upload failed → Queued");
        first.OutboxFiles.Files.Count.ShouldBe(1, "the payload is persisted before the item becomes Queued");
        var key = first.State.Items.Single().Key;

        var second = first.Restart();
        var mediaId = Guid.CreateVersion7();
        var uploaded = new List<byte>();
        second.Api.CreateUploadAsync(Arg.Any<CreateUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UploadSessionDto { MediaId = mediaId, ChunkSize = 1024, ChunkCount = 3, Status = MediaStatus.Uploading });
        second.Api.UploadChunkAsync(Arg.Is(mediaId), Arg.Any<int>(), Arg.Do<ReadOnlyMemory<byte>>(d => { lock (uploaded) uploaded.AddRange(d.ToArray()); }),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var ready = new MediaDto { Id = mediaId, Kind = MediaKind.Voice, Status = MediaStatus.Ready, SizeBytes = audio.Length };
        second.Api.CompleteUploadAsync(Arg.Is(mediaId), Arg.Any<CancellationToken>()).Returns(ready);
        SendMessageRequest? sent = null;
        second.Api.SendMessageAsync(Arg.Is(first.ChatId), Arg.Do<SendMessageRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(call => Fakes.Message(first.ChatId, 9, second.Me, clientMessageId: call.ArgAt<SendMessageRequest>(1).ClientMessageId) with { Type = MessageType.Voice });
        await second.SignInAsync();

        await Wait.UntilAsync(() => sent is not null, "voice note sent after the restart");
        sent!.ClientMessageId.ShouldBe(key);
        sent.MediaIds.ShouldBe([mediaId]);
        uploaded.Count.ShouldBe(audio.Length);
        await Wait.UntilAsync(() => second.OutboxFiles.Files.IsEmpty, "durable copy deleted after sending");
    }

    [Fact]
    public async Task Permanent_errors_fail_the_item_and_transient_ones_queue_it()
    {
        var h = new MessageStoreHarness();
        h.Api.SendMessageAsync(Arg.Any<Guid>(), Arg.Is<SendMessageRequest>(r => r.Text == "forbidden"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(403, ErrorCodes.SendForbidden));
        await h.SignInAsync();

        await h.Store.SendTextAsync(h.ChatId, "forbidden");

        await Wait.UntilAsync(() => h.State.Items.SingleOrDefault()?.SendState == MessageSendState.Failed);
        h.State.Items.Single().Error.ShouldBe(ErrorCodes.SendForbidden);
    }

    [Fact]
    public async Task A_chat_covered_during_a_resync_catches_up_when_it_is_back_on_screen()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        h.ReturnsPage(Fakes.Page(Enumerable.Range(1, 3).Select(i => Fakes.Message(h.ChatId, i, other))));
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);
        var missed = Enumerable.Range(4, 3).Select(i => Fakes.Message(h.ChatId, i, other)).ToList();
        var afterReconnect = Fakes.Message(h.ChatId, 7, other);
        h.PageRequest(after: 7).Returns(Fakes.Page([]));
        h.Api.GetMessageChangesAsync(h.ChatId, "cursor-1", Arg.Any<CancellationToken>())
            .Returns(new MessageChangesDto { Messages = [.. missed, afterReconnect], NextCursor = "cursor-2" });

        // Covered by another page (ActiveChatId null): the resync leaves the catch-up for later, and a message that
        // arrives after the reconnect lands past the hole.
        h.Realtime.Resynced += Raise.Event<Action>();
        await Task.Delay(50);
        h.RaiseCreated(afterReconnect);
        await Wait.UntilAsync(() => h.State.Items.Any(i => i.Message.Seq == 7));
        h.State.Items.Select(i => i.Message.Seq).ShouldBe([1, 2, 3, 7]);
        h.CallsTo(nameof(ISkyApi.GetMessageChangesAsync)).ShouldBe(0);

        h.Chats.ActiveChatId.Returns(h.ChatId);
        h.Chats.ActiveChatChanged += Raise.Event<Action>();

        await Wait.UntilAsync(() => h.State.Items.Count == 7, "back on screen, the catch-up fills the hole");
        h.State.Items.Select(i => i.Message.Seq).ShouldBe([1, 2, 3, 4, 5, 6, 7]);
    }

    [Fact]
    public async Task Jumping_into_a_window_restored_from_the_cache_jumps_at_once_and_still_catches_it_up()
    {
        var h = new MessageStoreHarness();
        var t0 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var cached = Enumerable.Range(1, 5).Select(i => Fakes.Message(h.ChatId, i, updatedAt: t0)).ToList();
        h.WriteCachedWindow(cached, "cursor-0");
        h.Chats.Get(h.ChatId).Returns(new ChatSummaryDto { Id = h.ChatId, Type = ChatType.Private, LastMessageSeq = 6 });
        h.PageRequest(after: 5).Returns(Fakes.Page([Fakes.Message(h.ChatId, 6, text: "after the cache was written")]));
        h.Api.GetMessageChangesAsync(h.ChatId, "cursor-0", Arg.Any<CancellationToken>()).Returns(new MessageChangesDto
        {
            Messages = [cached[2] with { Text = "edited elsewhere", UpdatedAt = t0.AddMinutes(5) }],
            NextCursor = "cursor-1",
        });
        var jumps = new List<long>();
        h.Store.JumpRequested += (_, seq) => jumps.Add(seq);
        await h.SignInAsync();

        // A search hit opens the chat with ?seq=: no OpenAsync on that path.
        await h.Store.JumpToAsync(h.ChatId, 3);

        jumps.ShouldBe([3]);
        await Wait.UntilAsync(() => h.State.Items.Any(i => i.Message.Seq == 6), "newer messages arrive after the jump");
        h.State.Items.Single(i => i.Message.Seq == 3).Message.Text.ShouldBe("edited elsewhere");
    }

    [Fact]
    public async Task A_failed_catch_up_of_the_chat_on_screen_runs_again_when_the_network_returns()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        h.ReturnsPage(Fakes.Page(Enumerable.Range(1, 3).Select(i => Fakes.Message(h.ChatId, i, other))));
        h.Chats.ActiveChatId.Returns(h.ChatId);
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);
        h.PageRequest(after: 3).ThrowsAsync(new ApiException(0, ClientErrorCodes.Timeout));
        h.Api.GetMessageChangesAsync(h.ChatId, "cursor-1", Arg.Any<CancellationToken>()).Returns(new MessageChangesDto { NextCursor = "cursor-2" });

        h.Realtime.Resynced += Raise.Event<Action>();
        await Wait.UntilAsync(() => h.CallsTo(nameof(ISkyApi.GetMessagesAsync)) == 2, "the resync catch-up ran");
        await Wait.UntilAsync(() => !h.State.IsLoadingAfter, "and failed");
        h.PageRequest(after: 3).Returns(Fakes.Page([Fakes.Message(h.ChatId, 4, other, "missed")]));
        h.Network.Changed += Raise.Event<Action>();

        await Wait.UntilAsync(() => h.State.Items.Any(i => i.Message.Seq == 4), "the catch-up is not left for the next open");
    }

    [Fact]
    public async Task Sending_from_an_older_window_returns_before_the_latest_page_and_lands_at_the_end()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        h.PageRequest(around: 1).Returns(Fakes.Page([Fakes.Message(h.ChatId, 1, other)], hasMoreAfter: true));
        var latest = new TaskCompletionSource<MessagesPageDto>();
        h.PageRequest().Returns(latest.Task);
        SendMessageRequest? sent = null;
        h.Api.SendMessageAsync(h.ChatId, Arg.Do<SendMessageRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(call => Fakes.Message(h.ChatId, 61, h.Me, "reply", clientMessageId: call.ArgAt<SendMessageRequest>(1).ClientMessageId));
        await h.SignInAsync();
        await h.Store.JumpToAsync(h.ChatId, 1);
        h.State.HasMoreAfter.ShouldBeTrue();

        await h.Store.SendTextAsync(h.ChatId, "reply").WaitAsync(TimeSpan.FromSeconds(3));

        h.State.Items[^1].IsPending.ShouldBeTrue("the pending item exists while the latest page is still loading");
        h.CacheStore.Entries[h.OutboxKey].ShouldContain("reply", Case.Sensitive, "and is on disk");
        sent.ShouldBeNull("the send itself waits for the window to reach the newest messages");
        // The chat screen scrolls to the newest messages right after sending.
        var scrolled = h.Store.JumpToLatestAsync(h.ChatId);

        latest.SetResult(Fakes.Page([Fakes.Message(h.ChatId, 59, other), Fakes.Message(h.ChatId, 60, other)]));
        await scrolled;

        await Wait.UntilAsync(() => h.State.Items.All(i => !i.IsPending) && h.State.Items.Count == 3, "confirmed at the end of the newest page");
        h.State.Items.Select(i => i.Message.Seq).ShouldBe([59, 60, 61]);
        h.Api.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISkyApi.GetMessagesAsync) && c.GetArguments()[3] is null)
            .ShouldBe(1, "one latest page for the send and the scroll");
    }

    [Fact]
    public async Task A_reaction_answered_after_a_jump_does_not_put_its_message_into_the_new_window()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        var recent = Enumerable.Range(50, 3).Select(i => Fakes.Message(h.ChatId, i, other)).ToList();
        h.ReturnsPage(Fakes.Page(recent, hasMoreBefore: true));
        var response = new TaskCompletionSource<MessageDto>();
        h.Api.SetReactionAsync(h.ChatId, recent[2].Id, "👍", Arg.Any<CancellationToken>()).Returns(response.Task);
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);

        var reacting = h.Store.SetReactionAsync(h.ChatId, recent[2].Id, "👍");
        h.PageRequest(around: 1).Returns(Fakes.Page([Fakes.Message(h.ChatId, 1, other), Fakes.Message(h.ChatId, 2, other)], hasMoreAfter: true));
        await h.Store.JumpToAsync(h.ChatId, 1);
        response.SetResult(recent[2] with { UpdatedAt = recent[2].UpdatedAt.AddMinutes(1) });
        await reacting;

        h.State.Items.Select(i => i.Message.Seq).ShouldBe([1, 2], "seqs 3..49 were never loaded: no hole may open in the window");
    }

    [Fact]
    public async Task An_unexpected_failure_of_a_page_load_does_not_leave_the_window_buffering_forever()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        h.ReturnsPage(Fakes.Page(Enumerable.Range(1, 3).Select(i => Fakes.Message(h.ChatId, i, other))));
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);
        // e.g. an HTTP stack that cannot be built from a malformed proxy host.
        h.PageRequest(around: 100).ThrowsAsync(new UriFormatException("Invalid URI: The hostname could not be parsed."));

        await h.Store.JumpToAsync(h.ChatId, 100);
        h.RaiseCreated(Fakes.Message(h.ChatId, 4, other));

        await Wait.UntilAsync(() => h.State.Items.Any(i => i.Message.Seq == 4), "realtime events are applied, not buffered");
        h.State.IsLoadingAfter.ShouldBeFalse();
    }

    [Fact]
    public async Task A_window_too_far_behind_starts_over_at_the_first_unread_instead_of_the_newest_page()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        h.WriteCachedWindow(Enumerable.Range(1, 5).Select(i => Fakes.Message(h.ChatId, i, other)), "cursor-0");
        // Cold start: the chat list is still the cached copy (read up to 5, nothing unread as far as it knows).
        h.Chats.Get(h.ChatId).Returns(new ChatSummaryDto { Id = h.ChatId, Type = ChatType.Group, LastMessageSeq = 5, MyLastReadSeq = 5 });
        // Hundreds of messages arrived since: the afterSeq pages of a catch-up do not reach the newest one.
        h.Api.GetMessagesAsync(h.ChatId, Arg.Is<long?>(v => v == null), Arg.Is<long?>(v => v != null), Arg.Is<long?>(v => v == null), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Fakes.Page(Enumerable.Range((int)call.ArgAt<long?>(2)!.Value + 1, 100).Select(i => Fakes.Message(h.ChatId, i, other)),
                hasMoreAfter: true));
        h.PageRequest(around: 6).Returns(Fakes.Page(Enumerable.Range(1, 30).Select(i => Fakes.Message(h.ChatId, i, other)), hasMoreAfter: true));
        h.PageRequest().Returns(Fakes.Page(Enumerable.Range(401, 50).Select(i => Fakes.Message(h.ChatId, i, other)), hasMoreBefore: true));
        await h.SignInAsync();

        await h.Store.OpenAsync(h.ChatId);

        h.State.Items.Select(i => i.Message.Seq).ShouldBe(Enumerable.Range(1, 30).Select(i => (long)i),
            "the newest page would skip 395 unread messages, and the screen marks what it shows as read");
        h.State.HasMoreAfter.ShouldBeTrue();
        h.State.FirstUnreadSeq.ShouldBe(6, "the divider sits where reading stopped although the cached summary counts nothing unread");
    }

    [Fact]
    public void Trimming_keeps_the_newest_messages_and_forgets_what_belonged_to_the_rest()
    {
        var chatId = Guid.CreateVersion7();
        var window = new MessageWindow(chatId);
        var messages = Enumerable.Range(1, 10).Select(i => Fakes.Message(chatId, i)).ToList();
        window.ApplyPage(Fakes.Page(messages), older: true, newer: true);
        window.LocalKeys[messages[0].Id] = Guid.CreateVersion7();
        window.LocalKeys[messages[9].Id] = Guid.CreateVersion7();
        // Confirmed while the window showed another part of the chat: never held, so never removed until now.
        window.LocalKeys[Guid.CreateVersion7()] = Guid.CreateVersion7();
        window.MyReactions[messages[0].Id] = "👍";

        window.TrimToNewest(4).ShouldBeTrue();

        window.Messages.Select(m => m.Seq).ShouldBe([7, 8, 9, 10]);
        window.HasMoreBefore.ShouldBeTrue("the dropped messages load again when scrolled to");
        window.LocalKeys.Keys.ShouldBe([messages[9].Id]);
        window.MyReactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_window_off_screen_stops_growing_with_every_new_message_unless_it_just_left_the_screen()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        h.ReturnsPage(Fakes.Page(Enumerable.Range(1, 50).Select(i => Fakes.Message(h.ChatId, i, other)), hasMoreBefore: true));
        h.Chats.ActiveChatId.Returns(h.ChatId);
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);
        h.Chats.ActiveChatChanged += Raise.Event<Action>();
        async Task ArriveAsync(int from, int to)
        {
            for (var seq = from; seq <= to; seq++) h.RaiseCreated(Fakes.Message(h.ChatId, seq, other));
            await Wait.UntilAsync(() => h.State.Items.Count > 0 && h.State.Items[^1].Message.Seq == to);
        }

        await ArriveAsync(51, 350);
        h.State.Items.Count.ShouldBe(350, "the chat on screen keeps everything (the user may be scrolled up)");

        // Covered by its info page a moment ago: the screen may come back to where it was.
        h.Chats.ActiveChatId.Returns((Guid?)null);
        h.Chats.ActiveChatChanged += Raise.Event<Action>();
        await ArriveAsync(351, 360);
        h.State.Items.Count.ShouldBe(360);

        // A chat that was never on screen in this session (or left long ago) is cut back to its newest messages.
        var idle = new MessageStoreHarness(h.Me);
        idle.ReturnsPage(Fakes.Page(Enumerable.Range(1, 50).Select(i => Fakes.Message(idle.ChatId, i, other)), hasMoreBefore: true));
        await idle.SignInAsync();
        await idle.Store.OpenAsync(idle.ChatId);
        for (var seq = 51; seq <= 400; seq++) idle.RaiseCreated(Fakes.Message(idle.ChatId, seq, other));
        await Wait.UntilAsync(() => idle.State.Items.Count > 0 && idle.State.Items[^1].Message.Seq == 400);
        idle.State.Items.Count.ShouldBeLessThanOrEqualTo(300);
        idle.State.Items.Select(i => i.Message.Seq).ShouldBe(Enumerable.Range(401 - idle.State.Items.Count, idle.State.Items.Count).Select(i => (long)i),
            "the newest messages, without holes");
        idle.State.HasMoreBefore.ShouldBeTrue();
    }

    [Fact]
    public async Task Deleting_more_than_one_batch_goes_in_chunks_and_only_the_refused_messages_come_back()
    {
        var h = new MessageStoreHarness();
        var other = Guid.CreateVersion7();
        var messages = Enumerable.Range(1, 250).Select(i => Fakes.Message(h.ChatId, i, other)).ToList();
        h.ReturnsPage(Fakes.Page(messages));
        var requests = new List<IReadOnlyList<Guid>>();
        h.Api.DeleteMessagesAsync(h.ChatId, Arg.Any<DeleteMessagesRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            requests.Add(call.Arg<DeleteMessagesRequest>().MessageIds);
            return requests.Count == 3 ? Task.FromException(new ApiException(403, ErrorCodes.Forbidden)) : Task.CompletedTask;
        });
        await h.SignInAsync();
        await h.Store.OpenAsync(h.ChatId);

        await Should.ThrowAsync<ApiException>(() => h.Store.DeleteAsync(h.ChatId, messages.Select(m => m.Id).ToList(), forEveryone: true));

        requests.Select(r => r.Count).ShouldBe([Limits.MaxMessagesPerBatch, Limits.MaxMessagesPerBatch, 50], "the server takes 100 ids per request");
        h.State.Items.Select(i => i.Message.Seq).ShouldBe(Enumerable.Range(201, 50).Select(i => (long)i), "the chunks the server accepted stay deleted");
    }

    private static OutgoingAttachment Attachment(MediaKind kind, int bytes = 1000) => new()
    {
        Kind = kind,
        DurationMs = kind is MediaKind.Voice or MediaKind.Video ? 1500 : null,
        Source = new PickedFile
        {
            FileName = kind == MediaKind.Voice ? "voice.webm" : kind == MediaKind.Video ? "clip.mp4" : "photo.jpg",
            ContentType = kind == MediaKind.Voice ? "audio/webm" : kind == MediaKind.Video ? "video/mp4" : "image/jpeg",
            Size = bytes,
            OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(new byte[bytes])),
        },
    };

    /// <summary>Uploads that create a session per file; <paramref name="complete"/> answers complete-upload per media kind.</summary>
    private static void AcceptUploads(MessageStoreHarness h, Func<Guid, MediaKind, Task<MediaDto>> complete)
    {
        var kinds = new System.Collections.Concurrent.ConcurrentDictionary<Guid, MediaKind>();
        h.Api.CreateUploadAsync(Arg.Any<CreateUploadRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = Guid.CreateVersion7();
            kinds[id] = call.Arg<CreateUploadRequest>().Kind;
            return new UploadSessionDto { MediaId = id, ChunkSize = 1024, ChunkCount = 1, Status = MediaStatus.Uploading };
        });
        h.Api.UploadChunkAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        h.Api.CompleteUploadAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => complete(call.ArgAt<Guid>(0), kinds[call.ArgAt<Guid>(0)]));
    }

    private static MediaDto Ready(Guid id, MediaKind kind) => new() { Id = id, Kind = kind, Status = MediaStatus.Ready, SizeBytes = 1000 };

    [Fact]
    public async Task The_rest_of_a_long_caption_is_sent_after_the_album_that_carries_its_beginning()
    {
        var h = new MessageStoreHarness();
        var upload = new TaskCompletionSource();
        AcceptUploads(h, async (id, kind) =>
        {
            await upload.Task;
            return Ready(id, kind);
        });
        var sent = new List<string>();
        var seq = 10L;
        h.Api.SendMessageAsync(h.ChatId, Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.ArgAt<SendMessageRequest>(1);
            lock (sent) sent.Add(request.MediaIds is { Count: > 0 } ? "album" : "caption tail");
            return Fakes.Message(h.ChatId, Interlocked.Increment(ref seq), h.Me, request.Text, clientMessageId: request.ClientMessageId);
        });
        await h.SignInAsync();
        var caption = new string('a', Limits.MessageTextMaxLength) + " the end";

        await h.Store.SendAttachmentsAsync(h.ChatId, [Attachment(MediaKind.Photo), Attachment(MediaKind.Photo)], caption);
        await Task.Delay(300);
        sent.ShouldBeEmpty("the end of the caption waits while the album uploads");

        upload.SetResult();
        await Wait.UntilAsync(() => sent.Count == 2 && h.State.Items.All(i => !i.IsPending));
        sent.ShouldBe(["album", "caption tail"]);
    }

    [Fact]
    public async Task A_voice_note_does_not_wait_behind_videos_that_are_still_uploading()
    {
        var h = new MessageStoreHarness();
        // The videos are slow to upload (a weak mobile uplink); the voice note is done at once.
        var videos = new TaskCompletionSource();
        AcceptUploads(h, async (id, kind) =>
        {
            if (kind != MediaKind.Voice) await videos.Task;
            return Ready(id, kind);
        });
        var sent = new List<SendMessageRequest>();
        h.Api.SendMessageAsync(h.ChatId, Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.ArgAt<SendMessageRequest>(1);
            lock (sent) sent.Add(request);
            return Fakes.Message(h.ChatId, 9 + sent.Count, h.Me, clientMessageId: request.ClientMessageId);
        });
        await h.SignInAsync();

        await h.Store.SendAttachmentsAsync(h.ChatId, [Attachment(MediaKind.Video)]);
        await h.Store.SendAttachmentsAsync(h.ChatId, [Attachment(MediaKind.Video)]);
        await Wait.UntilAsync(() => h.State.Items.Count(i => i.SendState == MessageSendState.Uploading) == 2, "both videos upload");
        await h.Store.SendAttachmentsAsync(h.ChatId, [Attachment(MediaKind.Voice)]);

        await Wait.UntilAsync(() => sent.Count == 1, "the voice note goes out while the videos are still uploading");
        await Wait.UntilAsync(() => h.State.Items.Count == 2, "the voice note is confirmed");
        h.State.Items.ShouldAllBe(i => i.Message.Type == MessageType.Video && i.SendState == MessageSendState.Uploading);
        videos.SetResult();
        await Wait.UntilAsync(() => sent.Count == 3, "the videos follow");
    }

    [Fact]
    public void Long_text_is_split_without_breaking_entities_inside_a_part()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 30));
        var entities = new List<MessageEntityDto> { new() { Type = MessageEntityType.Bold, Offset = 0, Length = 4 } };

        var parts = TextSplitter.Split(text, entities, maxLength: 40);

        parts.Count.ShouldBeGreaterThan(1);
        parts.All(p => p.Text.Length <= 40).ShouldBeTrue();
        string.Join(' ', parts.Select(p => p.Text)).ShouldBe(text);
        parts[0].Entities!.Single().Length.ShouldBe(4);
    }
}
