using System.Text.Json;
using Shouldly;
using SkyMessage.Application.Calls;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>The LiveKit join token handed out by start / accept / join, decoded as LiveKit would read it.</summary>
[Collection(ApiCollection.Name)]
public sealed class LiveKitTokenTests(ApiFactory api)
{
    private static void ShouldBeJoinToken(string token, CallUser device, CallDto call, string displayName)
    {
        var header = Jwt.Header(token);
        header.GetProperty("alg").GetString().ShouldBe("HS256");
        header.GetProperty("typ").GetString().ShouldBe("JWT");

        var payload = Jwt.Payload(token);
        payload.GetProperty("iss").GetString().ShouldBe(LiveKitDev.ApiKey);
        payload.GetProperty("sub").GetString().ShouldBe($"{device.Id:N}.{device.SessionId:N}");
        payload.GetProperty("name").GetString().ShouldBe(displayName);

        var nbf = DateTimeOffset.FromUnixTimeSeconds(payload.GetProperty("nbf").GetInt64());
        var exp = DateTimeOffset.FromUnixTimeSeconds(payload.GetProperty("exp").GetInt64());
        (exp - nbf).ShouldBe(TimeSpan.FromMinutes(15));
        nbf.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddSeconds(5));

        // The grant is a JSON object (LiveKit rejects a stringified one) for exactly this room.
        var video = payload.GetProperty("video");
        video.ValueKind.ShouldBe(JsonValueKind.Object);
        video.GetProperty("room").GetString().ShouldBe($"call-{call.Id:N}");
        video.GetProperty("room").GetString().ShouldBe(LiveKitNames.RoomName(call.Id));
        foreach (var flag in new[] { "roomJoin", "canPublish", "canSubscribe", "canPublishData" })
            video.GetProperty(flag).GetBoolean().ShouldBeTrue(flag);
        video.EnumerateObject().Select(p => p.Name).Order()
            .ShouldBe(["canPublish", "canPublishData", "canSubscribe", "room", "roomJoin"]); // no admin/create/list grants

        // metadata is a JSON string the clients parse to group devices into people.
        var metadata = payload.GetProperty("metadata");
        metadata.ValueKind.ShouldBe(JsonValueKind.String);
        using var parsed = JsonDocument.Parse(metadata.GetString()!);
        parsed.RootElement.GetProperty("userId").GetGuid().ShouldBe(device.Id);
        parsed.RootElement.GetProperty("avatarMediaId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Start_accept_and_join_hand_out_per_device_tokens_for_the_call_room()
    {
        var alice = await api.NewUserAsync("Алиса Петрова");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);

        var started = await alice.StartCallAsync(chatId);
        ShouldBeJoinToken(started.LiveKitToken, alice, started.Call, "Алиса Петрова");
        (await Jwt.IsSignedByDevKeyAsync(started.LiveKitToken)).ShouldBeTrue();

        var accepted = await bob.AcceptAsync(started.Call.Id);
        ShouldBeJoinToken(accepted.LiveKitToken, bob, started.Call, "Bob");
        (await Jwt.IsSignedByDevKeyAsync(accepted.LiveKitToken)).ShouldBeTrue();

        // A second device of the same person is another participant of the same room.
        var bobPhone = await bob.SignInAgainAsync();
        var fromPhone = await bobPhone.JoinAsync(started.Call.Id);
        ShouldBeJoinToken(fromPhone.LiveKitToken, bobPhone, started.Call, "Bob");
        Jwt.Payload(fromPhone.LiveKitToken).GetProperty("sub").GetString()
            .ShouldNotBe(Jwt.Payload(accepted.LiveKitToken).GetProperty("sub").GetString());
        LiveKitNames.TryParseIdentity(Jwt.Payload(fromPhone.LiveKitToken).GetProperty("sub").GetString(), out var userId, out var sessionId).ShouldBeTrue();
        userId.ShouldBe(bob.Id);
        sessionId.ShouldBe(bobPhone.SessionId);

        await alice.HangupAsync(started.Call.Id);
    }

    [Fact]
    public async Task Group_call_tokens_name_the_group_call_room()
    {
        var owner = await api.NewUserAsync("Olga");
        var bob = await api.NewUserAsync("Bob");
        var chatId = await owner.CreateGroupAsync("Team", bob);

        var started = await owner.StartCallAsync(chatId);
        var joined = await bob.JoinAsync(started.Call.Id);

        ShouldBeJoinToken(started.LiveKitToken, owner, started.Call, "Olga");
        ShouldBeJoinToken(joined.LiveKitToken, bob, started.Call, "Bob");
        started.LiveKitUrl.ShouldBe("/livekit");
        joined.LiveKitUrl.ShouldBe("/livekit");

        await owner.HangupAsync(started.Call.Id);
        await bob.HangupAsync(started.Call.Id);
    }
}
