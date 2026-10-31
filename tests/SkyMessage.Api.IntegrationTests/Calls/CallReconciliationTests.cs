using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Application.Calls;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.Calls;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>
/// The periodic comparison of active calls with LiveKit (normally every 30 s), run by hand on a host whose
/// <see cref="IClock"/> can jump ahead past the 60 s absence timeout.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CallReconciliationTests(ApiFactory api) : IAsyncLifetime
{
    private readonly ShiftedClock _clock = new();
    private FakeLiveKit _liveKit = null!;
    private WebApplicationFactory<Program> _host = null!;

    public async ValueTask InitializeAsync()
    {
        _liveKit = await FakeLiveKit.StartAsync();
        _liveKit.ListRoomsEnabled = true;
        _host = api.WithFakeLiveKit(_liveKit, services =>
        {
            services.AddSingleton<IClock>(_clock);
            // This test drives reconciliation itself.
            foreach (var hosted in services.Where(d => d.ImplementationType == typeof(CallMaintenanceService)).ToList())
                services.Remove(hosted);
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _liveKit.DisposeAsync();
    }

    private Task ReconcileAsync() => _host.RunMaintenanceAsync(m => m.ReconcileAsync(CallKit.Ct));

    private static string Room(CallDto call) => LiveKitNames.RoomName(call.Id);

    [Fact]
    public async Task An_answered_call_whose_room_is_gone_for_over_a_minute_is_ended_with_a_log()
    {
        var alice = await _host.NewUserAsync("Alice");
        var bob = await _host.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        await using var bobHub = await CallHub.ConnectAsync(bob);
        var call = await alice.ActivePrivateCallAsync(bob, chatId);

        await ReconcileAsync();
        _clock.Offset = TimeSpan.FromSeconds(50);
        await ReconcileAsync();
        (await bob.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Active);

        _clock.Offset = TimeSpan.FromSeconds(62);
        await ReconcileAsync();

        var ended = await bobHub.CallStatusAsync(call.Id, CallStatus.Ended);
        ended.Participants.ShouldAllBe(p => p.Status == CallParticipantStatus.Left);
        (await bobHub.CallLogAsync(chatId, call.Id)).Call!.Status.ShouldBe(CallStatus.Ended);
        _liveKit.TwirpCalls("ListRooms").ShouldAllBe(c => c.Authorized && c.Grant!.Value.GetProperty("roomList").GetBoolean());
    }

    [Fact]
    public async Task Members_absent_from_a_group_call_room_are_marked_left_and_present_ones_joined()
    {
        var owner = await _host.NewUserAsync("Olga");
        var bob = await _host.NewUserAsync("Bob");
        var carol = await _host.NewUserAsync("Carol");
        var groupId = await owner.CreateGroupAsync("Team", bob, carol);
        await using var ownerHub = await CallHub.ConnectAsync(owner);
        var call = (await owner.StartCallAsync(groupId)).Call;
        await bob.JoinAsync(call.Id);
        // Carol is talking although her join never reached the API; Bob's app died without leaving.
        _liveKit.AddParticipant(Room(call), owner.Identity);
        _liveKit.AddParticipant(Room(call), carol.Identity);

        await ReconcileAsync();

        var carolIn = await ownerHub.CallUpdatedAsync(call.Id, c => c.StatusOf(carol) == CallParticipantStatus.Joined);
        carolIn.StatusOf(bob).ShouldBe(CallParticipantStatus.Joined);
        _liveKit.TwirpCalls("ListParticipants", Room(call)).ShouldNotBeEmpty();

        _clock.Offset = TimeSpan.FromSeconds(61);
        await ReconcileAsync();

        var bobOut = await ownerHub.CallUpdatedAsync(call.Id, c => c.StatusOf(bob) == CallParticipantStatus.Left);
        bobOut.Status.ShouldBe(CallStatus.Active);
        bobOut.StatusOf(owner).ShouldBe(CallParticipantStatus.Joined);
        bobOut.StatusOf(carol).ShouldBe(CallParticipantStatus.Joined);
        await owner.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Members_removed_while_livekit_was_down_are_kicked_by_the_next_reconciliation()
    {
        var owner = await _host.NewUserAsync("Olga");
        var bob = await _host.NewUserAsync("Bob");
        var groupId = await owner.CreateGroupAsync("Team", bob);
        var call = (await owner.StartCallAsync(groupId)).Call;
        await bob.JoinAsync(call.Id);
        _liveKit.AddParticipant(Room(call), owner.Identity);
        _liveKit.AddParticipant(Room(call), bob.Identity);

        _liveKit.Available = false;
        await owner.RemoveMemberAsync(groupId, bob);
        (await owner.GetCallAsync(call.Id)).StatusOf(bob).ShouldBe(CallParticipantStatus.Left);
        _liveKit.Available = true;
        _liveKit.TwirpCalls("RemoveParticipant").ShouldBeEmpty();

        await ReconcileAsync();

        var kick = await _liveKit.WaitForTwirpAsync("RemoveParticipant", c => c.Identity == bob.Identity);
        kick.Room.ShouldBe(Room(call));
        var now = await owner.GetCallAsync(call.Id);
        now.Status.ShouldBe(CallStatus.Active);
        now.StatusOf(bob).ShouldBe(CallParticipantStatus.Left);
        await owner.HangupAsync(call.Id);
    }

    [Fact]
    public async Task Nothing_is_ended_while_livekit_cannot_be_asked()
    {
        var alice = await _host.NewUserAsync("Alice");
        var bob = await _host.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        var call = await alice.ActivePrivateCallAsync(bob, chatId);
        _liveKit.Available = false;

        await ReconcileAsync();
        _clock.Offset = TimeSpan.FromMinutes(5);
        await ReconcileAsync();

        (await bob.GetCallAsync(call.Id)).Status.ShouldBe(CallStatus.Active);
        _liveKit.Available = true;
        await alice.HangupAsync(call.Id);
    }
}

/// <summary>The system clock (microsecond precision, like the server's) moved by <see cref="Offset"/>.</summary>
internal sealed class ShiftedClock : IClock
{
    public TimeSpan Offset { get; set; }

    public DateTimeOffset UtcNow
    {
        get
        {
            var now = DateTimeOffset.UtcNow + Offset;
            return new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        }
    }
}
