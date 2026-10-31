using System.Text.Json;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>
/// Call audio on phones: when the screen may go dark at the ear (proximity lock) and when music in other apps pauses
/// (audio focus) — the decisions the Android host applies.
/// </summary>
public sealed class CallAudioTests
{
    // ---- proximity lock ----

    [Fact]
    public void The_screen_goes_dark_only_with_the_call_at_the_ear()
    {
        CallAudioPolicy.HoldsProximityLock(AudioRouteKind.Earpiece, cameraOn: false, inCommunication: true).ShouldBeTrue();
    }

    [Theory]
    [InlineData(AudioRouteKind.Speaker)]
    [InlineData(AudioRouteKind.Bluetooth)]
    [InlineData(AudioRouteKind.Wired)]
    public void Speaker_and_headsets_keep_the_screen_on(AudioRouteKind route)
    {
        CallAudioPolicy.HoldsProximityLock(route, cameraOn: false, inCommunication: true).ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_route_keeps_the_screen_on()
    {
        CallAudioPolicy.HoldsProximityLock(null, cameraOn: false, inCommunication: true).ShouldBeFalse();
    }

    [Fact]
    public void My_camera_on_keeps_the_screen_on_even_on_the_earpiece()
    {
        CallAudioPolicy.HoldsProximityLock(AudioRouteKind.Earpiece, cameraOn: true, inCommunication: true).ShouldBeFalse();
    }

    [Fact]
    public void Voice_played_like_media_keeps_the_screen_on_whatever_the_call_route_says()
    {
        // No microphone open yet (a call started muted): the WebView plays the voice on the loudspeaker.
        CallAudioPolicy.HoldsProximityLock(AudioRouteKind.Earpiece, cameraOn: false, inCommunication: false).ShouldBeFalse();
    }

    [Fact]
    public void Switching_to_the_speaker_and_back_drops_and_takes_the_lock_again()
    {
        var timeline = new[] { AudioRouteKind.Earpiece, AudioRouteKind.Speaker, AudioRouteKind.Earpiece, AudioRouteKind.Bluetooth };
        timeline.Select(r => CallAudioPolicy.HoldsProximityLock(r, cameraOn: false, inCommunication: true))
            .ShouldBe([true, false, true, false]);
    }

    // ---- audio focus: the decision ----

    [Fact]
    public void Nothing_pauses_other_apps_when_the_setting_is_off()
    {
        AudioFocusPolicy.Decide(false, Session(CallPhase.Active), Session(CallPhase.IncomingRinging), callsRing: true, recording: true)
            .ShouldBe(AudioFocusKind.None);
    }

    [Theory]
    [InlineData(CallPhase.OutgoingRinging)]
    [InlineData(CallPhase.Connecting)]
    [InlineData(CallPhase.Active)]
    [InlineData(CallPhase.Reconnecting)]
    [InlineData(CallPhase.Ended)]
    public void A_call_of_mine_holds_the_focus_from_dialing_to_its_end_screen(CallPhase phase)
    {
        AudioFocusPolicy.Decide(true, Session(phase), null, callsRing: true, recording: false).ShouldBe(AudioFocusKind.Call);
    }

    [Fact]
    public void A_ringing_call_pauses_music_only_when_calls_ring()
    {
        AudioFocusPolicy.Decide(true, null, Session(CallPhase.IncomingRinging), callsRing: true, recording: false).ShouldBe(AudioFocusKind.Ringtone);
        AudioFocusPolicy.Decide(true, null, Session(CallPhase.IncomingRinging), callsRing: false, recording: false).ShouldBe(AudioFocusKind.None);
    }

    [Fact]
    public void A_call_wins_over_a_second_ringing_call_and_a_ringing_call_over_a_recording()
    {
        AudioFocusPolicy.Decide(true, Session(CallPhase.Active), Session(CallPhase.IncomingRinging), callsRing: true, recording: true)
            .ShouldBe(AudioFocusKind.Call);
        AudioFocusPolicy.Decide(true, null, Session(CallPhase.IncomingRinging), callsRing: true, recording: true).ShouldBe(AudioFocusKind.Ringtone);
    }

    [Fact]
    public void Recording_a_note_pauses_music()
    {
        AudioFocusPolicy.Decide(true, null, null, callsRing: true, recording: true).ShouldBe(AudioFocusKind.Recording);
        AudioFocusPolicy.Decide(true, null, null, callsRing: true, recording: false).ShouldBe(AudioFocusKind.None);
    }

    // ---- audio focus: the coordinator ----

    [Fact]
    public void A_call_takes_the_focus_and_gives_it_back_when_it_is_gone()
    {
        var h = new FocusHarness();
        h.SetCall(Session(CallPhase.OutgoingRinging));
        h.SetCall(Session(CallPhase.Active));
        h.SetCall(Session(CallPhase.Ended));
        h.SetCall(null);

        h.Platform.Requests.ShouldBe([AudioFocusKind.Call, AudioFocusKind.None], "only changes reach the platform");
    }

    [Fact]
    public void Answering_a_ringing_call_moves_from_ringtone_to_call_without_letting_music_play_in_between()
    {
        var h = new FocusHarness();
        h.SetIncoming(Session(CallPhase.IncomingRinging));
        h.Calls.Current.Returns(Session(CallPhase.Connecting));
        h.Calls.Incoming.Returns((CallSession?)null);
        h.Calls.Changed += Raise.Event<Action>();
        h.SetCall(null);

        h.Platform.Requests.ShouldBe([AudioFocusKind.Ringtone, AudioFocusKind.Call, AudioFocusKind.None]);
    }

    [Fact]
    public void A_recording_holds_the_focus_until_its_handle_is_disposed_even_twice()
    {
        var h = new FocusHarness();
        var first = h.Coordinator.HoldForRecording();
        var second = h.Coordinator.HoldForRecording();
        first.Dispose();
        first.Dispose();
        h.Coordinator.Held.ShouldBe(AudioFocusKind.Recording, "the second recording still runs");
        second.Dispose();

        h.Platform.Requests.ShouldBe([AudioFocusKind.Recording, AudioFocusKind.None]);
    }

    [Fact]
    public void Turning_the_setting_off_gives_the_focus_back_at_once()
    {
        var h = new FocusHarness();
        h.SetCall(Session(CallPhase.Active));
        h.Settings.Current.Returns(new AppSettings { Devices = new DeviceSettings { PauseOtherAudio = false } });
        h.Settings.Changed += Raise.Event<Action>();

        h.Platform.Requests.ShouldBe([AudioFocusKind.Call, AudioFocusKind.None]);
    }

    [Fact]
    public void Where_other_apps_cannot_be_paused_nothing_is_asked()
    {
        var h = new FocusHarness(supported: false);
        h.SetCall(Session(CallPhase.Active));
        using (h.Coordinator.HoldForRecording())
        {
        }

        h.Coordinator.IsSupported.ShouldBeFalse();
        h.Platform.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void A_failing_platform_does_not_break_the_call_flow()
    {
        var h = new FocusHarness();
        h.Platform.Throw = true;
        Should.NotThrow(() => h.SetCall(Session(CallPhase.Active)));
        h.Coordinator.Held.ShouldBe(AudioFocusKind.Call);
    }

    // ---- the setting ----

    [Fact]
    public void Pausing_music_is_on_for_settings_saved_before_it_existed_and_an_off_choice_is_kept()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("""{"devices":{"echoCancellation":true}}""", SkyJson.Options)!;
        old.Devices.PauseOtherAudio.ShouldBeTrue();

        var off = new AppSettings { Devices = new DeviceSettings { PauseOtherAudio = false } };
        off.Clone().Devices.PauseOtherAudio.ShouldBeFalse();
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(off, SkyJson.Options), SkyJson.Options)!.Devices.PauseOtherAudio.ShouldBeFalse();
    }

    private static CallSession Session(CallPhase phase) => new()
    {
        Call = new CallDto { Id = Guid.CreateVersion7(), ChatId = Guid.CreateVersion7(), Kind = CallKind.Private },
        Phase = phase,
    };

    private sealed class FakeFocus(bool supported) : IAudioFocusService
    {
        public List<AudioFocusKind> Requests { get; } = [];
        public bool Throw { get; set; }
        public bool IsSupported => supported;

        public void SetFocus(AudioFocusKind kind)
        {
            Requests.Add(kind);
            if (Throw) throw new InvalidOperationException("audio service gone");
        }
    }

    private sealed class FocusHarness
    {
        public FocusHarness(bool supported = true)
        {
            Platform = new FakeFocus(supported);
            Calls = Substitute.For<ICallService>();
            Settings = Fakes.Settings();
            Coordinator = new AudioFocusCoordinator(Platform, Calls, Settings, Log.For<AudioFocusCoordinator>());
        }

        public FakeFocus Platform { get; }
        public ICallService Calls { get; }
        public ISettingsService Settings { get; }
        public AudioFocusCoordinator Coordinator { get; }

        public void SetCall(CallSession? session)
        {
            Calls.Current.Returns(session);
            Calls.Changed += Raise.Event<Action>();
        }

        public void SetIncoming(CallSession? session)
        {
            Calls.Incoming.Returns(session);
            Calls.Changed += Raise.Event<Action>();
        }
    }
}
