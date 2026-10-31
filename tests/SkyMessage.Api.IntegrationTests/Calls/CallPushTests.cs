using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>Push hand-off of the call lifecycle (delivery itself is v2): who is woken up for incoming and ended calls.</summary>
[Collection(ApiCollection.Name)]
public sealed class CallPushTests(ApiFactory api) : IAsyncLifetime
{
    private readonly RecordingPush _push = new();
    private readonly ShiftedClock _clock = new();
    private WebApplicationFactory<Program> _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = api.WithSettings(new Dictionary<string, string?>(), services =>
        {
            services.AddSingleton<IPushNotificationService>(_push);
            services.AddSingleton<IClock>(_clock);
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Private_calls_push_the_callee_when_ringing_and_both_when_finished()
    {
        var alice = await _host.NewUserAsync("Alice");
        var bob = await _host.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);

        var call = (await alice.StartCallAsync(chatId)).Call;
        await bob.DeclineAsync(call.Id);

        _push.For(call.Id).Select(p => (p.Kind, p.Status)).ShouldBe([("incoming", CallStatus.Ringing), ("ended", CallStatus.Declined)]);
        _push.For(call.Id)[0].Recipients.ShouldBe([bob.Id]);
        _push.For(call.Id)[1].Recipients.Order().ShouldBe(new[] { alice.Id, bob.Id }.Order());
    }

    [Fact]
    public async Task A_call_nobody_answered_pushes_the_missed_call()
    {
        var alice = await _host.NewUserAsync("Alice");
        var bob = await _host.NewUserAsync("Bob");
        var chatId = await alice.PrivateChatWithAsync(bob);
        var call = (await alice.StartCallAsync(chatId)).Call;

        // Only this host's clock moves on, so only this host (with the recording push service) expires the call.
        _clock.Offset = TimeSpan.FromSeconds(Limits.CallRingTimeoutSeconds + 1);
        await _host.RunMaintenanceAsync(m => m.ExpireRingingAsync(CallKit.Ct));

        await CallKit.EventuallyAsync(() => _push.For(call.Id).Any(p => p.Kind == "ended"), "the missed call was pushed");
        var ended = _push.For(call.Id).Single(p => p.Kind == "ended");
        ended.Status.ShouldBe(CallStatus.Missed);
        ended.Recipients.ShouldContain(bob.Id);
    }

    [Fact]
    public async Task Group_calls_push_every_other_member()
    {
        var owner = await _host.NewUserAsync("Olga");
        var bob = await _host.NewUserAsync("Bob");
        var carol = await _host.NewUserAsync("Carol");
        var groupId = await owner.CreateGroupAsync("Team", bob, carol);

        var call = (await owner.StartCallAsync(groupId)).Call;
        await owner.HangupAsync(call.Id);

        var incoming = _push.For(call.Id).Single(p => p.Kind == "incoming");
        incoming.Recipients.Order().ShouldBe(new[] { bob.Id, carol.Id }.Order());
        _push.For(call.Id).Single(p => p.Kind == "ended").Recipients.Order().ShouldBe(new[] { owner.Id, bob.Id, carol.Id }.Order());
    }

    private sealed class RecordingPush : IPushNotificationService
    {
        private readonly ConcurrentQueue<(string Kind, Guid CallId, CallStatus Status, IReadOnlyList<Guid> Recipients)> _sent = new();

        public IReadOnlyList<(string Kind, Guid CallId, CallStatus Status, IReadOnlyList<Guid> Recipients)> For(Guid callId) =>
            _sent.Where(p => p.CallId == callId).ToList();

        public Task NotifyNewMessageAsync(MessageDto message, IReadOnlyCollection<Guid> recipientIds, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task NotifyIncomingCallAsync(CallDto call, IReadOnlyCollection<Guid> recipientIds, CancellationToken cancellationToken = default)
        {
            _sent.Enqueue(("incoming", call.Id, call.Status, recipientIds.ToList()));
            return Task.CompletedTask;
        }

        public Task NotifyCallEndedAsync(CallDto call, IReadOnlyCollection<Guid> recipientIds, CancellationToken cancellationToken = default)
        {
            _sent.Enqueue(("ended", call.Id, call.Status, recipientIds.ToList()));
            return Task.CompletedTask;
        }
    }
}
