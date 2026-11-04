using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>
/// Chat folders (created by id, edited field by field, ordered, synced to my devices, their own pins, marking a folder
/// read) and where new and archived chats land (new chats from strangers start in the archive, muted; a new message or
/// a call from the other side brings an archived chat back when the user does not keep archived chats archived — except
/// muted chats and chats from strangers).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ChatFolderTests(ApiFactory factory)
{
    /// <summary>A new folder with every field set, as the apps send it.</summary>
    private static SaveChatFolderRequest Folder(string name, bool contacts = false, bool strangers = false, bool groups = false,
        IReadOnlyList<Guid>? include = null, IReadOnlyList<Guid>? exclude = null) => new()
    {
        Create = true,
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

    private static async Task<ChatFoldersDto> SettingsAsync(TestUser user, UpdateChatListSettingsRequest change) =>
        await (await user.PutRawAsync(ApiRoutes.Folders.Settings, change)).ReadAsync<ChatFoldersDto>();

    private async Task<MessageDto> PostCallLogAsync(Guid chatId, Guid initiatorId, CallStatus status = CallStatus.Missed)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISystemMessageService>().PostCallLogAsync(chatId,
            new CallInfoDto { CallId = Guid.NewGuid(), Status = status, InitiatorId = initiatorId }, TestKit.Ct);
    }

    private static void ShouldBeMutedForever(ChatSummaryDto chat) =>
        chat.MutedUntil.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddYears(100), "archived on arrival means muted too");

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
        var saved = await SaveAsync(alice, id, Folder("Mine too", include: [chat, notMine], exclude: [group]) with { Create = false });

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
        // A name must show something; one emoji is one emoji (a word, a row of them or a bidi override is not).
        foreach (var name in new[] { "\u200B", " \u200B\u2060 ", "a\u0007b" })
            await (await TrySaveAsync(alice, Guid.CreateVersion7(), Folder(name, groups: true))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        foreach (var icon in new[] { "Привет", string.Concat(Enumerable.Repeat("🏠", 16)), "\u202E😀", "\u202E" })
        {
            await (await TrySaveAsync(alice, Guid.CreateVersion7(), Folder("Icon", groups: true) with { Icon = icon }))
                .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        }
        var keycap = Guid.CreateVersion7();
        var lines = await SaveAsync(alice, keycap, Folder("Two\nlines", groups: true) with { Icon = "1️⃣" });
        lines.Folders.Single().ShouldSatisfyAllConditions(f => f.Name.ShouldBe("Two lines"), f => f.Icon.ShouldBe("1️⃣"));
        await alice.DeleteRawAsync(ApiRoutes.Folders.ByIdPath(keycap));
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

        var state = await SettingsAsync(alice, new UpdateChatListSettingsRequest { ArchiveNewFromStrangers = true, KeepArchived = false, HideArchive = true });
        state.Settings.ShouldBe(settings);
        (await tabletRt.WaitAsync<ChatFoldersDto>(Ev.ChatFoldersChanged)).Settings.ShouldBe(settings);
        (await StateAsync(tablet)).Settings.ShouldBe(settings);

        (await SettingsAsync(alice, new UpdateChatListSettingsRequest { HideArchive = true })).Revision.ShouldBe(state.Revision);
        (await SettingsAsync(alice, new UpdateChatListSettingsRequest())).Revision.ShouldBe(state.Revision);
    }

    [Fact]
    public async Task Two_devices_switching_different_settings_at_once_both_keep_their_change()
    {
        var alice = await factory.NewUserAsync("Alice");
        var tablet = await alice.SecondDeviceAsync();

        await Task.WhenAll(
            SettingsAsync(alice, new UpdateChatListSettingsRequest { HideArchive = true }),
            SettingsAsync(tablet, new UpdateChatListSettingsRequest { KeepArchived = false }),
            SettingsAsync(tablet, new UpdateChatListSettingsRequest { ArchiveNewFromStrangers = true }));

        (await StateAsync(alice)).ShouldSatisfyAllConditions(
            s => s.Settings.ShouldBe(new ChatListSettingsDto { ArchiveNewFromStrangers = true, KeepArchived = false, HideArchive = true }),
            s => s.Revision.ShouldBe(3));
    }

    [Fact]
    public async Task An_edit_changes_only_what_it_sets_and_never_brings_back_a_folder_deleted_elsewhere()
    {
        var alice = await factory.NewUserAsync("Alice");
        var tablet = await alice.SecondDeviceAsync();
        var id = Guid.CreateVersion7();
        await SaveAsync(alice, id, Folder("Семья", contacts: true, groups: true) with { Icon = "🏠", ExcludeMuted = true });

        // Only the name (an app that knows fewer fields than the server leaves the others alone).
        var renamed = await SaveAsync(alice, id, new SaveChatFolderRequest { Name = "Родные" });
        renamed.Folders.Single().ShouldSatisfyAllConditions(
            f => f.Name.ShouldBe("Родные"),
            f => f.Icon.ShouldBe("🏠"),
            f => f.IncludeContacts.ShouldBeTrue(),
            f => f.IncludeGroups.ShouldBeTrue(),
            f => f.ExcludeMuted.ShouldBeTrue());
        (await SaveAsync(alice, id, new SaveChatFolderRequest { Icon = "" })).Folders.Single().Icon.ShouldBeNull();
        await (await TrySaveAsync(alice, id, new SaveChatFolderRequest { IncludeContacts = false, IncludeGroups = false }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Deleted on the tablet: the phone's edit is a 404 and the folder stays deleted.
        var deleted = await (await tablet.DeleteRawAsync(ApiRoutes.Folders.ByIdPath(id))).ReadAsync<ChatFoldersDto>();
        await (await TrySaveAsync(alice, id, Folder("Семья", contacts: true) with { Create = false }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.FolderNotFound);
        (await StateAsync(alice)).ShouldSatisfyAllConditions(s => s.Folders.ShouldBeEmpty(), s => s.Revision.ShouldBe(deleted.Revision));

        // Made again on purpose, it is a new folder.
        (await SaveAsync(alice, id, Folder("Again", groups: true))).Folders.Single().Name.ShouldBe("Again");
    }

    [Fact]
    public async Task Parallel_creates_stop_exactly_at_the_limit()
    {
        var alice = await factory.NewUserAsync("Alice");

        var responses = await Task.WhenAll(Enumerable.Range(0, Limits.MaxChatFolders + 10)
            .Select(i => TrySaveAsync(alice, Guid.CreateVersion7(), Folder($"F{i}", groups: true))));

        responses.Count(r => r.IsSuccessStatusCode).ShouldBe(Limits.MaxChatFolders);
        foreach (var refused in responses.Where(r => !r.IsSuccessStatusCode))
            await refused.ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.FolderLimit);
        var state = await StateAsync(alice);
        state.Folders.Count.ShouldBe(Limits.MaxChatFolders);
        state.Revision.ShouldBe(Limits.MaxChatFolders);
        var positions = await factory.WithDbAsync(db => db.ChatFolders.Where(f => f.UserId == alice.Id).OrderBy(f => f.Position)
            .Select(f => f.Position).ToListAsync(TestKit.Ct));
        positions.ShouldBe(Enumerable.Range(0, Limits.MaxChatFolders).ToList(), "one after another, each in its own place");
    }

    [Fact]
    public async Task Chats_that_are_no_longer_mine_leave_the_folders_lists_and_pins()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var kept = await alice.NewGroupAsync(bob);
        var left = await bob.NewGroupAsync(alice);
        var leftToo = await bob.NewGroupAsync(alice);
        var other = await alice.NewGroupAsync(bob);
        var id = Guid.CreateVersion7();
        await SaveAsync(alice, id, Folder("Groups", groups: true, include: [left], exclude: [leftToo]));
        await PinAsync(alice, id, kept);
        await PinAsync(alice, id, left);
        await alice.LeaveAsync(left);
        await alice.LeaveAsync(leftToo);

        // The next pin does not count the chat that is gone (it takes up no room).
        (await PinAsync(alice, id, other)).Folders.Single().PinnedChatIds.ShouldBe([other, kept]);
        // A save drops them from the lists too, even when it does not touch the lists.
        var saved = (await SaveAsync(alice, id, new SaveChatFolderRequest { Name = "Группы" })).Folders.Single();
        saved.IncludedChatIds.ShouldBeEmpty();
        saved.ExcludedChatIds.ShouldBeEmpty();
        saved.PinnedChatIds.ShouldBe([other, kept]);
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

        await SettingsAsync(alice, new UpdateChatListSettingsRequest { ArchiveNewFromStrangers = true });
        var stranger2 = await factory.NewUserAsync("Stranger 2");
        var fromStranger = (await stranger2.OpenPrivateAsync(alice.Id)).Id;
        await using var aliceRt = await alice.ConnectAsync();
        await stranger2.SendAsync(fromStranger, "hello there");
        await aliceRt.WaitAsync<ChatRefEvent>(Ev.ChatAdded, e => e.ChatId == fromStranger);
        var arrived = await alice.ChatAsync(fromStranger);
        arrived.IsArchived.ShouldBeTrue();
        // Muted too: a stranger's messages neither show nor ring.
        ShouldBeMutedForever(arrived);
        // The sender's own chat is where they put it.
        (await stranger2.ChatAsync(fromStranger)).ShouldSatisfyAllConditions(c => c.IsArchived.ShouldBeFalse(), c => c.MutedUntil.ShouldBeNull());

        var fromFriend = (await friend.OpenPrivateAsync(alice.Id)).Id;
        await friend.SendAsync(fromFriend, "hi Alice");
        (await alice.ChatAsync(fromFriend)).ShouldSatisfyAllConditions(c => c.IsArchived.ShouldBeFalse(), c => c.MutedUntil.ShouldBeNull());

        // A chat I deleted comes back like a new one.
        await alice.DeleteChatAsync(before, forEveryone: false);
        await stranger.SendAsync(before, "are you there?");
        var back = await alice.ChatAsync(before);
        back.IsArchived.ShouldBeTrue();
        ShouldBeMutedForever(back);

        // A forward is a message too.
        var forwarder = await factory.NewUserAsync("Forwarder");
        var note = await forwarder.SendAsync(forwarder.SavedChatId, "look at this");
        var forwarded = (await forwarder.OpenPrivateAsync(alice.Id)).Id;
        await forwarder.ForwardAsync(forwarded,
            new ForwardMessagesRequest { FromChatId = forwarder.SavedChatId, MessageIds = [note.Id], ClientMessageIds = [Guid.NewGuid()] });
        var viaForward = await alice.ChatAsync(forwarded);
        viaForward.IsArchived.ShouldBeTrue();
        ShouldBeMutedForever(viaForward);

        // Groups: added by a stranger → archive (muted); by a contact → the list.
        var strangersGroup = await stranger.NewGroupAsync(alice);
        var group = await alice.ChatAsync(strangersGroup);
        group.IsArchived.ShouldBeTrue();
        ShouldBeMutedForever(group);
        (await stranger.ChatAsync(strangersGroup)).MutedUntil.ShouldBeNull();
        var friendsGroup = await friend.NewGroupAsync();
        await friend.AddMembersAsync(friendsGroup, alice.Id);
        (await alice.ChatAsync(friendsGroup)).ShouldSatisfyAllConditions(c => c.IsArchived.ShouldBeFalse(), c => c.MutedUntil.ShouldBeNull());
        var addedByStranger = await stranger.NewGroupAsync();
        await stranger.AddMembersAsync(addedByStranger, alice.Id);
        var added = await alice.ChatAsync(addedByStranger);
        added.IsArchived.ShouldBeTrue();
        ShouldBeMutedForever(added);
    }

    [Fact]
    public async Task A_service_message_that_brings_back_a_strangers_chat_also_starts_it_in_the_archive()
    {
        var alice = await factory.NewUserAsync("Alice");
        var stranger = await factory.NewUserAsync("Stranger");
        await SettingsAsync(alice, new UpdateChatListSettingsRequest { ArchiveNewFromStrangers = true });
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
        await SettingsAsync(alice, new UpdateChatListSettingsRequest { KeepArchived = false, ArchiveNewFromStrangers = true });
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

        // A stranger's new chat arrives archived and muted, so it stays; unmuted, it stays only while strangers' chats go to
        // the archive.
        var other = await factory.NewUserAsync("Other");
        var withOther = (await other.OpenPrivateAsync(alice.Id)).Id;
        await other.SendAsync(withOther, "hi");
        ShouldBeMutedForever(await alice.ChatAsync(withOther));
        await other.SendAsync(withOther, "anyone?");
        (await alice.ChatAsync(withOther)).IsArchived.ShouldBeTrue();
        await alice.UpdateSettingsAsync(withOther, new UpdateChatSettingsRequest { MuteForSeconds = 0 });
        await other.SendAsync(withOther, "still here");
        (await alice.ChatAsync(withOther)).IsArchived.ShouldBeTrue();
        await SettingsAsync(alice, new UpdateChatListSettingsRequest { ArchiveNewFromStrangers = false });
        await other.SendAsync(withOther, "hello again");
        (await alice.ChatAsync(withOther)).IsArchived.ShouldBeFalse();
    }

    [Fact]
    public async Task A_call_from_the_other_side_brings_an_archived_chat_back_but_a_notice_does_not()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var stranger = await factory.NewUserAsync("Stranger");
        await alice.AddContactAsync(bob.Id);
        var withBob = (await bob.OpenPrivateAsync(alice.Id)).Id;
        await bob.SendAsync(withBob, "hi");
        await alice.UpdateSettingsAsync(withBob, new UpdateChatSettingsRequest { IsArchived = true });
        await SettingsAsync(alice, new UpdateChatListSettingsRequest { KeepArchived = false, ArchiveNewFromStrangers = true });
        await using var aliceRt = await alice.ConnectAsync();

        // Alice's own call leaves her chat where it is; Bob's missed call brings it back.
        await PostCallLogAsync(withBob, alice.Id, CallStatus.Ended);
        (await alice.ChatAsync(withBob)).IsArchived.ShouldBeTrue();
        await PostCallLogAsync(withBob, bob.Id);
        (await aliceRt.WaitAsync<ChatSettingsChangedEvent>(Ev.ChatSettingsChanged, e => e.ChatId == withBob && !e.Settings.IsArchived))
            .Settings.MutedUntil.ShouldBeNull();
        (await alice.ChatAsync(withBob)).IsArchived.ShouldBeFalse();

        // A notice never does.
        await alice.UpdateSettingsAsync(withBob, new UpdateChatSettingsRequest { IsArchived = true });
        await bob.SetContentProtectionAsync(withBob, true);
        (await alice.ChatAsync(withBob)).IsArchived.ShouldBeTrue();

        // A muted chat stays, and so does a stranger's (unmuted) while strangers' chats go to the archive.
        await alice.UpdateSettingsAsync(withBob, new UpdateChatSettingsRequest { MuteForSeconds = -1 });
        await PostCallLogAsync(withBob, bob.Id);
        (await alice.ChatAsync(withBob)).IsArchived.ShouldBeTrue();
        var withStranger = (await stranger.OpenPrivateAsync(alice.Id)).Id;
        await stranger.SendAsync(withStranger, "hello");
        await alice.UpdateSettingsAsync(withStranger, new UpdateChatSettingsRequest { MuteForSeconds = 0 });
        await PostCallLogAsync(withStranger, stranger.Id);
        (await alice.ChatAsync(withStranger)).IsArchived.ShouldBeTrue();
    }

    [Fact]
    public async Task A_mute_that_ran_out_lets_a_new_message_bring_the_chat_back()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        await alice.AddContactAsync(bob.Id);
        var chat = (await bob.OpenPrivateAsync(alice.Id)).Id;
        await bob.SendAsync(chat, "first");
        await alice.UpdateSettingsAsync(chat, new UpdateChatSettingsRequest { IsArchived = true, MuteForSeconds = 3600 });
        await SettingsAsync(alice, new UpdateChatListSettingsRequest { KeepArchived = false });

        await bob.SendAsync(chat, "while muted");
        (await alice.ChatAsync(chat)).IsArchived.ShouldBeTrue();

        // The hour is over.
        await factory.WithDbAsync(db => db.ChatMembers.Where(m => m.ChatId == chat && m.UserId == alice.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.MutedUntil, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1)), TestKit.Ct));
        await bob.SendAsync(chat, "and now?");
        (await alice.ChatAsync(chat)).IsArchived.ShouldBeFalse();
    }

    [Fact]
    public async Task A_group_a_contact_added_me_to_comes_back_whoever_writes_in_it()
    {
        var alice = await factory.NewUserAsync("Alice");
        var friend = await factory.NewUserAsync("Friend");
        var stranger = await factory.NewUserAsync("Stranger");
        await alice.AddContactAsync(friend.Id);
        await SettingsAsync(alice, new UpdateChatListSettingsRequest { KeepArchived = false, ArchiveNewFromStrangers = true });
        var group = await friend.NewGroupAsync(stranger);
        await friend.AddMembersAsync(group, alice.Id);
        (await alice.ChatAsync(group)).IsArchived.ShouldBeFalse();
        await alice.UpdateSettingsAsync(group, new UpdateChatSettingsRequest { IsArchived = true });

        // Who added me decides where the group came from, not who writes: a stranger's message brings it back.
        await stranger.SendAsync(group, "hello everyone");
        (await alice.ChatAsync(group)).IsArchived.ShouldBeFalse();

        // A group a stranger added me to starts archived and muted, and stays there.
        var strangersGroup = await stranger.NewGroupAsync(friend, alice);
        await friend.SendAsync(strangersGroup, "hi all");
        var quiet = await alice.ChatAsync(strangersGroup);
        quiet.IsArchived.ShouldBeTrue();
        ShouldBeMutedForever(quiet);
    }
}
