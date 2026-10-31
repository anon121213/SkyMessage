using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// The realtime hub delivers chat events to members only (SPEC §7), honors blocks for typing (§6.1) and cuts off
/// revoked sessions. "Nothing arrived" is proven with <see cref="HubProbe.FlushAsync"/>, never with a sleep.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RealtimeIsolationTests(ApiFactory factory)
{
    [Fact]
    public async Task Chat_events_never_reach_non_members()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var eve = await factory.RegisterUserAsync("eve");
        await using var bobHub = await HubProbe.ConnectAsync(factory, bob);
        await using var eveHub = await HubProbe.ConnectAsync(factory, eve);

        var chat = await alice.OpenPrivateChatAsync(bob);
        var message = await alice.SendTextAsync(chat.Id, SecurityKit.Unique("between us"));
        await alice.Client.PatchJsonAsync<MessageDto>(ApiRoutes.Messages.ByIdPath(chat.Id, message.Id), new EditMessageRequest { Text = "edited" });
        await bob.Client.PutJsonAsync<MessageDto>(ApiRoutes.Messages.ReactionPath(chat.Id, message.Id), new SetReactionRequest { Emoji = "❤️" });
        await alice.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Chats.ReadPath(chat.Id), new MarkReadRequest { UpToSeq = message.Seq });
        await alice.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Messages.PinPath(chat.Id, message.Id));
        await alice.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Messages.DeletePath(chat.Id), new DeleteMessagesRequest { MessageIds = [message.Id], ForEveryone = true });
        var group = await alice.CreateGroupAsync("Secret society", bob);
        await alice.Client.PatchJsonAsync<ChatInfoDto>(ApiRoutes.Chats.ByIdPath(group.Id), new UpdateChatRequest { Title = "Renamed society" });
        await alice.SendTextAsync(group.Id, "members only");

        // Members got them all ...
        await bobHub.WaitAsync<MessagesDeletedEvent>(nameof(IRealtimeClient.MessagesDeleted), e => e.ChatId == chat.Id && e.ForEveryone);
        await bobHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.ChatId == group.Id && m.Text == "members only");
        await bobHub.WaitAsync<ChatInfoDto>(nameof(IRealtimeClient.ChatUpdated), c => c.Id == group.Id && c.Title == "Renamed society");

        // ... the outsider got none of them.
        await eveHub.FlushAsync(eve);
        var leaked = eveHub.All()
            .Where(e => Mentions(e.Payload, chat.Id, group.Id, message.Id)
                        || e.Payload is UserRefEvent u && (u.UserId == alice.Id || u.UserId == bob.Id)
                        || e.Payload is PresenceChangedEvent p && (p.UserId == alice.Id || p.UserId == bob.Id))
            .Select(e => e.Name)
            .ToList();
        leaked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Typing_into_a_chat_you_are_not_in_reaches_nobody()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var eve = await factory.RegisterUserAsync("eve");
        var chat = await alice.OpenPrivateChatAsync(bob);
        await alice.SendTextAsync(chat.Id, "hi");
        await using var aliceHub = await HubProbe.ConnectAsync(factory, alice);
        await using var eveHub = await HubProbe.ConnectAsync(factory, eve);
        await using var bobHub = await HubProbe.ConnectAsync(factory, bob);

        await eveHub.Connection.InvokeAsync(nameof(IRealtimeServer.SendTyping), chat.Id, TypingAction.Typing, SecurityKit.Ct);
        await bobHub.Connection.InvokeAsync(nameof(IRealtimeServer.SendTyping), chat.Id, TypingAction.RecordingVoice, SecurityKit.Ct);

        await aliceHub.WaitAsync<TypingEvent>(nameof(IRealtimeClient.Typing), t => t.UserId == bob.Id && t.ChatId == chat.Id);
        await aliceHub.FlushAsync(alice);
        aliceHub.Received<TypingEvent>(nameof(IRealtimeClient.Typing)).ShouldNotContain(t => t.UserId == eve.Id);
    }

    /// <summary>SPEC §6.1: "Blocked users never receive Typing/Presence of the blocker" (and §6.2: blockers never get the blocked user's typing).</summary>
    [Fact]
    public async Task Typing_is_not_delivered_across_a_block_in_either_direction()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var carol = await factory.RegisterUserAsync("carol");
        var group = await carol.CreateGroupAsync("Neighbours", alice, bob);
        await alice.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Blocks.ByUserIdPath(bob.Id));
        await using var aliceHub = await HubProbe.ConnectAsync(factory, alice);
        await using var bobHub = await HubProbe.ConnectAsync(factory, bob);
        await using var carolHub = await HubProbe.ConnectAsync(factory, carol);

        await aliceHub.Connection.InvokeAsync(nameof(IRealtimeServer.SendTyping), group.Id, TypingAction.Typing, SecurityKit.Ct);
        await bobHub.Connection.InvokeAsync(nameof(IRealtimeServer.SendTyping), group.Id, TypingAction.Typing, SecurityKit.Ct);
        await carolHub.Connection.InvokeAsync(nameof(IRealtimeServer.SendTyping), group.Id, TypingAction.Typing, SecurityKit.Ct);

        // Everyone hears carol; carol hears both of them.
        await aliceHub.WaitAsync<TypingEvent>(nameof(IRealtimeClient.Typing), t => t.UserId == carol.Id);
        await bobHub.WaitAsync<TypingEvent>(nameof(IRealtimeClient.Typing), t => t.UserId == carol.Id);
        await carolHub.WaitAsync<TypingEvent>(nameof(IRealtimeClient.Typing), t => t.UserId == alice.Id);
        await carolHub.WaitAsync<TypingEvent>(nameof(IRealtimeClient.Typing), t => t.UserId == bob.Id);

        await aliceHub.FlushAsync(alice);
        await bobHub.FlushAsync(bob);
        aliceHub.Received<TypingEvent>(nameof(IRealtimeClient.Typing)).ShouldNotContain(t => t.UserId == bob.Id, "alice blocked bob: she must not see him typing");
        bobHub.Received<TypingEvent>(nameof(IRealtimeClient.Typing)).ShouldNotContain(t => t.UserId == alice.Id, "bob is blocked by alice: he must not see her typing");
    }

    [Fact]
    public async Task Revoked_session_is_told_and_disconnected_and_cannot_reconnect()
    {
        var phone = await factory.RegisterUserAsync("alice");
        var tablet = await factory.SignInAgainAsync(phone, "tablet");
        await using var phoneHub = await HubProbe.ConnectAsync(factory, phone);
        await using var tabletHub = await HubProbe.ConnectAsync(factory, tablet);

        await phone.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Sessions.ByIdPath(tablet.SessionId));

        (await tabletHub.WaitAsync<SessionRevokedEvent>(nameof(IRealtimeClient.SessionRevoked))).SessionId.ShouldBe(tablet.SessionId);
        await tabletHub.Closed.WaitAsync(SecurityKit.EventTimeout, SecurityKit.Ct);
        tabletHub.Connection.State.ShouldBe(HubConnectionState.Disconnected);

        await using var again = factory.CreateHubConnection(tablet.AccessToken);
        var error = await Should.ThrowAsync<HttpRequestException>(() => again.StartAsync(SecurityKit.Ct));
        error.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The other device was not affected.
        await phoneHub.FlushAsync(phone);
        phoneHub.Received<SessionRevokedEvent>(nameof(IRealtimeClient.SessionRevoked)).ShouldBeEmpty();
        phoneHub.Connection.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Removed_member_stops_receiving_the_group()
    {
        var owner = await factory.RegisterUserAsync("owner");
        var max = await factory.RegisterUserAsync("max");
        var group = await owner.CreateGroupAsync("Club", max);
        await using var maxHub = await HubProbe.ConnectAsync(factory, max);

        await owner.SendTextAsync(group.Id, "before");
        await maxHub.WaitAsync<MessageDto>(nameof(IRealtimeClient.MessageCreated), m => m.ChatId == group.Id && m.Text == "before");

        await owner.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Chats.MemberPath(group.Id, max.Id));
        await maxHub.WaitAsync<ChatRefEvent>(nameof(IRealtimeClient.ChatRemoved), e => e.ChatId == group.Id);

        await owner.SendTextAsync(group.Id, "after");
        await owner.Client.PatchJsonAsync<ChatInfoDto>(ApiRoutes.Chats.ByIdPath(group.Id), new UpdateChatRequest { Title = "Club without max" });
        await maxHub.FlushAsync(max);
        maxHub.Received<MessageDto>(nameof(IRealtimeClient.MessageCreated)).ShouldNotContain(m => m.ChatId == group.Id && m.Text == "after");
        maxHub.Received<ChatInfoDto>(nameof(IRealtimeClient.ChatUpdated)).ShouldNotContain(c => c.Title == "Club without max");
    }

    /// <summary>
    /// Long-polling connections are addressed by a connection token in the URL (which can end up in logs). Another
    /// signed-in user presenting that token must not be able to read, write to or close the victim's connection.
    /// </summary>
    [Fact]
    public async Task A_realtime_connection_token_is_useless_to_another_user()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var eve = await factory.RegisterUserAsync("eve");
        var hub = HubRoutes.Realtime.TrimStart('/');

        using var negotiate = await alice.Client.PostAsync($"{hub}/negotiate?negotiateVersion=1", null, SecurityKit.Ct);
        await negotiate.ShouldHaveStatusAsync(HttpStatusCode.OK);
        using var negotiation = System.Text.Json.JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync(SecurityKit.Ct));
        var token = negotiation.RootElement.GetProperty("connectionToken").GetString()!;
        var path = $"{hub}?id={Uri.EscapeDataString(token)}";

        using (var handshake = await alice.Client.PostAsync(path, new StringContent("{\"protocol\":\"json\",\"version\":1}\u001e"), SecurityKit.Ct))
            await handshake.ShouldHaveStatusAsync(HttpStatusCode.OK);
        using (var first = await alice.Client.GetAsync(path, SecurityKit.Ct))
            await first.ShouldHaveStatusAsync(HttpStatusCode.OK);
        // An invocation is only dispatched after OnConnectedAsync: once its completion arrives, alice is registered.
        using (var ready = await alice.Client.PostAsync(path,
                   new StringContent("{\"type\":1,\"invocationId\":\"0\",\"target\":\"SetActive\",\"arguments\":[true]}\u001e"), SecurityKit.Ct))
            await ready.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var registered = false;
        for (var poll = 0; poll < 5 && !registered; poll++)
        {
            using var response = await alice.Client.GetAsync(path, SecurityKit.Ct);
            await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
            registered = (await response.Content.ReadAsStringAsync(SecurityKit.Ct)).Contains("\"invocationId\":\"0\"", StringComparison.Ordinal);
        }
        registered.ShouldBeTrue("alice's raw long-polling connection never completed its first invocation");

        // Something for alice is now waiting in her connection.
        var chat = await bob.OpenPrivateChatAsync(alice);
        var secret = SecurityKit.Unique("for alice only");
        await bob.SendTextAsync(chat.Id, secret);

        var breaches = new List<string>();
        using (var stolenPoll = await eve.Client.GetAsync(path, SecurityKit.Ct))
        {
            var body = await stolenPoll.Content.ReadAsStringAsync(SecurityKit.Ct);
            if (stolenPoll.StatusCode == HttpStatusCode.OK || body.Contains(secret, StringComparison.Ordinal))
                breaches.Add($"GET (poll) as eve → {(int)stolenPoll.StatusCode}, body: {SecurityKit.Truncate(body, 160)}");
        }
        using (var stolenSend = await eve.Client.PostAsync(path,
                   new StringContent("{\"type\":1,\"invocationId\":\"1\",\"target\":\"SetActive\",\"arguments\":[false]}\u001e"), SecurityKit.Ct))
        {
            if (stolenSend.IsSuccessStatusCode)
                breaches.Add($"POST (send) as eve → {(int)stolenSend.StatusCode}");
        }
        using (var stolenClose = await eve.Client.DeleteAsync(path, SecurityKit.Ct))
        {
            if (stolenClose.IsSuccessStatusCode)
                breaches.Add($"DELETE (close) as eve → {(int)stolenClose.StatusCode}");
        }
        breaches.ShouldBeEmpty();

        // alice still gets her message on her own connection.
        using var mine = await alice.Client.GetAsync(path, SecurityKit.Ct);
        await mine.ShouldHaveStatusAsync(HttpStatusCode.OK);
        (await mine.Content.ReadAsStringAsync(SecurityKit.Ct)).ShouldContain(secret);
        (await alice.Client.DeleteAsync(path, SecurityKit.Ct)).Dispose();
    }

    private static bool Mentions(object payload, Guid chatId, Guid groupId, Guid messageId) => payload switch
    {
        MessageDto m => m.ChatId == chatId || m.ChatId == groupId || m.Id == messageId,
        MessagesDeletedEvent e => e.ChatId == chatId || e.ChatId == groupId,
        MessagesReadEvent e => e.ChatId == chatId || e.ChatId == groupId,
        TypingEvent e => e.ChatId == chatId || e.ChatId == groupId,
        ChatRefEvent e => e.ChatId == chatId || e.ChatId == groupId,
        ChatInfoDto e => e.Id == chatId || e.Id == groupId,
        HistoryClearedEvent e => e.ChatId == chatId || e.ChatId == groupId,
        ChatSettingsChangedEvent e => e.ChatId == chatId || e.ChatId == groupId,
        CallDto e => e.ChatId == chatId || e.ChatId == groupId,
        _ => false,
    };
}
