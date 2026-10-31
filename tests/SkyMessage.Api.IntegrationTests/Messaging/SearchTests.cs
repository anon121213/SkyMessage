using System.Net;
using Shouldly;
using SkyMessage.Application.Messaging.Services;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>In-chat search, global search and the shared media gallery (SPEC §6.3).</summary>
[Collection(ApiCollection.Name)]
public sealed class SearchTests(ApiFactory factory)
{
    private static string Token() => "q" + Guid.NewGuid().ToString("N")[..10];

    private static Task<MessagesPageDto> SearchInChatAsync(TestUser user, Guid chatId, string query, long? beforeSeq = null, int limit = Limits.SearchResultsMax) =>
        user.GetAsync<MessagesPageDto>(ApiRoutes.Messages.SearchPath(chatId, query, beforeSeq, limit));

    [Fact]
    public async Task In_chat_search_is_case_insensitive_newest_first_and_pages_backwards()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var t = Token();
        var a = await bob.SendAsync(chat, $"Hello {t} world");
        await bob.SendAsync(chat, "unrelated");
        var b = await alice.SendAsync(chat, $"{t.ToUpperInvariant()} again");
        var c = await bob.SendAsync(chat, $"last {t}");

        var hits = await SearchInChatAsync(alice, chat, t);
        hits.Messages.Select(m => m.Id).ShouldBe([c.Id, b.Id, a.Id]);
        hits.HasMoreBefore.ShouldBeFalse();
        hits.HasMoreAfter.ShouldBeFalse();
        hits.Users.Select(u => u.Id).ShouldBe([alice.Id, bob.Id], ignoreOrder: true);
        hits.SyncCursor.ShouldNotBeNullOrEmpty();

        var first = await SearchInChatAsync(alice, chat, t, limit: 2);
        first.Messages.Select(m => m.Id).ShouldBe([c.Id, b.Id]);
        first.HasMoreBefore.ShouldBeTrue();
        var rest = await SearchInChatAsync(alice, chat, t, beforeSeq: first.Messages[^1].Seq, limit: 2);
        rest.Messages.Select(m => m.Id).ShouldBe([a.Id]);
        rest.HasMoreBefore.ShouldBeFalse();

        // Surrounding whitespace is ignored; too short or blank queries are rejected.
        (await SearchInChatAsync(alice, chat, $"  {t}  ")).Messages.Count.ShouldBe(3);
        await (await alice.GetRawAsync(ApiRoutes.Messages.SearchPath(chat, "x"))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.GetRawAsync(ApiRoutes.Messages.SearchPath(chat, "   "))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.GetRawAsync($"api/chats/{chat}/messages/search")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var outsider = await factory.NewUserAsync("Outsider");
        await (await outsider.GetRawAsync(ApiRoutes.Messages.SearchPath(chat, t))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }

    [Fact]
    public async Task Like_wildcards_in_the_query_are_literal()
    {
        var alice = await factory.NewUserAsync("Alice");
        var chat = alice.SavedChatId;
        var t = Token();
        var percent = await alice.SendAsync(chat, $"{t} 50% off");
        await alice.SendAsync(chat, $"{t} 500 pieces");
        var underscore = await alice.SendAsync(chat, $"{t} a_b");
        await alice.SendAsync(chat, $"{t} axb");
        var backslash = await alice.SendAsync(chat, $"{t} C:\\temp");
        await alice.SendAsync(chat, $"{t} C:temp");

        (await SearchInChatAsync(alice, chat, "50%")).Messages.Select(m => m.Id).ShouldBe([percent.Id]);
        (await SearchInChatAsync(alice, chat, "a_b")).Messages.Select(m => m.Id).ShouldBe([underscore.Id]);
        (await SearchInChatAsync(alice, chat, ":\\t")).Messages.Select(m => m.Id).ShouldBe([backslash.Id]);
        (await SearchInChatAsync(alice, chat, "%%")).Messages.ShouldBeEmpty();
        (await SearchInChatAsync(alice, chat, "__")).Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_folds_case_beyond_ascii()
    {
        var t = Token();
        var alice = await factory.NewUserAsync("Алиса");
        var boris = await factory.NewUserAsync($"Борис {t}");
        var chat = (await alice.OpenPrivateAsync(boris.Id)).Id;
        var hello = await boris.SendAsync(chat, $"Привет, как дела? {t}");
        var group = (await alice.CreateGroupAsync($"Семья {t}", boris)).Chat.Id;

        (await SearchInChatAsync(alice, chat, "привет")).Messages.Select(m => m.Id).ShouldBe([hello.Id]);
        (await SearchInChatAsync(alice, chat, "ПРИВЕТ")).Messages.Select(m => m.Id).ShouldBe([hello.Id]);
        (await alice.GetAsync<IReadOnlyList<MessageSearchHitDto>>(ApiRoutes.Search.MessagesPath("КАК ДЕЛА"))).Select(h => h.Message.Id).ShouldBe([hello.Id]);

        var result = await alice.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath($"семья {t}"));
        result.Chats.Select(c => c.Id).ShouldBe([group]);
        var people = await alice.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath($"БОРИС {t}"));
        people.Users.Select(u => u.Id).ShouldBe([boris.Id]);
        people.Chats.Select(c => c.Id).ShouldBe([chat]);
    }

    [Fact]
    public async Task Search_skips_deleted_hidden_and_cleared_messages()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var t = Token();
        var old = await bob.SendAsync(chat, $"old {t}");
        await alice.ClearHistoryAsync(chat);
        var deleted = await bob.SendAsync(chat, $"deleted {t}");
        var hidden = await bob.SendAsync(chat, $"hidden {t}");
        var visible = await bob.SendAsync(chat, $"visible {t}");
        await bob.DeleteMessagesAsync(chat, forEveryone: true, deleted.Id);
        await alice.DeleteMessagesAsync(chat, forEveryone: false, hidden.Id);

        (await SearchInChatAsync(alice, chat, t)).Messages.Select(m => m.Id).ShouldBe([visible.Id]);
        (await SearchInChatAsync(bob, chat, t)).Messages.Select(m => m.Id).ShouldBe([visible.Id, hidden.Id, old.Id]);
        (await alice.GetAsync<IReadOnlyList<MessageSearchHitDto>>(ApiRoutes.Search.MessagesPath(t))).Select(h => h.Message.Id).ShouldBe([visible.Id]);
        (await bob.GetAsync<IReadOnlyList<MessageSearchHitDto>>(ApiRoutes.Search.MessagesPath(t))).Select(h => h.Message.Id).ShouldBe([visible.Id, hidden.Id, old.Id]);
    }

    [Fact]
    public async Task Global_message_search_covers_only_my_chats_and_names_them()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var dave = await factory.NewUserAsync("Dave");
        var t = Token();
        var privateChat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var group = (await alice.CreateGroupAsync("Book club", carol)).Chat.Id;
        var inPrivate = await bob.SendAsync(privateChat, $"private {t}");
        var inGroup = await carol.SendAsync(group, $"group {t}");
        var inSaved = await alice.SendAsync(alice.SavedChatId, $"saved {t}");
        var notMine = (await carol.OpenPrivateAsync(dave.Id)).Id;
        await dave.SendAsync(notMine, $"secret {t}");

        var hits = await alice.GetAsync<IReadOnlyList<MessageSearchHitDto>>(ApiRoutes.Search.MessagesPath(t));
        hits.Select(h => h.Message.Id).ShouldBe([inSaved.Id, inGroup.Id, inPrivate.Id]); // newest first
        var byChat = hits.ToDictionary(h => h.ChatId);
        byChat[privateChat].ChatType.ShouldBe(ChatType.Private);
        byChat[privateChat].ChatTitle.ShouldBe("Bob");
        byChat[group].ChatType.ShouldBe(ChatType.Group);
        byChat[group].ChatTitle.ShouldBe("Book club");
        byChat[alice.SavedChatId].ChatType.ShouldBe(ChatType.Saved);
        byChat[alice.SavedChatId].ChatTitle.ShouldBe(ChatSummaryBuilder.SavedMessagesTitle);

        (await alice.GetAsync<IReadOnlyList<MessageSearchHitDto>>(ApiRoutes.Search.MessagesPath(t, limit: 2))).Count.ShouldBe(2);
        await (await alice.GetRawAsync(ApiRoutes.Search.MessagesPath("a"))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Leaving a group takes its messages out of my results.
        await carol.LeaveAsync(group);
        (await carol.GetAsync<IReadOnlyList<MessageSearchHitDto>>(ApiRoutes.Search.MessagesPath(t))).Select(h => h.ChatId).ShouldBe([notMine]);
    }

    [Fact]
    public async Task Global_search_finds_chats_people_and_messages()
    {
        var t = Token();
        var alice = await factory.NewUserAsync("Alice");
        var friend = await factory.NewUserAsync($"Friend {t}");
        var stranger = await factory.NewUserAsync($"Stranger {t}");
        var group = (await alice.CreateGroupAsync($"Hiking {t}")).Chat.Id;
        var privateChat = (await alice.OpenPrivateAsync(friend.Id)).Id;
        var message = await alice.SendAsync(alice.SavedChatId, $"note {t}");

        var result = await alice.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(t));
        result.Chats.Select(c => c.Id).ShouldBe([privateChat, group], ignoreOrder: true);
        result.Messages.Select(m => m.Message.Id).ShouldBe([message.Id]);
        // People: related users match by display name; strangers never do (no enumeration by name).
        result.Users.Select(u => u.Id).ShouldBe([friend.Id]);

        // Strangers are found by a long enough username prefix only.
        var byUsername = await alice.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(stranger.Username[..12]));
        byUsername.Users.Select(u => u.Id).ShouldBe([stranger.Id]);
        (await alice.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath("@" + stranger.Username))).Users.Select(u => u.Id).ShouldBe([stranger.Id]);
        (await alice.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(alice.Username))).Users.ShouldBeEmpty(); // never myself

        // Users who blocked me are invisible.
        await stranger.BlockAsync(alice.Id);
        (await alice.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(stranger.Username))).Users.ShouldBeEmpty();
        await (await alice.GetRawAsync(ApiRoutes.Search.GlobalPath("z"))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Global_search_ignores_chats_hidden_from_my_list()
    {
        var t = Token();
        var alice = await factory.NewUserAsync("Alice");
        var peer = await factory.NewUserAsync($"Peer {t}");
        // Peer opened a chat with Alice but never wrote: it is not in Alice's list, so search must not show it.
        var chat = (await peer.OpenPrivateAsync(alice.Id)).Id;

        (await alice.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(t))).Chats.ShouldBeEmpty();
        (await peer.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(alice.Username[..12]))).Chats.Select(c => c.Id).ShouldBe([chat]);

        await peer.SendAsync(chat, "hello");
        (await alice.GetAsync<GlobalSearchResultDto>(ApiRoutes.Search.GlobalPath(t))).Chats.Select(c => c.Id).ShouldBe([chat]);
    }

    [Fact]
    public async Task Message_search_is_rate_limited_per_user()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var t = Token();
        for (var i = 0; i < 60; i++)
        {
            await (await alice.GetRawAsync(ApiRoutes.Messages.SearchPath(alice.SavedChatId, t))).ShouldSucceedAsync();
            await (await alice.GetRawAsync(ApiRoutes.Search.GlobalPath(t))).ShouldSucceedAsync();
        }

        await (await alice.GetRawAsync(ApiRoutes.Search.MessagesPath(t))).ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        // Other people and other endpoints are unaffected.
        await (await bob.GetRawAsync(ApiRoutes.Search.MessagesPath(t))).ShouldSucceedAsync();
        await (await alice.GetRawAsync(ApiRoutes.Messages.PagePath(alice.SavedChatId))).ShouldSucceedAsync();
    }

    [Fact]
    public async Task Shared_media_lists_each_kind_newest_first()
    {
        var alice = await factory.NewUserAsync("Alice");
        var chat = alice.SavedChatId;
        Task<Guid> Media(MediaKind kind) => factory.CreateMediaAsync(alice, kind);

        var photo = await alice.SendMediaAsync(chat, await Media(MediaKind.Photo));
        var video = await alice.SendMediaAsync(chat, await Media(MediaKind.Video));
        var album = await alice.SendMediaAsync(chat, await Media(MediaKind.Photo), await Media(MediaKind.Video));
        var file = await alice.SendMediaAsync(chat, await Media(MediaKind.File));
        var audio = await alice.SendMediaAsync(chat, await Media(MediaKind.Audio));
        var voice = await alice.SendMediaAsync(chat, await Media(MediaKind.Voice));
        var videoNote = await alice.SendMediaAsync(chat, await Media(MediaKind.VideoNote));
        var url = await alice.SendAsync(chat, "see example.com", entities: [new MessageEntityDto { Type = MessageEntityType.Url, Offset = 4, Length = 11 }]);
        var textLink = await alice.SendAsync(chat, "docs here", entities:
            [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 4, Url = "https://example.com/docs" }]);
        await alice.SendAsync(chat, "plain text", entities: [new MessageEntityDto { Type = MessageEntityType.Bold, Offset = 0, Length = 5 }]);
        var deletedPhoto = await alice.SendMediaAsync(chat, await Media(MediaKind.Photo));
        await alice.DeleteMessagesAsync(chat, forEveryone: true, deletedPhoto.Id);

        async Task<List<Guid>> Gallery(SharedMediaFilter filter) =>
            (await alice.GetAsync<MessagesPageDto>(ApiRoutes.Messages.SharedMediaPath(chat, filter))).Messages.Select(m => m.Id).ToList();

        (await Gallery(SharedMediaFilter.Media)).ShouldBe([album.Id, video.Id, photo.Id]);
        (await Gallery(SharedMediaFilter.Files)).ShouldBe([audio.Id, file.Id]);
        (await Gallery(SharedMediaFilter.Voice)).ShouldBe([voice.Id]);
        (await Gallery(SharedMediaFilter.VideoNotes)).ShouldBe([videoNote.Id]);
        (await Gallery(SharedMediaFilter.Links)).ShouldBe([textLink.Id, url.Id]);

        var page = await alice.GetAsync<MessagesPageDto>(ApiRoutes.Messages.SharedMediaPath(chat, SharedMediaFilter.Media, limit: 2));
        page.Messages.Select(m => m.Id).ShouldBe([album.Id, video.Id]);
        page.HasMoreBefore.ShouldBeTrue();
        page.Messages[0].Attachments.Count.ShouldBe(2);
        var next = await alice.GetAsync<MessagesPageDto>(ApiRoutes.Messages.SharedMediaPath(chat, SharedMediaFilter.Media, beforeSeq: video.Seq, limit: 2));
        next.Messages.Select(m => m.Id).ShouldBe([photo.Id]);
        next.HasMoreBefore.ShouldBeFalse();

        // Default kind is Media; unknown kinds are rejected.
        (await alice.GetAsync<MessagesPageDto>($"api/chats/{chat}/media")).Messages.Count.ShouldBe(3);
        await (await alice.GetRawAsync($"api/chats/{chat}/media?kind=Stickers")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await alice.GetRawAsync($"api/chats/{chat}/media?kind=99")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var outsider = await factory.NewUserAsync("Outsider");
        await (await outsider.GetRawAsync(ApiRoutes.Messages.SharedMediaPath(chat, SharedMediaFilter.Media))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
    }

    [Fact]
    public async Task Shared_media_respects_what_I_can_see()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var cleared = await bob.SendMediaAsync(chat, await factory.CreateMediaAsync(bob, MediaKind.Photo));
        await alice.ClearHistoryAsync(chat);
        var hidden = await bob.SendMediaAsync(chat, await factory.CreateMediaAsync(bob, MediaKind.Photo));
        var shown = await bob.SendMediaAsync(chat, await factory.CreateMediaAsync(bob, MediaKind.Photo));
        await alice.DeleteMessagesAsync(chat, forEveryone: false, hidden.Id);

        (await alice.GetAsync<MessagesPageDto>(ApiRoutes.Messages.SharedMediaPath(chat, SharedMediaFilter.Media))).Messages.Select(m => m.Id).ShouldBe([shown.Id]);
        (await bob.GetAsync<MessagesPageDto>(ApiRoutes.Messages.SharedMediaPath(chat, SharedMediaFilter.Media))).Messages.Select(m => m.Id)
            .ShouldBe([shown.Id, hidden.Id, cleared.Id]);
    }
}
