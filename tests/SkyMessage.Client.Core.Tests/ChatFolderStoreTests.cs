using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class ChatFolderStoreTests
{
    private sealed class Harness
    {
        public Guid Me { get; }
        public ISkyApi Api { get; } = Substitute.For<ISkyApi>();
        public IRealtimeService Realtime { get; } = Substitute.For<IRealtimeService>();
        public InMemoryCacheStore CacheStore { get; }
        public AccountLifecycle Lifecycle { get; }
        public ChatFolderStore Store { get; }

        public Harness(InMemoryCacheStore? cacheStore = null, Guid? me = null)
        {
            Me = me ?? Guid.CreateVersion7();
            CacheStore = cacheStore ?? new InMemoryCacheStore();
            var (cache, lifecycle) = Fakes.Account(CacheStore);
            Lifecycle = lifecycle;
            Store = new ChatFolderStore(Api, Realtime, cache, lifecycle, TimeProvider.System, Log.For<ChatFolderStore>());
        }

        public async Task LoadAsync(ChatFoldersDto state)
        {
            Api.GetChatFoldersAsync(Arg.Any<CancellationToken>()).Returns(state);
            await Lifecycle.ActivateAsync(Me);
            await Store.LoadAsync(force: true);
        }

        public void Raise(ChatFoldersDto state) => Realtime.ChatFoldersChanged += NSubstitute.Raise.Event<Action<ChatFoldersDto>>(state);
    }

    private static ChatFolderDto Folder(string name, params Guid[] pins) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = name,
        IncludeGroups = true,
        PinnedChatIds = pins,
    };

    private static ChatFoldersDto State(long revision, params ChatFolderDto[] folders) => new() { Revision = revision, Folders = folders };

    [Fact]
    public async Task Loads_the_server_state_and_newer_events_replace_it_while_older_ones_are_ignored()
    {
        var h = new Harness();
        var family = Folder("Family");
        await h.LoadAsync(State(3, family));
        h.Store.IsLoaded.ShouldBeTrue();
        h.Store.Folders.Select(f => f.Name).ShouldBe(["Family"]);

        h.Raise(State(2, Folder("Older")));
        h.Store.Folders.Select(f => f.Name).ShouldBe(["Family"]);

        h.Raise(State(4, family, Folder("Work")) with { Settings = new ChatListSettingsDto { HideArchive = true } });
        h.Store.Folders.Select(f => f.Name).ShouldBe(["Family", "Work"]);
        h.Store.Settings.HideArchive.ShouldBeTrue();
    }

    [Fact]
    public async Task Changes_show_at_once_and_the_answer_replaces_them()
    {
        var h = new Harness();
        await h.LoadAsync(State(1));
        var answer = new TaskCompletionSource<ChatFoldersDto>();
        h.Api.SaveChatFolderAsync(Arg.Any<Guid>(), Arg.Any<SaveChatFolderRequest>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        var folder = Folder("New");

        var saving = h.Store.SaveAsync(folder);
        h.Store.Folders.ShouldHaveSingleItem().Name.ShouldBe("New");

        answer.SetResult(State(2, folder with { Name = "New (server)" }));
        await saving;
        h.Store.Folders.ShouldHaveSingleItem().Name.ShouldBe("New (server)");
        h.Store.State.Revision.ShouldBe(2);
        await h.Api.Received(1).SaveChatFolderAsync(folder.Id, Arg.Is<SaveChatFolderRequest>(r => r.Name == "New" && r.IncludeGroups),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_change_is_put_back_and_rethrown()
    {
        var h = new Harness();
        var family = Folder("Family");
        await h.LoadAsync(State(1, family));
        h.Api.DeleteChatFolderAsync(family.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new ApiException(404, ErrorCodes.FolderNotFound, "gone"));

        await Should.ThrowAsync<ApiException>(() => h.Store.DeleteAsync(family.Id));

        h.Store.Folders.ShouldHaveSingleItem().Id.ShouldBe(family.Id);
    }

    [Fact]
    public async Task A_pending_change_stays_on_top_of_a_newer_state_from_another_device()
    {
        var h = new Harness();
        var chat = Guid.CreateVersion7();
        var family = Folder("Family");
        await h.LoadAsync(State(1, family));
        var answer = new TaskCompletionSource<ChatFoldersDto>();
        h.Api.SetFolderPinAsync(family.Id, chat, true, Arg.Any<CancellationToken>()).Returns(answer.Task);

        var pinning = h.Store.SetPinnedAsync(family.Id, chat, true);
        h.Raise(State(2, family with { Name = "Семья" }));

        h.Store.Folders.Single().ShouldSatisfyAllConditions(
            f => f.Name.ShouldBe("Семья"),
            f => f.PinnedChatIds.ShouldBe([chat]));

        answer.SetResult(State(3, family with { Name = "Семья", PinnedChatIds = [chat] }));
        await pinning;
        h.Store.State.Revision.ShouldBe(3);
        h.Store.Folders.Single().PinnedChatIds.ShouldBe([chat]);
    }

    [Fact]
    public async Task Starts_from_the_cached_state_of_the_account_and_a_reset_server_still_wins_once()
    {
        var cacheStore = new InMemoryCacheStore();
        var me = Guid.CreateVersion7();
        var first = new Harness(cacheStore, me);
        await first.LoadAsync(State(7, Folder("Cached")));
        await Wait.UntilAsync(() => cacheStore.Entries.Keys.Any(k => k.EndsWith("/folders", StringComparison.Ordinal)), "the state is cached");

        var restart = new Harness(cacheStore, me);
        await restart.Lifecycle.ActivateAsync(me);
        restart.Store.IsLoaded.ShouldBeTrue();
        restart.Store.Folders.Single().Name.ShouldBe("Cached");

        // The server has a lower revision than the cache (its data was reset): its state is the truth.
        restart.Api.GetChatFoldersAsync(Arg.Any<CancellationToken>()).Returns(State(1, Folder("Fresh")));
        await restart.Store.LoadAsync(force: true);
        restart.Store.Folders.Single().Name.ShouldBe("Fresh");
    }

    [Fact]
    public async Task Signing_out_forgets_the_folders()
    {
        var h = new Harness();
        await h.LoadAsync(State(2, Folder("Family")));

        await h.Lifecycle.SignedOutAsync(h.Me);

        h.Store.IsLoaded.ShouldBeFalse();
        h.Store.Folders.ShouldBeEmpty();
    }

    // ---- Pure edits and the folder view ----

    [Fact]
    public void Edits_match_what_the_server_does()
    {
        var a = Folder("A");
        var b = Folder("B");
        var c = Folder("C");
        var state = State(1, a, b, c);
        var x = Guid.CreateVersion7();
        var y = Guid.CreateVersion7();

        ChatFolderEdits.Reorder(state, [c.Id, a.Id]).Folders.Select(f => f.Name).ShouldBe(["C", "B", "A"]);
        ChatFolderEdits.Delete(state, b.Id).Folders.Select(f => f.Name).ShouldBe(["A", "C"]);

        var pinned = ChatFolderEdits.SetPinned(ChatFolderEdits.SetPinned(state, a.Id, x, true), a.Id, y, true);
        pinned.Folders[0].PinnedChatIds.ShouldBe([y, x]);
        ChatFolderEdits.ReorderPinned(pinned, a.Id, [x, y]).Folders[0].PinnedChatIds.ShouldBe([x, y]);
        ChatFolderEdits.SetPinned(pinned, a.Id, y, false).Folders[0].PinnedChatIds.ShouldBe([x]);

        // Saving keeps the pins (they have their own calls) and a new folder goes last.
        var saved = ChatFolderEdits.Save(pinned, a with { Name = "A2", PinnedChatIds = [] });
        saved.Folders[0].ShouldSatisfyAllConditions(f => f.Name.ShouldBe("A2"), f => f.PinnedChatIds.ShouldBe([y, x]));
        ChatFolderEdits.Save(state, Folder("D")).Folders.Select(f => f.Name).ShouldBe(["A", "B", "C", "D"]);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static ChatSummaryDto Chat(string title, int activity, ChatType type = ChatType.Group, int unread = 0, bool muted = false,
        bool archived = false) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = type,
        Title = title,
        UnreadCount = unread,
        LastActivityAt = Now.AddMinutes(activity),
        MutedUntil = muted ? Now.AddDays(1) : null,
        IsArchived = archived,
    };

    [Fact]
    public void A_folder_lists_its_own_pins_first_then_by_activity()
    {
        var quiet = Chat("quiet", 1);
        var busy = Chat("busy", 10);
        var pinnedOld = Chat("pinned old", 0);
        var pinnedTop = Chat("pinned top", 5);
        var person = Chat("person", 20, ChatType.Private);
        var folder = new ChatFolderDto { IncludeGroups = true, PinnedChatIds = [pinnedTop.Id, Guid.CreateVersion7(), pinnedOld.Id] };

        ChatFolderView.ChatsOf(folder, [quiet, busy, pinnedOld, pinnedTop, person], _ => false, Now).Select(c => c.Title)
            .ShouldBe(["pinned top", "pinned old", "busy", "quiet"]);
        ChatFolderView.IsPinned(folder, pinnedOld.Id).ShouldBeTrue();
    }

    [Fact]
    public void Contacts_decide_where_private_chats_go_and_an_open_chat_stays_in_an_unread_folder()
    {
        var friend = Chat("friend", 1, ChatType.Private);
        var stranger = Chat("stranger", 2, ChatType.Private, unread: 1);
        var contacts = new ChatFolderDto { IncludeContacts = true };
        bool IsContact(ChatSummaryDto c) => c.Id == friend.Id;

        ChatFolderView.ChatsOf(contacts, [friend, stranger], IsContact, Now).ShouldBe([friend]);
        ChatFolderView.Count(new ChatFolderDto { IncludeNonContacts = true }, [friend, stranger], IsContact, Now).ShouldBe(1);

        var unread = new ChatFolderDto { IncludeContacts = true, IncludeNonContacts = true, ExcludeRead = true };
        ChatFolderView.ChatsOf(unread, [friend, stranger], IsContact, Now).ShouldBe([stranger]);
        ChatFolderView.ChatsOf(unread, [friend, stranger], IsContact, Now, keepChatId: friend.Id).Select(c => c.Title).ShouldBe(["stranger", "friend"]);
        // Keeping only bridges "read": a chat the folder leaves out for another reason stays out.
        ChatFolderView.ChatsOf(unread with { ExcludedChatIds = [friend.Id] }, [friend, stranger], IsContact, Now, keepChatId: friend.Id).ShouldBe([stranger]);
    }

    [Fact]
    public void Badges_count_unread_chats_and_fall_back_to_the_muted_ones()
    {
        var loud = Chat("loud", 1, unread: 4);
        var mentioned = Chat("mentioned", 2) with { UnreadMentionCount = 1 };
        var quiet = Chat("quiet", 3, unread: 9, muted: true);
        var read = Chat("read", 4);

        ChatFolderView.BadgeOf([loud, mentioned, quiet, read], Now).ShouldBe(new FolderBadge(2, false));
        ChatFolderView.BadgeOf([quiet, read], Now).ShouldBe(new FolderBadge(1, true));
        ChatFolderView.BadgeOf([read], Now).ShouldBe(new FolderBadge(0, false));
    }

    [Fact]
    public void Presets_are_recognized_by_their_rules_only()
    {
        foreach (var preset in ChatFolderPresets.All)
        {
            var folder = ChatFolderPresets.Create(preset, "Name");
            folder.Id.ShouldNotBe(Guid.Empty);
            folder.Icon.ShouldBe(preset.Icon);
            ChatFolderPresets.Matching(folder).ShouldBe(preset);
            ChatFolderPresets.Matching(folder with { IncludedChatIds = [Guid.CreateVersion7()] }).ShouldBeNull();
        }
        ChatFolderPresets.Matching(new ChatFolderDto { IncludeGroups = true, ExcludeMuted = true }).ShouldBeNull();
        ChatFolderPresets.All.Select(p => p.Key).ShouldBe([ChatFolderPresets.New, ChatFolderPresets.Contacts, ChatFolderPresets.Groups,
            ChatFolderPresets.Channels, ChatFolderPresets.Unread]);
    }
}
