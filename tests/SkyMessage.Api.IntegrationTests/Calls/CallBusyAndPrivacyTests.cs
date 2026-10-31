using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>Who may ring whom: busy detection across chats, the Calls privacy level and blocks.</summary>
[Collection(ApiCollection.Name)]
public sealed class CallBusyAndPrivacyTests(ApiFactory api)
{
    [Fact]
    public async Task Callee_talking_in_another_call_is_busy_until_that_call_ends()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var carol = await api.NewUserAsync("Carol");
        var aliceBob = await alice.PrivateChatWithAsync(bob);
        var carolBob = await carol.PrivateChatWithAsync(bob);
        await using var bobHub = await CallHub.ConnectAsync(bob);
        var other = await carol.ActivePrivateCallAsync(bob, carolBob);

        await (await alice.TryStartCallAsync(aliceBob)).ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CallBusy);

        (await alice.GetActiveCallAsync(aliceBob)).ShouldBeNull();
        await bobHub.FlushAsync();
        bobHub.Received<CallDto>(nameof(IRealtimeClient.IncomingCall), c => c.ChatId == aliceBob).ShouldBeEmpty();

        await carol.HangupAsync(other.Id);
        var call = (await alice.StartCallAsync(aliceBob)).Call;
        call.Status.ShouldBe(CallStatus.Ringing);
        await bobHub.IncomingCallAsync(call.Id);
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Ringing_and_calling_count_as_busy()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var carol = await api.NewUserAsync("Carol");
        var dave = await api.NewUserAsync("Dave");
        var aliceBob = await alice.PrivateChatWithAsync(bob);
        var carolBob = await carol.PrivateChatWithAsync(bob);
        var bobDave = await bob.PrivateChatWithAsync(dave);

        // Bob's phone is already ringing (Carol calls).
        var ringing = (await carol.StartCallAsync(carolBob)).Call;
        await (await alice.TryStartCallAsync(aliceBob)).ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CallBusy);
        await bob.DeclineAsync(ringing.Id);

        // Bob is calling Dave himself.
        var calling = (await bob.StartCallAsync(bobDave)).Call;
        await (await alice.TryStartCallAsync(aliceBob)).ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CallBusy);
        await bob.HangupAsync(calling.Id);

        // Missed, declined and cancelled calls leave nobody busy.
        var free = (await alice.StartCallAsync(aliceBob)).Call;
        free.Status.ShouldBe(CallStatus.Ringing);
        await alice.HangupAsync(free.Id);
    }

    [Fact]
    public async Task Group_call_invitations_do_not_make_busy_but_taking_part_does()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var carol = await api.NewUserAsync("Carol");
        var aliceBob = await alice.PrivateChatWithAsync(bob);
        var group = await carol.CreateGroupAsync("Family", bob);
        var groupCall = (await carol.StartCallAsync(group)).Call;
        groupCall.StatusOf(bob).ShouldBe(CallParticipantStatus.Invited);

        var call = (await alice.StartCallAsync(aliceBob)).Call;
        call.Status.ShouldBe(CallStatus.Ringing);
        await alice.HangupAsync(call.Id);

        await bob.JoinAsync(groupCall.Id);
        await (await alice.TryStartCallAsync(aliceBob)).ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CallBusy);

        await bob.HangupAsync(groupCall.Id);
        var afterLeaving = (await alice.StartCallAsync(aliceBob)).Call;
        afterLeaving.Status.ShouldBe(CallStatus.Ringing);
        await alice.HangupAsync(afterLeaving.Id);
        await carol.HangupAsync(groupCall.Id);
    }

    [Fact]
    public async Task Two_callers_ringing_the_same_person_at_once_get_one_call_and_one_busy()
    {
        var bob = await api.NewUserAsync("Bob");
        var callers = new List<(CallUser User, Guid ChatId)>();
        for (var i = 0; i < 3; i++)
        {
            var caller = await api.NewUserAsync($"Caller {i}");
            callers.Add((caller, await caller.PrivateChatWithAsync(bob)));
        }

        var responses = await Task.WhenAll(callers.Select(c => c.User.TryStartCallAsync(c.ChatId)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        foreach (var busy in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            await busy.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.CallBusy);
        var winner = callers[Array.FindIndex(responses, r => r.StatusCode == HttpStatusCode.OK)];
        var call = await responses.Single(r => r.StatusCode == HttpStatusCode.OK).ReadAsync<CallJoinInfoDto>();
        await winner.User.HangupAsync(call.Call.Id);
    }

    [Theory]
    [InlineData(PrivacyLevel.Nobody)]
    [InlineData(PrivacyLevel.Contacts)]
    public async Task Calls_privacy_refuses_strangers(PrivacyLevel level)
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        await bob.SetCallPrivacyAsync(level);
        await using var bobHub = await CallHub.ConnectAsync(bob);

        await (await alice.TryStartCallAsync(chatId)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.PrivacyRestricted);

        (await alice.GetActiveCallAsync(chatId)).ShouldBeNull();
        await bobHub.FlushAsync();
        bobHub.Received<CallDto>(nameof(IRealtimeClient.IncomingCall)).ShouldBeEmpty();
        // Bob can still call Alice: the level protects Bob, not Alice.
        var back = (await bob.StartCallAsync(chatId)).Call;
        await bob.HangupAsync(back.Id);
    }

    [Fact]
    public async Task Contacts_level_lets_contacts_call()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        await bob.SetCallPrivacyAsync(PrivacyLevel.Contacts);
        // Alice having Bob in her contacts does not matter; Bob having Alice does.
        await alice.AddContactAsync(bob);
        await (await alice.TryStartCallAsync(chatId)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.PrivacyRestricted);

        await bob.AddContactAsync(alice);

        var call = (await alice.StartCallAsync(chatId)).Call;
        call.Status.ShouldBe(CallStatus.Ringing);
        await alice.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Blocks_forbid_calls_in_both_directions()
    {
        var alice = await api.NewUserAsync("Alice");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);

        await bob.BlockAsync(alice);

        await (await alice.TryStartCallAsync(chatId)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        await (await bob.TryStartCallAsync(chatId)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        (await alice.GetActiveCallAsync(chatId)).ShouldBeNull();
    }
}
