using System.Net;
using System.Text.Json;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>
/// Chat folders (created by id, ordered, synced to my devices, their own pins, marking a folder read) and where new and
/// archived chats land (new chats from strangers start in the archive; a new message brings an archived chat back when
/// the user does not keep archived chats archived — except muted chats and chats from strangers).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ChatFolderTests(ApiFactory factory)
{
    private static SaveChatFolderRequest Folder(string name, bool contacts = false, bool strangers = false, bool groups = false,
        IReadOnlyList<Guid>? include = null, IReadOnlyList<Guid>? exclude = null) => new()
    {
        Name = name,
        IncludeContacts = contacts,
        IncludeNonContacts = strangers,
        IncludeGroups = groups,
        IncludedChatIds = include ?? [],
        ExcludedChatIds = exclude ?? [],
    };

    private static Task<HttpResponseMessage> TrySaveAsync(TestUser user, Guid folderId, SaveChatFolderRequest request) =>
        user.PutRawAsync(ApiRoutes.Folders.ByIdPath(folderId), request);

    private static async Task<ChatFoldersDto> SaveAsync(TestUser user, Guid folderId, SaveChatFolderRequest request) =>
        await (await TrySaveAsync(user, folderId, request)).ReadAsync<ChatFoldersDto>();

    private static Task<ChatFoldersDto> StateAsync(TestUser user) => user.GetAsync<ChatFoldersDto>(ApiRoutes.Folders.Root);

    private static string Json(ChatFoldersDto state) => JsonSerializer.Serialize(state, SkyJson.Options);

    private static async Task<ChatFoldersDto> SettingsAsync(TestUser user, ChatListSettingsDto settings) =>
        await (await user.PutRawAsync(ApiRoutes.Folders.Settings, settings)).ReadAsync<ChatFoldersDto>();

    private static async Task<ChatFoldersDto> PinAsync(TestUser user, Guid folderId, Guid chatId) =>
        await (await user.PutEmptyAsync(ApiRoutes.Folders.PinPath(folderId, chatId))).ReadAsync<ChatFoldersDto>();

    // ---- Folders ----

    [Fact]
    public async Task Folders_are_created_by_id_kept_in_my_order_and_synced_to_my_devices()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var tablet = await alice.SecondDeviceAsync();
        await using var tabletRt = await tablet.ConnectAsync();
        await using var bobRt = await bob.ConnectAsync();

        (await StateAsync(alice)).ShouldSatisfyAllConditions(
            s => s.Revision.ShouldBe(0),
            s => s.Folders.ShouldBeEmpty(),
            s => s.Settings.ShouldBe(new ChatListSettingsDto()));

        var family = Guid.CreateVersion7();
        var created = await SaveAsync(alice, family, Folder("  Семья ", contacts: true) with { Icon = "🏠" });
        created.Revision.ShouldBe(1);
        var folder = created.Folders.ShouldHaveSingleItem();
        folder.ShouldSatisfyAllConditions(
            f => f.Id.ShouldBe(family),
            f => f.Name.ShouldBe("Семья"),
            f => f.Icon.ShouldBe("🏠"),
            f => f.IncludeContacts.ShouldBeTrue(),
            f => f.IncludeGroups.ShouldBeFalse());
        (await tabletRt.WaitAsync<ChatFoldersDto>(Ev.ChatFoldersChanged, s => s.Revision == 1)).Folders.Single().Name.ShouldBe("Семья");

        // A repeat of the same create (a retry after a lost answer) is the same folder and changes nothing.
        var repeated = await SaveAsync(alice, family, Folder("Семья", contacts: true) with { Icon = "🏠" });
        repeated.Revision.ShouldBe(1);
        repeated.Folders.Count.ShouldBe(1);

        var work = Guid.CreateVersion7();
        var groups = Guid.CreateVersion7();
        await SaveAsync(alice, work, Folder("Работа", strangers: true));
        var three = await SaveAsync(alice, groups, Folder("Группы", groups: true));
        three.Folders.Select(f => f.Id).ShouldBe([family, work, groups]);

        // Reordering moves the listed folders into the places they held; unknown ids are ignored.
        var reordered = await (await alice.PutRawAsync(ApiRoutes.Folders.Order,
            new ReorderChatFoldersRequest { FolderIds = [groups, Guid.NewGuid(), family] })).ReadAsync<ChatFoldersDto>();
        reordered.Folders.Select(f => f.Id).ShouldBe([groups, work, family]);
        (await StateAsync(tablet)).Folders.Select(f => f.Id).ShouldBe([groups, work, family]);

        var afterDelete = await (await alice.DeleteRawAsync(ApiRoutes.Folders.ByIdPath(work))).ReadAsync<ChatFoldersDto>();
        afterDelete.Folders.Select(f => f.Id).ShouldBe([groups, family]);
        var last = await tabletRt.WaitAsync<ChatFoldersDto>(Ev.ChatFoldersChanged, s => s.Revision == afterDelete.Revision);
        Json(last).ShouldBe(Json(afterDelete), "the event carries the same state as the answer");
        tabletRt.All<ChatFoldersDto>(Ev.ChatFoldersChanged).Select(s => s.Revision).ShouldBe([1L, 2L, 3L, 4L, 5L]);

        // My folders are mine.
        await bobRt.BarrierAsync();
        bobRt.All<ChatFoldersDto>(Ev.ChatFoldersChanged).ShouldBeEmpty();
        (await StateAsync(bob)).Folders.ShouldBeEmpty();
    }

    [Fact]
    public async Task Saving_replaces_the_rules_keeps_the_pins_and_drops_chats_that_are_not_mine()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var group = await alice.NewGroupAsync(bob);
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var notMine = await bob.NewGroupAsync(carol);
        var id = Guid.CreateVersion7();

        await SaveAsync(alice, id, Folder("Mine", groups: true));
        await PinAsync(alice, id, group);
        var saved = await SaveAsync(alice, id, Folder("Mine too", include: [chat, notMine], exclude: [group]));

        var folder = saved.Folders.Single();
        folder.Name.ShouldBe("Mine too");
        folder.IncludeGroups.ShouldBeFalse();
        folder.IncludedChatIds.ShouldBe([chat]);
        folder.ExcludedChatIds.ShouldBe([group]);
        folder.PinnedChatIds.ShouldBe([group]);
    }

    [Fact]
    public async Task Someone_elses_folder_does_not_exist_for_me()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var group = await bob.NewGroupAsync(alice);
        var id = Guid.CreateVersion7();
        await SaveAsync(alice, id, Folder("Alice's", groups: true));

        await (await TrySaveAsync(bob, id, Folder("Taken", groups: true))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.FolderNotFound);
        await (await bob.DeleteRawAsync(ApiRoutes.Folders.ByIdPath(id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.FolderNotFound);
        await (await bob.PutEmptyAsync(ApiRoutes.Folders.PinPath(id, group))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.FolderNotFound);
        await (await bob.PostEmptyAsync(ApiRoutes.Folders.ReadPath(id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.FolderNotFound);
        (await StateAsync(alice)).Folders.Single().Name.ShouldBe("Alice's");
    }

    [Fact]
    public async Task Folders_are_checked_and_limited()
    {
        var alice = await factory.NewUserAsync("Alice");

        await (await TrySaveAsync(alice, Guid.CreateVersion7(), Folder(" ", groups: true)))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await TrySaveAsync(alice, Guid.CreateVersion7(), Folder("Nothing")))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await TrySaveAsync(alice, Guid.CreateVersion7(), Folder("Icon", groups: true) with { Icon = "not an icon" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.PutRawAsync(ApiRoutes.Folders.Order, new ReorderChatFoldersRequest()))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        for (var i = 0; i < Limits.MaxChatFolders; i++)
            await SaveAsync(alice, Guid.CreateVersion7(), Folder($"F{i}", groups: true));
        await (await TrySaveAsync(alice, Guid.CreateVersion7(), Folder("One too many", groups: true)))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.FolderLimit);
        (await StateAsync(alice)).Folders.Count.ShouldBe(Limits.MaxChatFolders);
    }

    [Fact]
    public async Task A_folder_has_its_own_pins_in_its_own_order()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var a = await alice.NewGroupAsync(bob);
        var b = await alice.NewGroupAsync(bob);
        var c = await alice.NewGroupAsync(bob);
        var bobsOwn = await bob.NewGroupAsync();
        var id = Guid.CreateVersion7();
        await SaveAsync(alice, id, Folder("Groups", groups: true));

        await PinAsync(alice, id, a);
        await PinAsync(alice, id, b);
        (await PinAsync(alice, id, c)).Folders.Single().PinnedChatIds.ShouldBe([c, b, a]);
        var repeat = await PinAsync(alice, id, c);
        repeat.Folders.Single().PinnedChatIds.ShouldBe([c, b, a]);

        var reordered = await (await alice.PutRawAsync(ApiRoutes.Folders.PinnedOrderPath(id), new ReorderPinnedChatsRequest { ChatIds = [a, c] }))
            .ReadAsync<ChatFoldersDto>();
        reordered.Folders.Single().PinnedChatIds.ShouldBe([a, b, c]);

        var unpinned = await (await alice.DeleteRawAsync(ApiRoutes.Folders.PinPath(id, b))).ReadAsync<ChatFoldersDto>();
        unpinned.Folders.Single().PinnedChatIds.ShouldBe([a, c]);

        // Folder pins leave the global pins of "All chats" alone, and only my chats can be pinned.
        (await alice.ChatsAsync()).ShouldAllBe(x => !x.IsPinned);
        await (await alice.PutEmptyAsync(ApiRoutes.Folders.PinPath(id, bobsOwn))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }

    [Fact]
    public async Task Marking_a_folder_read_reads_only_the_chats_its_rules_take_in()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        await alice.AddContactAsync(bob.Id);
        var group = await alice.NewGroupAsync(bob);
        var otherGroup = await alice.NewGroupAsync(bob);
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var archivedGroup = await alice.NewGroupAsync(bob);
        await alice.UpdateSettingsAsync(archivedGroup, new UpdateChatSettingsRequest { IsArchived = true });
        foreach (var target in new[] { group, otherGroup, chat, archivedGroup })
            await bob.SendAsync(target, "hello");
        var id = Guid.CreateVersion7();
        await SaveAsync(alice, id, Folder("Groups", groups: true, exclude: [otherGroup]) with { ExcludeArchived = true });
        await using var bobRt = await bob.ConnectAsync();

        await alice.PostEmptyAsync(ApiRoutes.Folders.ReadPath(id)).NoContentAsync();

        (await alice.ChatAsync(group)).UnreadCount.ShouldBe(0);
        (await alice.ChatAsync(otherGroup)).UnreadCount.ShouldBe(1);
        (await alice.ChatAsync(chat)).UnreadCount.ShouldBe(1);
        (await alice.ChatAsync(archivedGroup)).UnreadCount.ShouldBe(1);
        (await bobRt.WaitAsync<MessagesReadEvent>(Ev.MessagesRead, e => e.ChatId == group && e.UserId == alice.Id)).UpToSeq.ShouldBeGreaterThan(0);

        // "All chats": everything outside the archive.
        await alice.PostEmptyAsync(ApiRoutes.Folders.ReadAll).NoContentAsync();
        (await alice.ChatAsync(otherGroup)).UnreadCount.ShouldBe(0);
        (await alice.ChatAsync(chat)).UnreadCount.ShouldBe(0);
        (await alice.ChatAsync(archivedGroup)).UnreadCount.ShouldBe(1);
    }

    [Fact]
    public async Task Chat_list_settings_are_stored_and_synced()
    {
        var alice = await factory.NewUserAsync("Alice");
        var tablet = await alice.SecondDeviceAsync();
        await using var tabletRt = await tablet.ConnectAsync();
        var settings = new ChatListSettingsDto { ArchiveNewFromStrangers = true, KeepArchived = false, HideArchive = true };

        var state = await SettingsAsync(alice, settings);
        state.Settings.ShouldBe(settings);
        (await tabletRt.WaitAsync<ChatFoldersDto>(Ev.ChatFoldersChanged)).Settings.ShouldBe(settings);
        (await StateAsync(tablet)).Settings.ShouldBe(settings);

        (await SettingsAsync(alice, settings)).Revision.ShouldBe(state.Revision);
    }

    // ---- New chats and the archive ----

    [Fact]
    public async Task New_chats_from_strangers_start_in_the_archive_when_I_ask_for_it()
    {
        var alice = await factory.NewUserAsync("Alice");
        var friend = await factory.NewUserAsync("Friend");
        var stranger = await factory.NewUserAsync("Stranger");
        await alice.AddContactAsync(friend.Id);

        // By default new chats appear in the list, from anyone.
        var before = (await stranger.OpenPrivateAsync(alice.Id)).Id;
        await stranger.SendAsync(before, "hi");
        (await alice.ChatAsync(before)).IsArchived.ShouldBeFalse();

        await SettingsAsync(alice, new ChatListSettingsDto { ArchiveNewFromStrangers = true });
        var stranger2 = await factory.NewUserAsync("Stranger 2");
        var fromStranger = (await stranger2.OpenPrivateAsync(alice.Id)).Id;
        await using var aliceRt = await alice.ConnectAsync();
        await stranger2.SendAsync(fromStranger, "hello there");
        await aliceRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == fromStranger);
        (await alice.ChatAsync(fromStranger)).IsArchived.ShouldBeTrue();
        // The sender's own chat is where they put it.
        (await stranger2.ChatAsync(fromStranger)).IsArchived.ShouldBeFalse();

        var fromFriend = (await friend.OpenPrivateAsync(alice.Id)).Id;
        await friend.SendAsync(fromFriend, "hi Alice");
        (await alice.ChatAsync(fromFriend)).IsArchived.ShouldBeFalse();

        // A chat I deleted comes back like a new one.
        await alice.DeleteChatAsync(before, forEveryone: false);
        await stranger.SendAsync(before, "are you there?");
        (await alice.ChatAsync(before)).IsArchived.ShouldBeTrue();

        // Groups: added by a stranger → archive; by a contact → the list.
        var strangersGroup = await stranger.NewGroupAsync(alice);
        (await alice.ChatAsync(strangersGroup)).IsArchived.ShouldBeTrue();
        var friendsGroup = await friend.NewGroupAsync();
        await friend.AddMembersAsync(friendsGroup, alice.Id);
        (await alice.ChatAsync(friendsGroup)).IsArchived.ShouldBeFalse();
        var addedByStranger = await stranger.NewGroupAsync();
        await stranger.AddMembersAsync(addedByStranger, alice.Id);
        (await alice.ChatAsync(addedByStranger)).IsArchived.ShouldBeTrue();
    }

    [Fact]
    public async Task A_service_message_that_brings_back_a_strangers_chat_also_starts_it_in_the_archive()
    {
        var alice = await factory.NewUserAsync("Alice");
        var stranger = await factory.NewUserAsync("Stranger");
        await SettingsAsync(alice, new ChatListSettingsDto { ArchiveNewFromStrangers = true });
        var chat = (await stranger.OpenPrivateAsync(alice.Id)).Id;

        // Turning protection on posts a notice into the chat, which shows it to Alice for the first time.
        await stranger.SetContentProtectionAsync(chat, true);

        var seen = await alice.ChatAsync(chat);
        seen.IsArchived.ShouldBeTrue();
        (await alice.ChatsAsync()).ShouldContain(c => c.Id == chat);
    }

    [Fact]
    public async Task Archived_chats_stay_archived_by_default()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        await alice.AddContactAsync(bob.Id);
        var chat = (await bob.OpenPrivateAsync(alice.Id)).Id;
        await bob.SendAsync(chat, "first");
        await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { IsArchived = true });

        await bob.SendAsync(chat, "second");

        (await alice.ChatAsync(chat)).IsArchived.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_keep_archived_a_new_message_brings_a_chat_back_but_not_a_muted_one_or_a_strangers()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var stranger = await factory.NewUserAsync("Stranger");
        await alice.AddContactAsync(bob.Id);
        var withBob = (await bob.OpenPrivateAsync(alice.Id)).Id;
        var mutedGroup = await bob.NewGroupAsync(alice);
        var withStranger = (await stranger.OpenPrivateAsync(alice.Id)).Id;
        await bob.SendAsync(withBob, "first");
        await stranger.SendAsync(withStranger, "first");
        foreach (var chat in new[] { withBob, mutedGroup, withStranger })
            await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { IsArchived = true });
        await alice.UpdateSettingsAsync(mutedGroup, new UpdateChatSettingsRequest { MuteForSeconds = -1 });
        await SettingsAsync(alice, new ChatListSettingsDto { KeepArchived = false, ArchiveNewFromStrangers = true });
        await using var aliceRt = await alice.ConnectAsync();

        await bob.SendAsync(withBob, "back to the list");
        var moved = await aliceRt.WaitAsync<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, e => e.ChatId == withBob);
        moved.Settings.IsArchived.ShouldBeFalse();
        (await alice.ChatAsync(withBob)).IsArchived.ShouldBeFalse();

        await bob.SendAsync(mutedGroup, "quiet please");
        await stranger.SendAsync(withStranger, "still a stranger");
        (await alice.ChatAsync(mutedGroup)).IsArchived.ShouldBeTrue();
        (await alice.ChatAsync(withStranger)).IsArchived.ShouldBeTrue();

        // My own message does not move my chat, and once the stranger is a contact their messages bring it back.
        await alice.SendAsync(withStranger, "who is this?");
        (await alice.ChatAsync(withStranger)).IsArchived.ShouldBeTrue();
        await alice.AddContactAsync(stranger.Id);
        await stranger.SendAsync(withStranger, "an old friend");
        (await alice.ChatAsync(withStranger)).IsArchived.ShouldBeFalse();

        // Strangers stay put only while their chats go to the archive.
        var other = await factory.NewUserAsync("Other");
        var withOther = (await other.OpenPrivateAsync(alice.Id)).Id;
        await other.SendAsync(withOther, "hi");
        (await alice.ChatAsync(withOther)).IsArchived.ShouldBeTrue();
        await SettingsAsync(alice, new ChatListSettingsDto { KeepArchived = false, ArchiveNewFromStrangers = false });
        await other.SendAsync(withOther, "hello again");
        (await alice.ChatAsync(withOther)).IsArchived.ShouldBeFalse();
    }
}
