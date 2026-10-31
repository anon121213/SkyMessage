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

public sealed class ChatListStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public Guid Me { get; }
        public ISkyApi Api { get; } = Substitute.For<ISkyApi>();
        public IRealtimeService Realtime { get; } = Substitute.For<IRealtimeService>();
        public ILocalNotificationService Notifications { get; } = Substitute.For<ILocalNotificationService>();
        public InMemoryCacheStore CacheStore { get; }
        public AccountLifecycle Lifecycle { get; }
        public ChatListStore Store { get; }

        /// <param name="cacheStore">Storage left by an earlier process of the same account (<paramref name="me"/>): a restart.</param>
        public Harness(InMemoryCacheStore? cacheStore = null, Guid? me = null)
        {
            Me = me ?? Guid.CreateVersion7();
            CacheStore = cacheStore ?? new InMemoryCacheStore();
            var (cache, lifecycle) = Fakes.Account(CacheStore);
            Lifecycle = lifecycle;
            Realtime.BeginSync().Returns(Substitute.For<IDisposable>());
            var users = Substitute.For<IUserStore>();
            Store = new ChatListStore(Api, Realtime, Fakes.Auth(Me), users, Notifications, cache, lifecycle, Fakes.Settings(),
                Substitute.For<IServiceProvider>(), TimeProvider.System, Log.For<ChatListStore>());
        }

        public async Task LoadAsync(params ChatSummaryDto[] chats)
        {
            Api.GetChatsAsync(Arg.Any<CancellationToken>()).Returns(chats);
            await Lifecycle.ActivateAsync(Me);
            await Store.LoadAsync(force: true);
        }

        public void RaiseCreated(MessageDto message) => Realtime.MessageCreated += Raise.Event<Action<MessageDto>>(message);
    }

    private static ChatSummaryDto Chat(string title, int activityMinutes, long lastSeq = 10, int unread = 0, bool pinned = false,
        int pinMinutes = 0, bool archived = false, DateTimeOffset? mutedUntil = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = ChatType.Group,
        Title = title,
        LastMessageSeq = lastSeq,
        MyLastReadSeq = lastSeq - unread,
        UnreadCount = unread,
        LastActivityAt = T0.AddMinutes(activityMinutes),
        IsPinned = pinned,
        PinnedAt = pinned ? T0.AddMinutes(pinMinutes) : null,
        IsArchived = archived,
        MutedUntil = mutedUntil,
    };

    [Fact]
    public async Task Pinned_chats_come_first_newest_pin_first_then_by_activity()
    {
        var h = new Harness();
        var oldPin = Chat("old pin", 1, pinned: true, pinMinutes: 1);
        var newPin = Chat("new pin", 0, pinned: true, pinMinutes: 5);
        var quiet = Chat("quiet", 10);
        var busy = Chat("busy", 20);
        var archived = Chat("archived", 30, archived: true);

        await h.LoadAsync(oldPin, quiet, archived, busy, newPin);

        h.Store.Chats.Select(c => c.Title).ShouldBe(["new pin", "old pin", "busy", "quiet"]);
        h.Store.ArchivedChats.Select(c => c.Title).ShouldBe(["archived"]);
        h.Store.IsLoaded.ShouldBeTrue();
    }

    [Fact]
    public async Task Incoming_messages_raise_unread_and_move_the_chat_up_while_muted_chats_stay_out_of_the_total()
    {
        var h = new Harness();
        var first = Chat("first", 20, lastSeq: 10, unread: 1);
        var second = Chat("second", 10, lastSeq: 5);
        var muted = Chat("muted", 5, lastSeq: 7, unread: 4, mutedUntil: DateTimeOffset.MaxValue);
        await h.LoadAsync(first, second, muted);
        h.Store.TotalUnread.ShouldBe(1);

        h.RaiseCreated(Fakes.Message(second.Id, 6, Guid.CreateVersion7()) with { CreatedAt = T0.AddMinutes(30) });

        await Wait.UntilAsync(() => h.Store.Get(second.Id)!.UnreadCount == 1);
        h.Store.Chats[0].Id.ShouldBe(second.Id);
        h.Store.Get(second.Id)!.LastMessageSeq.ShouldBe(6);
        h.Store.TotalUnread.ShouldBe(2);
        await Wait.UntilAsync(() => h.Notifications.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ILocalNotificationService.SetBadgeCountAsync)
                                                                            && (int)c.GetArguments()[0]! == 2), "badge follows the total");
    }

    [Fact]
    public async Task Replayed_or_old_messages_do_not_count_twice_and_my_own_message_reads_the_chat()
    {
        var h = new Harness();
        var chat = Chat("chat", 1, lastSeq: 10, unread: 3);
        await h.LoadAsync(chat);

        h.RaiseCreated(Fakes.Message(chat.Id, 9, Guid.CreateVersion7()));
        await Task.Delay(50);
        h.Store.Get(chat.Id)!.UnreadCount.ShouldBe(3);

        h.RaiseCreated(Fakes.Message(chat.Id, 11, h.Me));
        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.UnreadCount == 0);
        h.Store.Get(chat.Id)!.MyLastReadSeq.ShouldBe(11);
    }

    [Fact]
    public async Task Read_on_another_device_clears_the_counters()
    {
        var h = new Harness();
        var chat = Chat("chat", 1, lastSeq: 10, unread: 3);
        await h.LoadAsync(chat);

        h.Realtime.MessagesRead += Raise.Event<Action<MessagesReadEvent>>(new MessagesReadEvent { ChatId = chat.Id, UserId = h.Me, UpToSeq = 10 });

        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.UnreadCount == 0);
        h.Store.TotalUnread.ShouldBe(0);
        await h.Notifications.Received().CancelForChatAsync(chat.Id);
    }

    [Fact]
    public async Task Events_arriving_during_a_reload_are_replayed_on_top_of_the_snapshot()
    {
        var h = new Harness();
        var chat = Chat("chat", 1, lastSeq: 10);
        await h.LoadAsync(chat);
        var snapshot = new TaskCompletionSource<IReadOnlyList<ChatSummaryDto>>();
        h.Api.GetChatsAsync(Arg.Any<CancellationToken>()).Returns(snapshot.Task);

        var reload = h.Store.LoadAsync(force: true);
        await Task.Delay(30);
        h.RaiseCreated(Fakes.Message(chat.Id, 11, Guid.CreateVersion7()) with { CreatedAt = T0.AddHours(1) });
        await Task.Delay(30);
        snapshot.SetResult([chat]);
        await reload;

        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.LastMessageSeq == 11, "the buffered event survives the older snapshot");
        h.Store.Get(chat.Id)!.UnreadCount.ShouldBe(1);
    }

    [Fact]
    public void Merging_never_moves_the_last_message_backwards()
    {
        var held = Chat("chat", 5, lastSeq: 20) with { LastMessage = new MessageDto { Seq = 20, Text = "newest" } };
        var stale = held with { LastMessageSeq = 18, LastMessage = new MessageDto { Seq = 18, Text = "older" }, Title = "renamed" };

        var merged = ChatListStore.Merge(held, stale);

        merged.LastMessageSeq.ShouldBe(20);
        merged.LastMessage!.Text.ShouldBe("newest");
        merged.Title.ShouldBe("renamed", "other fields still come from the server copy");
    }

    [Fact]
    public async Task My_own_service_messages_read_the_chat_while_other_members_ones_count_as_unread()
    {
        var h = new Harness();
        var chat = Chat("family", 1, lastSeq: 10);
        await h.LoadAsync(chat);
        MessageDto Renamed(long seq, Guid actor) => Fakes.Message(chat.Id, seq) with
        {
            SenderId = null,
            Type = MessageType.System,
            Text = null,
            SystemEvent = new SystemEventDto { Type = SystemEventType.TitleChanged, ActorId = actor, Text = "Family" },
        };

        h.RaiseCreated(Renamed(11, h.Me));
        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.LastMessageSeq == 11);
        h.Store.Get(chat.Id)!.UnreadCount.ShouldBe(0, "the server moved my LastReadSeq to my own service message");
        h.Store.Get(chat.Id)!.MyLastReadSeq.ShouldBe(11);
        h.Store.TotalUnread.ShouldBe(0);

        h.RaiseCreated(Renamed(12, Guid.CreateVersion7()));
        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.LastMessageSeq == 12);
        h.Store.Get(chat.Id)!.UnreadCount.ShouldBe(1);
    }

    [Fact]
    public async Task An_unexpected_failure_of_a_reload_does_not_leave_the_list_loading()
    {
        var h = new Harness();
        var chat = Chat("chat", 1, lastSeq: 10);
        await h.LoadAsync(chat);
        // e.g. an HTTP stack that cannot be built from a malformed proxy host.
        h.Api.GetChatsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new UriFormatException("Invalid URI: The hostname could not be parsed."));

        await h.Store.LoadAsync(force: true);

        h.Store.IsLoading.ShouldBeFalse();
        h.RaiseCreated(Fakes.Message(chat.Id, 11, Guid.CreateVersion7()));
        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.UnreadCount == 1, "events are applied, not buffered behind a load that never ends");
    }

    [Fact]
    public async Task A_partial_read_is_refetched_only_once_the_server_has_it_and_never_goes_back()
    {
        var h = new Harness();
        var chat = Chat("chat", 1, lastSeq: 20, unread: 10);
        await h.LoadAsync(chat);
        int Refetches() => h.Api.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISkyApi.GetChatAsync));
        // Until the read request lands, the server copy still has the old mark.
        h.Api.GetChatAsync(chat.Id, Arg.Any<CancellationToken>()).Returns(chat);
        var posted = new TaskCompletionSource();
        h.Api.MarkReadAsync(chat.Id, 15, Arg.Any<CancellationToken>()).Returns(posted.Task);

        await h.Store.MarkReadAsync(chat.Id, 15);
        await Task.Delay(700);
        Refetches().ShouldBe(0, "a copy fetched before the server has the read would carry the old mark");
        // A refetch for another reason (a message deleted elsewhere) still brings that older copy.
        h.Realtime.MessagesDeleted += Raise.Event<Action<MessagesDeletedEvent>>(new MessagesDeletedEvent { ChatId = chat.Id, MessageIds = [Guid.CreateVersion7()] });
        await Wait.UntilAsync(() => Refetches() == 1);
        await Task.Delay(100);

        h.Store.Get(chat.Id)!.MyLastReadSeq.ShouldBe(15, "read marks never move backwards");
        h.Store.Get(chat.Id)!.UnreadCount.ShouldBe(5);
        h.Store.TotalUnread.ShouldBe(5);

        // The server has the mark: its copy has the exact count (one of the five left is a deleted message).
        h.Api.GetChatAsync(chat.Id, Arg.Any<CancellationToken>()).Returns(chat with { MyLastReadSeq = 15, UnreadCount = 4 });
        posted.SetResult();
        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.UnreadCount == 4, "refetched after the read reached the server");
    }

    [Fact]
    public async Task A_read_that_could_not_be_sent_reaches_the_server_after_a_restart()
    {
        var first = new Harness();
        var chat = Chat("chat", 1, lastSeq: 20, unread: 10);
        await first.LoadAsync(chat);
        first.Api.MarkReadAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ApiException(0, ClientErrorCodes.Network));
        await first.Store.MarkReadAsync(chat.Id, 20);
        var key = LocalCache.AccountKey(first.Me, "chats");
        await Wait.UntilAsync(() => first.CacheStore.Entries.TryGetValue(key, out var json) && json.Contains("\"myLastReadSeq\":20"), "the read is in the cached list");

        // Read offline, then the app was killed: the server still has the old mark when it starts again.
        var second = new Harness(first.CacheStore.Clone(), first.Me);
        await second.LoadAsync(chat);

        second.Store.Get(chat.Id)!.UnreadCount.ShouldBe(0, "the read is not undone by the server copy");
        await Wait.UntilAsync(() => second.Api.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ISkyApi.MarkReadAsync)), "the read is sent again");
        await second.Api.Received(1).MarkReadAsync(chat.Id, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_message_published_out_of_order_is_counted_through_a_refetch()
    {
        var h = new Harness();
        var chat = Chat("family", 1, lastSeq: 10);
        await h.LoadAsync(chat);
        // Both sends were committed before their events went out: the server copy counts both.
        h.Api.GetChatAsync(chat.Id, Arg.Any<CancellationToken>())
            .Returns(chat with { LastMessageSeq = 12, UnreadCount = 2, UnreadMentionCount = 1, FirstUnreadMentionSeq = 11 });

        h.RaiseCreated(Fakes.Message(chat.Id, 12, Guid.CreateVersion7()) with { CreatedAt = T0.AddMinutes(30) });
        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.UnreadCount == 1);
        h.RaiseCreated(Fakes.Message(chat.Id, 11, Guid.CreateVersion7(), "@me_user look") with { CreatedAt = T0.AddMinutes(29) });

        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.UnreadCount == 2, "the late message is counted");
        h.Store.Get(chat.Id)!.UnreadMentionCount.ShouldBe(1);
        h.Store.Get(chat.Id)!.FirstUnreadMentionSeq.ShouldBe(11);
    }

    [Fact]
    public async Task Mark_read_updates_counters_at_once_and_reaches_the_server_debounced()
    {
        var h = new Harness();
        var chat = Chat("chat", 1, lastSeq: 10, unread: 4);
        await h.LoadAsync(chat);

        await h.Store.MarkReadAsync(chat.Id, 8);
        await h.Store.MarkReadAsync(chat.Id, 10);

        h.Store.Get(chat.Id)!.UnreadCount.ShouldBe(0);
        await Wait.UntilAsync(() => h.Api.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ISkyApi.MarkReadAsync)));
        await h.Api.Received(1).MarkReadAsync(chat.Id, 10, Arg.Any<CancellationToken>());
    }

    // ---- Pinned chats order, contact bar, content protection ----

    private static ChatSummaryDto Pinned(string title, long order, int pinMinutes = 0, bool archived = false) =>
        Chat(title, 0, pinned: true, pinMinutes: pinMinutes, archived: archived) with { PinOrder = order };

    private static PinnedChatsOrderDto Order(params ChatSummaryDto[] topFirst) => new()
    {
        Chats = topFirst.Select((c, i) => new ChatPinOrderDto { ChatId = c.Id, PinOrder = topFirst.Length - i }).ToList(),
    };

    [Fact]
    public async Task Pinned_chats_follow_my_pin_order_and_chats_cached_without_one_fall_back_to_the_pin_time()
    {
        var h = new Harness();
        var a = Pinned("a", 1, pinMinutes: 9);
        var b = Pinned("b", 3, pinMinutes: 1);
        var c = Pinned("c", 2, pinMinutes: 5);
        var oldNew = Pinned("cached, newer pin", 0, pinMinutes: 8);
        var oldOld = Pinned("cached, older pin", 0, pinMinutes: 2);
        var plain = Chat("plain", 50);

        await h.LoadAsync(plain, a, oldOld, b, c, oldNew);

        h.Store.Chats.Select(x => x.Title).ShouldBe(["b", "c", "a", "cached, newer pin", "cached, older pin", "plain"]);
    }

    [Fact]
    public async Task Reordering_shows_the_new_order_at_once_and_takes_the_servers_numbers()
    {
        var h = new Harness();
        var a = Pinned("a", 3);
        var b = Pinned("b", 2);
        var c = Pinned("c", 1);
        var archived = Pinned("archived", 7, archived: true);
        var plain = Chat("plain", 5);
        await h.LoadAsync(a, b, c, archived, plain);
        var answer = new TaskCompletionSource<PinnedChatsOrderDto>();
        h.Api.ReorderPinnedChatsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>()).Returns(answer.Task);

        var reorder = h.Store.ReorderPinnedAsync([c.Id, a.Id, b.Id]);

        await Wait.UntilAsync(() => h.Store.Chats[0].Id == c.Id, "the drag's result shows before the server answers");
        h.Store.Chats.Select(x => x.Title).ShouldBe(["c", "a", "b", "plain"]);
        h.Store.ArchivedChats.Single().PinOrder.ShouldBe(4, "the archived pin keeps its place above them, renumbered like the server does");
        await h.Api.Received(1).ReorderPinnedChatsAsync(Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { c.Id, a.Id, b.Id })), Arg.Any<CancellationToken>());

        answer.SetResult(Order(archived, c, a, b));
        await reorder;
        h.Store.Get(c.Id)!.PinOrder.ShouldBe(3);
        h.Store.Get(plain.Id)!.PinOrder.ShouldBe(0);
    }

    [Fact]
    public async Task A_refused_reorder_puts_the_old_order_back()
    {
        var h = new Harness();
        var a = Pinned("a", 2);
        var b = Pinned("b", 1);
        await h.LoadAsync(a, b);
        h.Api.ReorderPinnedChatsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(400, ErrorCodes.Validation, "no"));

        await Should.ThrowAsync<ApiException>(() => h.Store.ReorderPinnedAsync([b.Id, a.Id]));

        h.Store.Chats.Select(x => x.Title).ShouldBe(["a", "b"]);
        h.Store.Get(a.Id)!.PinOrder.ShouldBe(2);
    }

    [Fact]
    public async Task An_order_made_on_another_device_arrives_in_realtime()
    {
        var h = new Harness();
        var a = Pinned("a", 2);
        var b = Pinned("b", 1);
        await h.LoadAsync(a, b);

        h.Realtime.PinnedChatsReordered += Raise.Event<Action<PinnedChatsOrderDto>>(Order(b, a));

        await Wait.UntilAsync(() => h.Store.Chats[0].Id == b.Id);
        h.Store.Get(a.Id)!.PinOrder.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_pin_goes_on_top_and_unpinning_drops_the_order()
    {
        var h = new Harness();
        var a = Pinned("a", 4);
        var b = Pinned("b", 9);
        var fresh = Chat("fresh", 1);
        await h.LoadAsync(a, b, fresh);
        var answer = new TaskCompletionSource<ChatUserSettingsDto>();
        h.Api.UpdateChatSettingsAsync(fresh.Id, Arg.Any<UpdateChatSettingsRequest>(), Arg.Any<CancellationToken>()).Returns(answer.Task);

        var pin = h.Store.SetPinnedAsync(fresh.Id, true);

        await Wait.UntilAsync(() => h.Store.Chats[0].Id == fresh.Id);
        h.Store.Get(fresh.Id)!.PinOrder.ShouldBe(10);
        answer.SetResult(new ChatUserSettingsDto { IsPinned = true, PinnedAt = T0, PinOrder = 10 });
        await pin;

        h.Api.UpdateChatSettingsAsync(b.Id, Arg.Any<UpdateChatSettingsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatUserSettingsDto { IsPinned = false, PinOrder = 0 });
        await h.Store.SetPinnedAsync(b.Id, false);
        h.Store.Get(b.Id)!.PinOrder.ShouldBe(0);
        h.Store.Chats.Select(x => x.Title).ShouldBe(["fresh", "a", "b"]);
    }

    [Fact]
    public async Task A_refused_settings_change_keeps_my_pin_order_and_closed_contact_bar()
    {
        var h = new Harness();
        var chat = Pinned("a", 5) with { Type = ChatType.Private, ContactBarDismissed = true };
        await h.LoadAsync(chat);
        h.Api.UpdateChatSettingsAsync(chat.Id, Arg.Any<UpdateChatSettingsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(500, ClientErrorCodes.Network, "down"));

        await Should.ThrowAsync<ApiException>(() => h.Store.SetMutedAsync(chat.Id, -1));

        var back = h.Store.Get(chat.Id)!;
        back.PinOrder.ShouldBe(5);
        back.ContactBarDismissed.ShouldBeTrue();
        back.MutedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task Closing_the_contact_bar_is_sent_and_other_devices_follow_through_settings_events()
    {
        var h = new Harness();
        var chat = Chat("stranger", 1) with { Type = ChatType.Private };
        var other = Chat("other", 2) with { Type = ChatType.Private };
        await h.LoadAsync(chat, other);
        h.Api.UpdateChatSettingsAsync(chat.Id, Arg.Any<UpdateChatSettingsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatUserSettingsDto { ContactBarDismissed = true });

        await h.Store.SetContactBarDismissedAsync(chat.Id);

        h.Store.Get(chat.Id)!.ContactBarDismissed.ShouldBeTrue();
        await h.Api.Received(1).UpdateChatSettingsAsync(chat.Id, Arg.Is<UpdateChatSettingsRequest>(r => r.ContactBarDismissed == true && r.IsPinned == null),
            Arg.Any<CancellationToken>());

        h.Realtime.ChatSettingsChanged += Raise.Event<Action<ChatSettingsChangedEvent>>(new ChatSettingsChangedEvent
        {
            ChatId = other.Id,
            Settings = new ChatUserSettingsDto { ContactBarDismissed = true },
        });
        await Wait.UntilAsync(() => h.Store.Get(other.Id)!.ContactBarDismissed);
    }

    [Fact]
    public async Task Protecting_a_private_chat_shows_at_once_and_follows_the_servers_answer_and_events()
    {
        var h = new Harness();
        var peer = Guid.CreateVersion7();
        var chat = Chat("peer", 1) with { Type = ChatType.Private, MemberCount = 2 };
        await h.LoadAsync(chat);
        var answer = new TaskCompletionSource<ChatInfoDto>();
        h.Api.SetContentProtectionAsync(chat.Id, true, Arg.Any<CancellationToken>()).Returns(answer.Task);

        var turnOn = h.Store.SetContentProtectionAsync(chat.Id, true);

        await Wait.UntilAsync(() => h.Store.Get(chat.Id)!.ProtectContent);
        h.Store.Get(chat.Id)!.ProtectContentBy.ShouldBe([h.Me]);
        answer.SetResult(new ChatInfoDto { Id = chat.Id, Type = ChatType.Private, MemberCount = 2, ProtectContent = true, ProtectContentBy = [peer, h.Me] });
        await turnOn;
        h.Store.Get(chat.Id)!.ProtectContentBy.ShouldBe([peer, h.Me]);

        // The peer takes theirs back elsewhere, then I take mine: ChatUpdated carries the state to private chats too.
        h.Realtime.ChatUpdated += Raise.Event<Action<ChatInfoDto>>(new ChatInfoDto
        {
            Id = chat.Id, Type = ChatType.Private, MemberCount = 2, ProtectContent = false, ProtectContentBy = [],
        });
        await Wait.UntilAsync(() => !h.Store.Get(chat.Id)!.ProtectContent);
        h.Store.Get(chat.Id)!.Title.ShouldBe("peer", "a private chat keeps its own title");
    }

    [Fact]
    public async Task A_refused_protection_change_is_put_back()
    {
        var h = new Harness();
        var group = Chat("family", 1);
        await h.LoadAsync(group);
        h.Api.SetContentProtectionAsync(group.Id, true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(403, ErrorCodes.NotChatAdmin, "no"));

        await Should.ThrowAsync<ApiException>(() => h.Store.SetContentProtectionAsync(group.Id, true));

        h.Store.Get(group.Id)!.ProtectContent.ShouldBeFalse();
    }
}

public sealed class UserStoreTests
{
    [Fact]
    public async Task Unknown_users_are_fetched_in_one_batch_without_duplicates()
    {
        var api = Substitute.For<ISkyApi>();
        var requested = new List<IReadOnlyCollection<Guid>>();
        api.GetUsersAsync(Arg.Do<IReadOnlyCollection<Guid>>(ids => requested.Add(ids.ToList())), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<Guid>>(0)
                .Select(id => new UserSummaryDto { Id = id, Username = "u" + id.ToString("N")[..6], DisplayName = "User" })
                .ToList());
        var (cache, lifecycle) = Fakes.Account();
        var store = new UserStore(api, Substitute.For<IRealtimeService>(), Fakes.Auth(Guid.CreateVersion7()), cache, lifecycle,
            TimeProvider.System, Log.For<UserStore>());
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();

        var results = await Task.WhenAll(store.GetOrFetchAsync(a), store.GetOrFetchAsync(b), store.GetOrFetchAsync(a), store.GetOrFetchAsync(c));

        results.ShouldAllBe(u => u != null);
        requested.Count.ShouldBe(1);
        requested[0].ShouldBe([a, b, c], ignoreOrder: true);
        store.Get(b)!.DisplayName.ShouldBe("User");
        (await store.GetOrFetchAsync(a)).ShouldNotBeNull();
        requested.Count.ShouldBe(1, "known users are served from memory");
    }

    [Fact]
    public async Task Display_name_prefers_the_contact_name_and_presence_events_patch_the_cache()
    {
        var realtime = Substitute.For<IRealtimeService>();
        var (cache, lifecycle) = Fakes.Account();
        var store = new UserStore(Substitute.For<ISkyApi>(), realtime, Fakes.Auth(Guid.CreateVersion7()), cache, lifecycle,
            TimeProvider.System, Log.For<UserStore>());
        var id = Guid.CreateVersion7();
        var changed = new List<Guid>();
        store.UserChanged += changed.Add;

        store.Upsert([new UserSummaryDto { Id = id, Username = "anna", DisplayName = "Anna K", ContactName = "Мама" }]);
        realtime.PresenceChanged += Raise.Event<Action<PresenceChangedEvent>>(new PresenceChangedEvent
        {
            UserId = id,
            Presence = new PresenceDto { IsOnline = true, Approx = LastSeenApprox.Online },
        });

        store.DisplayNameOf(id).ShouldBe("Мама");
        store.Get(id)!.Presence.IsOnline.ShouldBeTrue();
        changed.ShouldBe([id, id]);
        await Task.CompletedTask;
    }
}
