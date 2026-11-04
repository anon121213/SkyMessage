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

        public void Resync() => Realtime.Resynced += NSubstitute.Raise.Event<Action>();
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

        var saving = h.Store.CreateAsync(folder);
        h.Store.Folders.ShouldHaveSingleItem().Name.ShouldBe("New");

        answer.SetResult(State(2, folder with { Name = "New (server)" }));
        await saving;
        h.Store.Folders.ShouldHaveSingleItem().Name.ShouldBe("New (server)");
        h.Store.State.Revision.ShouldBe(2);
        // Every field the app knows goes out, and that it is a new folder.
        await h.Api.Received(1).SaveChatFolderAsync(folder.Id,
            Arg.Is<SaveChatFolderRequest>(r => r.Create && r.Name == "New" && r.IncludeGroups == true && r.IncludeContacts == false && r.Icon == ""
                                               && r.IncludedChatIds != null && r.ExcludedChatIds != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saving_a_folder_deleted_on_another_device_does_not_bring_it_back()
    {
        var h = new Harness();
        var family = Folder("Family");
        var work = Folder("Work");
        await h.LoadAsync(State(1, family, work));
        var answer = new TaskCompletionSource<ChatFoldersDto>();
        h.Api.SaveChatFolderAsync(family.Id, Arg.Any<SaveChatFolderRequest>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        h.Raise(State(2, work));

        var saving = h.Store.SaveAsync(family with { Name = "Семья" });
        h.Store.Folders.Select(f => f.Name).ShouldBe(["Work"]);
        answer.SetException(new ApiException(404, ErrorCodes.FolderNotFound, "gone"));

        (await Should.ThrowAsync<ApiException>(() => saving)).Code.ShouldBe(ErrorCodes.FolderNotFound);
        h.Store.Folders.Select(f => f.Name).ShouldBe(["Work"]);
        await h.Api.Received(1).SaveChatFolderAsync(family.Id, Arg.Is<SaveChatFolderRequest>(r => !r.Create), Arg.Any<CancellationToken>());
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
    public async Task A_first_load_that_failed_is_tried_again_on_the_next_resync_and_later_resyncs_refresh()
    {
        var h = new Harness();
        var calls = 0;
        h.Api.GetChatFoldersAsync(Arg.Any<CancellationToken>()).Returns(_ => ++calls switch
        {
            1 => Task.FromException<ChatFoldersDto>(new ApiException(0, ClientErrorCodes.Network, "offline")),
            2 => Task.FromResult(State(3, Folder("Family"))),
            _ => Task.FromResult(State(4, Folder("Семья"))),
        });
        await h.Lifecycle.ActivateAsync(h.Me);
        var changes = 0;
        h.Store.Changed += () => changes++;

        // Sign-in's load fails (offline, a server restarting): nothing to show yet, and nothing else asks for folders.
        await h.Store.LoadAsync();
        h.Store.IsLoaded.ShouldBeFalse();

        // Realtime is back: the store asks again and publishes what came.
        h.Resync();
        await Wait.UntilAsync(() => h.Store.Folders.Count == 1, "the folders arrive after the resync");
        h.Store.Folders.Single().Name.ShouldBe("Family");
        h.Store.State.Revision.ShouldBe(3);
        changes.ShouldBeGreaterThan(0);

        // Once loaded, a resync refreshes (changes made elsewhere while disconnected).
        h.Resync();
        await Wait.UntilAsync(() => h.Store.State.Revision == 4, "a resync refreshes the folders");
        h.Store.Folders.Single().Name.ShouldBe("Семья");
        await h.Api.Received(3).GetChatFoldersAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_settings_change_sends_and_shows_only_that_setting()
    {
        var h = new Harness();
        await h.LoadAsync(State(1));
        var answer = new TaskCompletionSource<ChatFoldersDto>();
        h.Api.UpdateChatListSettingsAsync(Arg.Any<UpdateChatListSettingsRequest>(), Arg.Any<CancellationToken>()).Returns(answer.Task);

        var updating = h.Store.UpdateSettingsAsync(new UpdateChatListSettingsRequest { HideArchive = true });
        // Another device switched another setting meanwhile: both show.
        h.Raise(State(2) with { Settings = new ChatListSettingsDto { KeepArchived = false } });
        h.Store.Settings.ShouldBe(new ChatListSettingsDto { KeepArchived = false, HideArchive = true });

        answer.SetResult(State(3) with { Settings = new ChatListSettingsDto { KeepArchived = false, HideArchive = true } });
        await updating;
        h.Store.Settings.ShouldBe(new ChatListSettingsDto { KeepArchived = false, HideArchive = true });
        await h.Api.Received(1).UpdateChatListSettingsAsync(
            Arg.Is<UpdateChatListSettingsRequest>(r => r.HideArchive == true && r.KeepArchived == null && r.ArchiveNewFromStrangers == null),
            Arg.Any<CancellationToken>());
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

        // Saving keeps the pins (they have their own calls) and a new folder goes last; an edit of a folder that is gone
        // (deleted on another device) does not bring it back.
        var saved = ChatFolderEdits.Save(pinned, a with { Name = "A2", PinnedChatIds = [] }, create: false);
        saved.Folders[0].ShouldSatisfyAllConditions(f => f.Name.ShouldBe("A2"), f => f.PinnedChatIds.ShouldBe([y, x]));
        ChatFolderEdits.Save(state, Folder("D"), create: true).Folders.Select(f => f.Name).ShouldBe(["A", "B", "C", "D"]);
        ChatFolderEdits.Save(state, Folder("D"), create: false).ShouldBeSameAs(state);

        var settings = state with { Settings = new ChatListSettingsDto { ArchiveNewFromStrangers = true } };
        ChatFolderEdits.UpdateSettings(settings, new UpdateChatListSettingsRequest { KeepArchived = false }).Settings
            .ShouldBe(new ChatListSettingsDto { ArchiveNewFromStrangers = true, KeepArchived = false });
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
    public void Badges_count_unread_messages_like_the_dock_and_fall_back_to_the_muted_ones()
    {
        var loud = Chat("loud", 1, unread: 4);
        var mentioned = Chat("mentioned", 2) with { UnreadMentionCount = 1 };
        var quiet = Chat("quiet", 3, unread: 9, muted: true);
        var read = Chat("read", 4);

        ChatFolderView.BadgeOf([loud, mentioned, quiet, read], Now).ShouldBe(new FolderBadge(5, false));
        ChatFolderView.BadgeOf([quiet, read], Now).ShouldBe(new FolderBadge(9, true));
        ChatFolderView.BadgeOf([read], Now).ShouldBe(new FolderBadge(0, false));
    }

    [Fact]
    public void All_badges_in_one_pass_match_each_folder_on_its_own()
    {
        var friend = Chat("friend", 1, ChatType.Private, unread: 2);
        var stranger = Chat("stranger", 2, ChatType.Private, unread: 1);
        var group = Chat("group", 3, unread: 5);
        var quietGroup = Chat("quiet group", 4, unread: 7, muted: true);
        var archived = Chat("archived", 5, unread: 3, archived: true);
        var chats = new[] { friend, stranger, group, quietGroup, archived, Chat("read", 6) };
        bool IsContact(ChatSummaryDto c) => c.Id == friend.Id;
        var folders = new[]
        {
            new ChatFolderDto { Id = Guid.CreateVersion7(), IncludeContacts = true },
            new ChatFolderDto { Id = Guid.CreateVersion7(), IncludeNonContacts = true, IncludeGroups = true, ExcludeArchived = true },
            new ChatFolderDto { Id = Guid.CreateVersion7(), IncludeGroups = true, ExcludeMuted = true, ExcludeArchived = true },
            new ChatFolderDto { Id = Guid.CreateVersion7(), IncludedChatIds = [quietGroup.Id] },
        };

        var badges = ChatFolderView.BadgesOf(folders, chats, IsContact, Now);

        badges.ShouldBe([new FolderBadge(2, false), new FolderBadge(6, false), new FolderBadge(5, false), new FolderBadge(7, true)]);
        for (var i = 0; i < folders.Length; i++)
            badges[i].ShouldBe(ChatFolderView.BadgeOf(ChatFolderView.ChatsOf(folders[i], chats, IsContact, Now), Now));
        ChatFolderView.BadgesOf([], chats, IsContact, Now).ShouldBeEmpty();
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
