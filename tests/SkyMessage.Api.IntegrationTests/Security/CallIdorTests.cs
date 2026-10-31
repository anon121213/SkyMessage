using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>Calls belong to the members of their chat: everybody else gets 404 call_not_found and changes nothing.</summary>
[Collection(ApiCollection.Name)]
public sealed class CallIdorTests(ApiFactory factory)
{
    [Fact]
    public async Task Outsider_can_neither_see_nor_control_a_private_call()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");
        var eve = await factory.RegisterUserAsync("eve");
        var chat = await alice.OpenPrivateChatAsync(bob);
        await alice.SendTextAsync(chat.Id, "calling you");
        var start = await alice.Client.PostJsonAsync<CallJoinInfoDto>(ApiRoutes.Calls.Root, new StartCallRequest { ChatId = chat.Id, IsVideo = true });
        var callId = start.Call.Id;
        start.Call.Status.ShouldBe(CallStatus.Ringing);

        try
        {
            await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Calls.ByIdPath(callId), null, HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
            await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Calls.AcceptPath(callId), null, HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
            await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Calls.DeclinePath(callId), new DeclineCallRequest { Busy = true },
                HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
            await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Calls.JoinPath(callId), null, HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
            await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Calls.HangupPath(callId), null, HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
            await eve.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Calls.ActivePath(chat.Id), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
            await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Calls.Root, new StartCallRequest { ChatId = chat.Id },
                HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
            (await eve.Client.GetJsonAsync<List<CallHistoryItemDto>>(ApiRoutes.Calls.HistoryPath())).ShouldNotContain(h => h.Call.Id == callId);

            // Still ringing, with exactly the two original participants.
            var call = await bob.Client.GetJsonAsync<CallDto>(ApiRoutes.Calls.ByIdPath(callId));
            call.Status.ShouldBe(CallStatus.Ringing);
            call.Participants.Select(p => p.UserId).ShouldBe([alice.Id, bob.Id], ignoreOrder: true);
            call.Participants.Single(p => p.UserId == bob.Id).Status.ShouldBe(CallParticipantStatus.Ringing);
        }
        finally
        {
            await alice.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Calls.HangupPath(callId));
        }

        (await alice.Client.GetJsonAsync<CallDto>(ApiRoutes.Calls.ByIdPath(callId))).Status.ShouldBe(CallStatus.Cancelled);
    }

    [Fact]
    public async Task Only_current_members_can_join_a_group_call()
    {
        var owner = await factory.RegisterUserAsync("owner");
        var max = await factory.RegisterUserAsync("max");
        var nina = await factory.RegisterUserAsync("nina");
        var eve = await factory.RegisterUserAsync("eve");
        var group = await owner.CreateGroupAsync("Call group", max, nina);
        var start = await owner.Client.PostJsonAsync<CallJoinInfoDto>(ApiRoutes.Calls.Root, new StartCallRequest { ChatId = group.Id });
        var callId = start.Call.Id;

        try
        {
            await owner.Client.NoContentAsync(HttpMethod.Delete, ApiRoutes.Chats.MemberPath(group.Id, nina.Id));
            await nina.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Calls.JoinPath(callId), null, HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
            await nina.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Calls.ByIdPath(callId), null, HttpStatusCode.NotFound, ErrorCodes.CallNotFound);
            await eve.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Calls.JoinPath(callId), null, HttpStatusCode.NotFound, ErrorCodes.CallNotFound);

            var joined = await max.Client.PostJsonAsync<CallJoinInfoDto>(ApiRoutes.Calls.JoinPath(callId), null);
            joined.LiveKitToken.ShouldNotBeNullOrEmpty();
            var call = await owner.Client.GetJsonAsync<CallDto>(ApiRoutes.Calls.ByIdPath(callId));
            call.Participants.ShouldNotContain(p => p.UserId == eve.Id);
            call.Participants.Where(p => p.Status == CallParticipantStatus.Joined).Select(p => p.UserId).ShouldBe([owner.Id, max.Id], ignoreOrder: true);
        }
        finally
        {
            await max.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Calls.HangupPath(callId));
            await owner.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Calls.HangupPath(callId));
        }

        (await owner.Client.GetJsonAsync<CallDto>(ApiRoutes.Calls.ByIdPath(callId))).Status.ShouldBe(CallStatus.Ended);
    }
}
