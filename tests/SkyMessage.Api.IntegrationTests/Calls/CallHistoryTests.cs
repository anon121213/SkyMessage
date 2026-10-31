using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary><c>GET api/calls/history</c>: my calls, newest first, with the other side / group and paging.</summary>
[Collection(ApiCollection.Name)]
public sealed class CallHistoryTests(ApiFactory api)
{
    [Fact]
    public async Task History_lists_my_private_and_group_calls_newest_first()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var carol = await api.NewUserAsync("Carol");
        var chatId = await alice.PrivateChatWithAsync(bob);
        var groupId = await carol.CreateGroupAsync("Book club", alice, bob);

        var declined = (await alice.StartCallAsync(chatId)).Call;
        await bob.DeclineAsync(declined.Id);
        var answered = await alice.ActivePrivateCallAsync(bob, chatId);
        await alice.HangupAsync(answered.Id);
        var incoming = (await bob.StartCallAsync(chatId, isVideo: true)).Call;
        await bob.HangupAsync(incoming.Id);
        var group = (await carol.StartCallAsync(groupId)).Call;
        await carol.HangupAsync(group.Id);

        var history = await alice.GetHistoryAsync();

        history.Select(h => h.Call.Id).ShouldBe([group.Id, incoming.Id, answered.Id, declined.Id]);
        history.Select(h => h.Call.Status).ShouldBe([CallStatus.Ended, CallStatus.Cancelled, CallStatus.Ended, CallStatus.Declined]);

        var privateItem = history.Single(h => h.Call.Id == incoming.Id);
        privateItem.ChatType.ShouldBe(ChatType.Private);
        privateItem.IsOutgoing.ShouldBeFalse();
        privateItem.Call.IsVideo.ShouldBeTrue();
        privateItem.Peer!.Id.ShouldBe(bob.Id);
        privateItem.ChatTitle.ShouldBe("Bob");
        privateItem.DurationSeconds.ShouldBe(0);
        privateItem.Call.ShouldHaveParticipants((alice, CallParticipantStatus.Missed), (bob, CallParticipantStatus.Left));
        history.Single(h => h.Call.Id == declined.Id).IsOutgoing.ShouldBeTrue();
        var answeredItem = history.Single(h => h.Call.Id == answered.Id);
        answeredItem.DurationSeconds.ShouldBe((int)Math.Floor((answeredItem.Call.EndedAt!.Value - answeredItem.Call.AnsweredAt!.Value).TotalSeconds));

        // Groups: titled by the group; only the people who took part (and me) are listed.
        var groupItem = history.Single(h => h.Call.Id == group.Id);
        groupItem.ChatType.ShouldBe(ChatType.Group);
        groupItem.ChatTitle.ShouldBe("Book club");
        groupItem.Peer.ShouldBeNull();
        groupItem.IsOutgoing.ShouldBeFalse();
        groupItem.Call.ShouldHaveParticipants((carol, CallParticipantStatus.Left), (alice, CallParticipantStatus.Missed));

        var bobs = await bob.GetHistoryAsync();
        bobs.Select(h => h.Call.Id).ShouldBe([group.Id, incoming.Id, answered.Id, declined.Id]);
        bobs.Single(h => h.Call.Id == incoming.Id).IsOutgoing.ShouldBeTrue();
        bobs.Single(h => h.Call.Id == declined.Id).Peer!.Id.ShouldBe(alice.Id);
    }

    [Fact]
    public async Task History_pages_with_before_and_limit()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var call = (await alice.StartCallAsync(chatId)).Call;
            await alice.HangupAsync(call.Id);
            ids.Insert(0, call.Id);
        }

        var first = await alice.GetHistoryAsync(limit: 2);
        first.Select(h => h.Call.Id).ShouldBe(ids.Take(2));
        var second = await alice.GetHistoryAsync(before: first[^1].Call.CreatedAt, limit: 2);
        second.Select(h => h.Call.Id).ShouldBe(ids.Skip(2).Take(2));
        var rest = await alice.GetHistoryAsync(before: second[^1].Call.CreatedAt, limit: 2);
        rest.Select(h => h.Call.Id).ShouldBe(ids.Skip(4));

        // Out-of-range limits are clamped instead of failing.
        (await alice.GetHistoryAsync(limit: 0)).Count.ShouldBe(1);
        (await alice.GetHistoryAsync(limit: 1000)).Count.ShouldBe(5);
    }

    [Fact]
    public async Task History_accepts_a_cursor_with_any_utc_offset()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        var older = (await alice.StartCallAsync(chatId)).Call;
        await alice.HangupAsync(older.Id);
        var newer = (await alice.StartCallAsync(chatId)).Call;
        await alice.HangupAsync(newer.Id);

        // A client in Bangkok formatting its local time: same instant, +07:00 offset.
        var cursor = newer.CreatedAt.ToOffset(TimeSpan.FromHours(7)).ToString("O");
        var response = await alice.Http.GetAsync($"{ApiRoutes.Calls.History}?limit=10&before={Uri.EscapeDataString(cursor)}", CallKit.Ct);

        (await response.ReadAsync<List<CallHistoryItemDto>>()).Select(h => h.Call.Id).ShouldBe([older.Id]);
    }

    [Fact]
    public async Task Peers_are_titled_by_my_contact_name()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        await alice.AddContactAsync(bob, "Brother");
        var call = (await alice.StartCallAsync(chatId)).Call;
        await alice.HangupAsync(call.Id);

        var item = (await alice.GetHistoryAsync()).Single();

        item.ChatTitle.ShouldBe("Brother");
        item.Peer!.ContactName.ShouldBe("Brother");
        (await bob.GetHistoryAsync()).Single().ChatTitle.ShouldBe("Alice");
    }

    [Fact]
    public async Task Leaving_a_group_drops_its_calls_from_my_history()
    {
        var owner = await api.NewUserAsync("Olga");
        var bob = await api.NewUserAsync("Bob");
        var groupId = await owner.CreateGroupAsync("Team", bob);
        var call = (await owner.StartCallAsync(groupId)).Call;
        await bob.JoinAsync(call.Id);
        await bob.HangupAsync(call.Id);
        await owner.HangupAsync(call.Id);
        (await bob.GetHistoryAsync()).Select(h => h.Call.Id).ShouldBe([call.Id]);

        await bob.LeaveChatAsync(groupId);

        (await bob.GetHistoryAsync()).ShouldBeEmpty();
        (await owner.GetHistoryAsync()).Select(h => h.Call.Id).ShouldBe([call.Id]);
        (await bob.Http.GetAsync(ApiRoutes.Calls.HistoryPath(), CallKit.Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
