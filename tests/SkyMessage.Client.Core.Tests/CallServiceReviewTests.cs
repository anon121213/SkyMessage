using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>Call-state races: a start or an accept still on its way, events overtaking responses, the ended linger.</summary>
public sealed class CallServiceReviewTests
{
    [Fact]
    public async Task Hanging_up_before_the_start_returns_hangs_up_the_call_it_created()
    {
        var h = new Harness();
        var started = new TaskCompletionSource<CallJoinInfoDto>();
        h.Api.StartCallAsync(Arg.Any<StartCallRequest>(), Arg.Any<CancellationToken>()).Returns(started.Task);
        var created = h.PrivateCall(CallStatus.Ringing, initiator: h.Me);

        var start = h.Calls.StartAsync(h.ChatId, video: false);
        await WaitUntilAsync(() => h.Calls.Current is not null, "dialing");
        await h.Calls.HangupAsync();
        h.Calls.Current!.EndStatus.ShouldBe(CallStatus.Cancelled);
        started.SetResult(Harness.JoinInfo(created));
        await start;

        await WaitUntilAsync(() => h.HangupCalls() > 0, "the created call is hung up");
        await h.Api.Received(1).HangupAsync(created.Id, Arg.Any<CancellationToken>());
        await h.Api.DidNotReceive().HangupAsync(Guid.Empty, Arg.Any<CancellationToken>());
        h.Calls.Current!.EndStatus.ShouldBe(CallStatus.Cancelled);
        h.Calls.Current.JoinInfo.ShouldBeNull("the cancelled call never connects");
    }

    [Fact]
    public async Task Calling_again_before_the_first_start_returns_takes_over_its_call()
    {
        var h = new Harness();
        var first = new TaskCompletionSource<CallJoinInfoDto>();
        var second = new TaskCompletionSource<CallJoinInfoDto>();
        h.Api.StartCallAsync(Arg.Any<StartCallRequest>(), Arg.Any<CancellationToken>()).Returns(first.Task, second.Task);
        var created = h.PrivateCall(CallStatus.Ringing, initiator: h.Me);

        var dial = h.Calls.StartAsync(h.ChatId, video: false);
        await WaitUntilAsync(() => h.Calls.Current is not null, "dialing");
        await h.Calls.HangupAsync();
        var redial = h.Calls.StartAsync(h.ChatId, video: false);
        await WaitUntilAsync(() => h.Calls.Current is { Phase: CallPhase.OutgoingRinging }, "dialing again");
        // The server keeps one unfinished call per chat: both starts get the same call.
        first.SetResult(Harness.JoinInfo(created));
        second.SetResult(Harness.JoinInfo(created));
        await Task.WhenAll(dial, redial);

        h.Calls.Current!.Call.Id.ShouldBe(created.Id);
        h.Calls.Current.Phase.ShouldBe(CallPhase.OutgoingRinging);
        await h.Api.DidNotReceive().HangupAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Calling_someone_else_before_the_misdialed_start_returns_keeps_the_new_call()
    {
        var h = new Harness();
        var otherChat = Guid.CreateVersion7();
        var first = new TaskCompletionSource<CallJoinInfoDto>();
        var second = new TaskCompletionSource<CallJoinInfoDto>();
        h.Api.StartCallAsync(Arg.Is<StartCallRequest>(r => r.ChatId == h.ChatId), Arg.Any<CancellationToken>()).Returns(first.Task);
        h.Api.StartCallAsync(Arg.Is<StartCallRequest>(r => r.ChatId == otherChat), Arg.Any<CancellationToken>()).Returns(second.Task);
        var misdialed = h.PrivateCall(CallStatus.Ringing, initiator: h.Me);
        var intended = misdialed with { Id = Guid.CreateVersion7(), ChatId = otherChat };

        var dial = h.Calls.StartAsync(h.ChatId, video: false);
        await WaitUntilAsync(() => h.Calls.Current is not null, "dialing");
        await h.Calls.HangupAsync();
        var redial = h.Calls.StartAsync(otherChat, video: false);
        await WaitUntilAsync(() => h.Calls.Current is { Phase: CallPhase.OutgoingRinging }, "dialing the right person");
        first.SetResult(Harness.JoinInfo(misdialed));
        await dial;

        h.Calls.Current!.Call.ChatId.ShouldBe(otherChat);
        h.Calls.Current.JoinInfo.ShouldBeNull("the new call still waits for its own start");
        await WaitUntilAsync(() => h.HangupCalls() > 0, "the misdialed call is hung up");
        await h.Api.Received(1).HangupAsync(misdialed.Id, Arg.Any<CancellationToken>());

        second.SetResult(Harness.JoinInfo(intended));
        await redial;
        h.Calls.Current!.Call.Id.ShouldBe(intended.Id);
        h.Calls.Current.JoinInfo.ShouldNotBeNull();
        await h.Api.DidNotReceive().HangupAsync(intended.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_lost_start_response_hangs_up_my_call_that_rings_for_nobody()
    {
        var h = new Harness();
        var created = h.PrivateCall(CallStatus.Ringing, initiator: h.Me);
        h.Api.StartCallAsync(Arg.Any<StartCallRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ApiException(0, ClientErrorCodes.Network));
        h.Api.GetActiveCallAsync(h.ChatId, Arg.Any<CancellationToken>()).Returns(created);

        await Should.ThrowAsync<ApiException>(() => h.Calls.StartAsync(h.ChatId, video: false));

        h.Calls.Current.ShouldBeNull();
        await WaitUntilAsync(() => h.HangupCalls() > 0, "the created call is hung up");
        await h.Api.Received(1).HangupAsync(created.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_lost_start_response_leaves_a_call_somebody_else_started_alone()
    {
        var h = new Harness();
        var theirs = h.PrivateCall(CallStatus.Ringing, initiator: h.Peer);
        h.Api.StartCallAsync(Arg.Any<StartCallRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ApiException(0, ClientErrorCodes.Network));
        h.Api.GetActiveCallAsync(h.ChatId, Arg.Any<CancellationToken>()).Returns(theirs);

        await Should.ThrowAsync<ApiException>(() => h.Calls.StartAsync(h.ChatId, video: false));

        await WaitUntilAsync(() => h.Api.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ISkyApi.GetActiveCallAsync)), "looked up");
        await Task.Delay(50);
        h.HangupCalls().ShouldBe(0);
    }

    [Fact]
    public async Task An_answer_that_overtakes_the_start_response_is_kept()
    {
        var h = new Harness();
        var started = new TaskCompletionSource<CallJoinInfoDto>();
        h.Api.StartCallAsync(Arg.Any<StartCallRequest>(), Arg.Any<CancellationToken>()).Returns(started.Task);
        var ringing = h.PrivateCall(CallStatus.Ringing, initiator: h.Me);
        var answered = ringing with
        {
            Status = CallStatus.Active,
            Participants = [Participant(h.Me, CallParticipantStatus.Joined), Participant(h.Peer, CallParticipantStatus.Joined)],
        };

        var start = h.Calls.StartAsync(h.ChatId, video: false);
        await WaitUntilAsync(() => h.Calls.Current is not null, "dialing");
        // The server publishes before it answers the start; the callee answers before the response gets here.
        h.CallUpdated(ringing);
        h.CallUpdated(answered);
        started.SetResult(Harness.JoinInfo(ringing));
        await start;
        h.Calls.ReportMediaState(CallMediaState.Connected);

        var current = h.Calls.Current!;
        current.Call.Status.ShouldBe(CallStatus.Active);
        current.Phase.ShouldBe(CallPhase.Active, "no ringback once the callee answered");
        current.ConnectedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_busy_callee_that_overtakes_the_start_response_ends_the_call_as_busy()
    {
        var h = new Harness();
        var started = new TaskCompletionSource<CallJoinInfoDto>();
        h.Api.StartCallAsync(Arg.Any<StartCallRequest>(), Arg.Any<CancellationToken>()).Returns(started.Task);
        var ringing = h.PrivateCall(CallStatus.Ringing, initiator: h.Me);
        var phases = new List<(CallPhase Phase, CallStatus? EndStatus)>();
        h.Calls.Changed += () =>
        {
            if (h.Calls.Current is { } c) lock (phases) phases.Add((c.Phase, c.EndStatus));
        };

        var start = h.Calls.StartAsync(h.ChatId, video: false);
        await WaitUntilAsync(() => h.Calls.Current is not null, "dialing");
        // A call from someone else in this chat is not mine to pick up.
        h.CallUpdated(h.PrivateCall(CallStatus.Cancelled, initiator: h.Peer));
        h.CallUpdated(ringing with { Status = CallStatus.Busy });
        started.SetResult(Harness.JoinInfo(ringing));
        await start;

        var current = h.Calls.Current!;
        current.Phase.ShouldBe(CallPhase.Ended);
        current.EndStatus.ShouldBe(CallStatus.Busy);
        current.JoinInfo.ShouldBeNull("the room of a finished call is not joined");
        lock (phases) phases.ShouldNotContain((CallPhase.Ended, (CallStatus?)null), "the end is published with its status");
    }

    [Fact]
    public async Task Accepting_survives_call_updates_during_the_permission_prompt()
    {
        var h = new Harness(ChatType.Group);
        var member = Guid.CreateVersion7();
        var call = h.GroupCall(initiator: h.Peer,
            Participant(h.Peer, CallParticipantStatus.Joined), Participant(h.Me, CallParticipantStatus.Invited), Participant(member, CallParticipantStatus.Invited));
        h.IncomingCall(call);
        await WaitUntilAsync(() => h.Calls.Incoming is not null, "ringing");
        var prompt = new TaskCompletionSource<PermissionState>();
        h.Permissions.RequestAsync(AppPermission.Microphone).Returns(prompt.Task);
        var joined = call with
        {
            Participants = [Participant(h.Peer, CallParticipantStatus.Joined), Participant(h.Me, CallParticipantStatus.Invited), Participant(member, CallParticipantStatus.Joined)],
        };
        h.Api.AcceptCallAsync(call.Id, Arg.Any<CancellationToken>()).Returns(Harness.JoinInfo(joined));

        var accept = h.Calls.AcceptAsync(withVideo: false);
        h.CallUpdated(joined);
        prompt.SetResult(PermissionState.Granted);
        await accept;

        await h.Api.Received(1).AcceptCallAsync(call.Id, Arg.Any<CancellationToken>());
        h.Calls.Incoming.ShouldBeNull();
        h.Calls.Current!.JoinInfo.ShouldNotBeNull();
        h.Calls.Current.Call.Participants.Count(p => p.Status == CallParticipantStatus.Joined).ShouldBe(2);
    }

    [Fact]
    public async Task A_network_failure_while_accepting_rings_again_instead_of_dropping_the_call()
    {
        var h = new Harness();
        var call = h.PrivateCall(CallStatus.Ringing, initiator: h.Peer);
        h.IncomingCall(call);
        await WaitUntilAsync(() => h.Calls.Incoming is not null, "ringing");
        h.Api.AcceptCallAsync(call.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new ApiException(0, ClientErrorCodes.Network));
        // Meanwhile the app went to background: the notification has to ring again.
        h.App.IsForeground.Returns(false);

        await Should.ThrowAsync<ApiException>(() => h.Calls.AcceptAsync(withVideo: false));

        h.Calls.Current.ShouldBeNull();
        h.Calls.Incoming.ShouldNotBeNull().Call.Id.ShouldBe(call.Id);
        h.Calls.Incoming!.Phase.ShouldBe(CallPhase.IncomingRinging);
        await h.Notifications.Received(1).ShowIncomingCallAsync(Arg.Is<CallDto>(c => c.Id == call.Id), Arg.Any<string>());

        h.App.IsForeground.Returns(true);
        h.Api.AcceptCallAsync(call.Id, Arg.Any<CancellationToken>()).Returns(Harness.JoinInfo(call with { Status = CallStatus.Active }));
        await h.Calls.AcceptAsync(withVideo: false);
        h.Calls.Incoming.ShouldBeNull();
        h.Calls.Current!.JoinInfo.ShouldNotBeNull();
    }

    [Fact]
    public async Task Media_reports_after_the_end_do_not_keep_the_ended_call_on_screen()
    {
        var h = new Harness();
        var call = h.PrivateCall(CallStatus.Active, initiator: h.Me);
        h.Api.StartCallAsync(Arg.Any<StartCallRequest>(), Arg.Any<CancellationToken>()).Returns(Harness.JoinInfo(call));
        await h.Calls.StartAsync(h.ChatId, video: false);
        h.Calls.ReportMediaState(CallMediaState.Connected);
        h.Calls.ReportLocalMedia(micMuted: false, cameraOn: false, screenSharing: false, AudioRouteKind.Speaker);

        h.CallUpdated(call with { Status = CallStatus.Ended });
        h.Calls.Current!.Phase.ShouldBe(CallPhase.Ended);
        // Android resets the audio route as the call ends and the overlay reports it; the engine may report late too.
        h.Calls.ReportLocalMedia(micMuted: false, cameraOn: false, screenSharing: false, AudioRouteKind.Earpiece);
        h.Calls.ReportParticipants([new CallParticipantState(h.Peer, false, false, false, 3)]);
        h.Calls.Current!.AudioRoute.ShouldBe(AudioRouteKind.Speaker);

        h.Time.FireAll();
        await WaitUntilAsync(() => h.Calls.Current is null, "the ended call clears after its linger");
    }

    [Fact]
    public async Task A_call_from_a_chat_that_cannot_be_fetched_still_rings()
    {
        var h = new Harness();
        var newChat = Guid.CreateVersion7();
        var call = h.PrivateCall(CallStatus.Ringing, initiator: h.Peer) with { ChatId = newChat };
        h.Chats.GetOrFetchAsync(newChat, Arg.Any<CancellationToken>()).ThrowsAsync(new ApiException(0, ClientErrorCodes.Network));
        h.Users.DisplayNameOf(h.Peer).Returns("Mom");
        h.App.IsForeground.Returns(false);

        h.IncomingCall(call);

        await WaitUntilAsync(() => h.Calls.Incoming is not null, "ringing although the chat lookup failed");
        h.Calls.Incoming!.Call.Id.ShouldBe(call.Id);
        h.Calls.Incoming.Phase.ShouldBe(CallPhase.IncomingRinging);
        await WaitUntilAsync(() => Harness.CallsTo(h.Notifications, nameof(ILocalNotificationService.ShowIncomingCallAsync)) > 0, "the notification rings");
        await h.Notifications.Received(1).ShowIncomingCallAsync(Arg.Is<CallDto>(c => c.Id == call.Id), "Mom");
    }

    [Fact]
    public async Task A_slow_lookup_of_a_new_group_does_not_hold_up_ringing()
    {
        var h = new Harness();
        var group = new ChatSummaryDto { Id = Guid.CreateVersion7(), Type = ChatType.Group, Title = "Cousins" };
        var call = h.GroupCall(initiator: h.Peer, Participant(h.Peer, CallParticipantStatus.Joined), Participant(h.Me, CallParticipantStatus.Ringing))
            with { ChatId = group.Id };
        var fetched = new TaskCompletionSource<ChatSummaryDto?>();
        h.Chats.GetOrFetchAsync(group.Id, Arg.Any<CancellationToken>()).Returns(fetched.Task);
        h.Chats.TitleOf(Arg.Any<ChatSummaryDto>()).Returns(c => c.Arg<ChatSummaryDto>().Title);
        h.Users.DisplayNameOf(h.Peer).Returns("Uncle Vanya");
        h.App.IsForeground.Returns(false);

        h.IncomingCall(call);

        h.Calls.Incoming.ShouldNotBeNull("the call screen does not wait for the network");
        Harness.CallsTo(h.Notifications, nameof(ILocalNotificationService.ShowIncomingCallAsync)).ShouldBe(0, "the notification waits a moment for a name");
        // The lookup wait runs out (the ring expiry does not): the notification rings, named after who calls.
        h.Time.Fire(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => Harness.CallsTo(h.Notifications, nameof(ILocalNotificationService.ShowIncomingCallAsync)) > 0, "the notification rings");
        await h.Notifications.Received(1).ShowIncomingCallAsync(Arg.Is<CallDto>(c => c.Id == call.Id), "Uncle Vanya");
        h.Calls.Incoming.ShouldNotBeNull();

        fetched.SetResult(group);
        await WaitUntilAsync(() => h.Calls.Incoming?.Title == "Cousins", "the ringing call is named once its chat arrives");
        Harness.CallsTo(h.Notifications, nameof(ILocalNotificationService.ShowIncomingCallAsync)).ShouldBe(1);
    }

    [Fact]
    public async Task A_decline_from_the_notification_before_the_session_is_restored_is_sent_once_it_is()
    {
        var h = new Harness();
        var callId = Guid.CreateVersion7();

        // Cold start from the notification: the platform reports the button before Core restored the session.
        h.CallAction(callId, CallNotificationAction.Decline);
        await Task.Delay(50);
        Harness.CallsTo(h.Api, nameof(ISkyApi.DeclineCallAsync)).ShouldBe(0, "the API refuses requests until the session is restored");

        await h.SignInAsync();

        await WaitUntilAsync(() => Harness.CallsTo(h.Api, nameof(ISkyApi.DeclineCallAsync)) > 0, "declined once signed in");
        await h.Api.Received(1).DeclineCallAsync(callId, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_accept_from_the_notification_before_the_session_is_restored_answers_once_it_is()
    {
        var h = new Harness();
        var call = h.PrivateCall(CallStatus.Ringing, initiator: h.Peer);
        h.Api.GetCallAsync(call.Id, Arg.Any<CancellationToken>()).Returns(call);
        h.Api.AcceptCallAsync(call.Id, Arg.Any<CancellationToken>()).Returns(Harness.JoinInfo(call with { Status = CallStatus.Active }));

        h.CallAction(call.Id, CallNotificationAction.Accept);
        await Task.Delay(50);
        Harness.CallsTo(h.Api, nameof(ISkyApi.GetCallAsync)).ShouldBe(0, "the API refuses requests until the session is restored");

        await h.SignInAsync();

        await WaitUntilAsync(() => h.Calls.Current?.JoinInfo is not null, "answered once signed in");
        await h.Api.Received(1).AcceptCallAsync(call.Id, Arg.Any<CancellationToken>());
        h.Calls.Incoming.ShouldBeNull();
    }

    [Fact]
    public async Task A_notification_action_while_nobody_signs_in_is_given_up()
    {
        var h = new Harness();

        h.CallAction(Guid.CreateVersion7(), CallNotificationAction.Decline);
        h.Time.FireAll();
        await h.SignInAsync();
        await Task.Delay(50);

        Harness.CallsTo(h.Api, nameof(ISkyApi.DeclineCallAsync)).ShouldBe(0, "a button nobody could act on is dropped, not replayed later");
    }

    private static CallParticipantDto Participant(Guid userId, CallParticipantStatus status) => new() { UserId = userId, Status = status };

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time: " + because);
            await Task.Delay(10);
        }
    }

    private sealed class Harness
    {
        public Guid Me { get; } = Guid.CreateVersion7();
        public Guid Peer { get; } = Guid.CreateVersion7();
        public Guid ChatId { get; } = Guid.CreateVersion7();
        public ISkyApi Api { get; } = Substitute.For<ISkyApi>();
        public IRealtimeService Realtime { get; } = Substitute.For<IRealtimeService>();
        public IPermissionService Permissions { get; } = Substitute.For<IPermissionService>();
        public ILocalNotificationService Notifications { get; } = Substitute.For<ILocalNotificationService>();
        public IAppLifecycle App { get; } = Substitute.For<IAppLifecycle>();
        public IChatListStore Chats { get; } = Substitute.For<IChatListStore>();
        public IUserStore Users { get; } = Substitute.For<IUserStore>();
        public ManualTimers Time { get; } = new();
        public AccountLifecycle Lifecycle { get; }
        public CallService Calls { get; }

        public Harness(ChatType chatType = ChatType.Private)
        {
            var auth = Substitute.For<IAuthService>();
            auth.UserId.Returns(Me);
            Permissions.RequestAsync(Arg.Any<AppPermission>()).Returns(PermissionState.Granted);
            App.IsForeground.Returns(true);
            Chats.Get(ChatId).Returns(new ChatSummaryDto
            {
                Id = ChatId,
                Type = chatType,
                Title = "Family",
                Peer = chatType == ChatType.Private ? new UserSummaryDto { Id = Peer } : null,
            });
            var settings = Substitute.For<ISettingsService>();
            settings.Current.Returns(new AppSettings());
            var endpoints = Substitute.For<IServerEndpointService>();
            endpoints.ResolveLiveKitUrl(Arg.Any<string>()).Returns(c => "wss://sky.test" + c.Arg<string>());
            var mediaFiles = Substitute.For<IMediaFileStore>();
            var cache = new LocalCache(Substitute.For<ILocalCacheStore>(), NullLogger<LocalCache>.Instance);
            Lifecycle = new AccountLifecycle(cache, mediaFiles, NullLogger<AccountLifecycle>.Instance);
            Calls = new CallService(Api, Realtime, auth, Chats, Users, Permissions, Substitute.For<ICallPlatformService>(),
                Notifications, App, endpoints, settings, Lifecycle, Time, NullLogger<CallService>.Instance);
        }

        /// <summary>The session is restored (or signed in): the account's stores, calls among them, become active.</summary>
        public Task SignInAsync() => Lifecycle.ActivateAsync(Me);

        public CallDto PrivateCall(CallStatus status, Guid initiator)
        {
            var callee = initiator == Me ? Peer : Me;
            return new CallDto
            {
                Id = Guid.CreateVersion7(),
                ChatId = ChatId,
                Kind = CallKind.Private,
                Status = status,
                InitiatorId = initiator,
                Participants = [Participant(initiator, CallParticipantStatus.Joined), Participant(callee, CallParticipantStatus.Ringing)],
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }

        public CallDto GroupCall(Guid initiator, params CallParticipantDto[] participants) => new()
        {
            Id = Guid.CreateVersion7(),
            ChatId = ChatId,
            Kind = CallKind.Group,
            Status = CallStatus.Active,
            InitiatorId = initiator,
            Participants = participants,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        public static CallJoinInfoDto JoinInfo(CallDto call) => new() { Call = call, LiveKitUrl = "/livekit", LiveKitToken = "token-" + call.Id };

        public void CallUpdated(CallDto call) => Realtime.CallUpdated += Raise.Event<Action<CallDto>>(call);

        public void IncomingCall(CallDto call) => Realtime.IncomingCall += Raise.Event<Action<CallDto>>(call);

        /// <summary>A button on an incoming-call notification, as the platform reports it.</summary>
        public void CallAction(Guid callId, CallNotificationAction action) =>
            Notifications.CallActionInvoked += Raise.Event<Action<Guid, CallNotificationAction>>(callId, action);

        public int HangupCalls() => Api.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISkyApi.HangupAsync));

        public static int CallsTo(object substitute, string method) => substitute.ReceivedCalls().Count(c => c.GetMethodInfo().Name == method);
    }

    /// <summary>Delays (the linger of an ended call, the ring expiry, waits) wait until the test fires them.</summary>
    private sealed class ManualTimers : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State, TimeSpan Due)> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers) _timers.Add((callback, state, dueTime));
            return new Handle();
        }

        public void FireAll() => Fire(TimeSpan.MaxValue);

        /// <summary>Fires the timers that are due within <paramref name="elapsed"/>; later ones keep waiting.</summary>
        public void Fire(TimeSpan elapsed)
        {
            List<(TimerCallback Callback, object? State, TimeSpan Due)> due;
            lock (_timers)
            {
                due = _timers.Where(t => t.Due <= elapsed).ToList();
                _timers.RemoveAll(t => t.Due <= elapsed);
            }
            foreach (var (callback, state, _) in due) callback(state);
        }

        private sealed class Handle : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
