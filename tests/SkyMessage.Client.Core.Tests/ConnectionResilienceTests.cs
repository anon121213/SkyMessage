using System.Net;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

/// <summary>Timestamps under test control; periodic timers fire once per simulated second when the clock advances.</summary>
internal sealed class SteppedTime : TimeProvider
{
    private readonly List<(TimerCallback Callback, object? State)> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < by; elapsed += TimeSpan.FromSeconds(1))
        {
            Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond);
            foreach (var (callback, state) in _timers.ToList()) callback(state);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _timers.Add((callback, state));
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class ConnectionResilienceTests
{
    [Fact]
    public async Task A_connection_that_cannot_be_built_does_not_end_the_realtime_loop()
    {
        var endpoints = Substitute.For<IServerEndpointService>();
        endpoints.Resolve(Arg.Any<string>()).Returns(new Uri("http://127.0.0.1:9/hubs/realtime"));
        var tokens = Substitute.For<ITokenSource>();
        tokens.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("token");
        var handlers = Substitute.For<IHttpHandlerFactory>();
        handlers.SupportsProxy.Returns(true);
        var builds = 0;
        // A SOCKS5 host typed as "1.2.3.4:1080": the platform cannot turn it into a proxy URI.
        handlers.CreateProxy(Arg.Any<ConnectionSettings>()).Returns(_ =>
        {
            Interlocked.Increment(ref builds);
            throw new UriFormatException("Invalid URI: The hostname could not be parsed.");
        });
        var app = Substitute.For<IAppLifecycle>();
        app.IsForeground.Returns(true);
        var realtime = new RealtimeService(endpoints, Substitute.For<IEndpointRouting>(), tokens, handlers, Fakes.Settings(), app, Fakes.Network(),
            new InstantTime(), Log.For<RealtimeService>());

        try
        {
            await realtime.StartAsync();

            await Wait.UntilAsync(() => Volatile.Read(ref builds) >= 3, "the loop keeps retrying, so a corrected proxy takes effect");
            realtime.State.ShouldNotBe(ConnectionState.Connected);
        }
        finally
        {
            await realtime.StopAsync();
        }
    }

    [Fact]
    public async Task A_retry_after_the_server_address_changed_sends_the_token_only_once_the_new_address_is_verified()
    {
        var oldBase = new Uri("https://old.test/");
        var newBase = new Uri("https://new.test/");
        var active = oldBase;
        var routing = Substitute.For<IEndpointRouting>();
        routing.BaseUri.Returns(_ => active);
        routing.FailoverFromAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>()).Returns(true);
        // The address typed meanwhile turns out to serve another deployment: the identity check signs out instead.
        var check = new TaskCompletionSource();
        routing.EnsureTrustedAsync(Arg.Any<CancellationToken>()).Returns(check.Task);
        var sent = new System.Collections.Concurrent.ConcurrentQueue<(string Host, string? Token)>();
        var handler = new StubHandler(request =>
        {
            sent.Enqueue((request.RequestUri!.Host, request.Headers.Authorization?.Parameter));
            // The old server hangs until the request times out; the user enters a new server address meanwhile.
            active = newBase;
            throw new HttpRequestException("unreachable");
        });
        var factory = Substitute.For<IHttpHandlerFactory>();
        factory.CreateHandler(Arg.Any<ConnectionSettings>()).Returns(handler);
        var transport = new ApiTransport(new HttpClientProvider(factory, Fakes.Settings(), TimeProvider.System, Log.For<HttpClientProvider>()),
            routing, TimeProvider.System);

        var request = transport.SendAsync(ApiRequest.Get("api/chats"), "secret-token", CancellationToken.None);
        await Task.Delay(100);
        sent.Select(s => s.Host).ShouldBe(["old.test"], "the retry waits for the identity check of the new address");
        check.SetException(new ApiException(401, SkyMessage.Contracts.ErrorCodes.SessionRevoked));

        (await Should.ThrowAsync<ApiException>(request)).StatusCode.ShouldBe(401);
        sent.Select(s => s.Host).ShouldBe(["old.test"], "the token never reaches an address of another deployment");
    }

    [Fact]
    public void After_the_body_is_written_the_upload_watchdog_waits_for_its_tail_and_the_response()
    {
        var time = new SteppedTime();
        using var watchdog = new StallWatchdog(time, TransferDefaults.StallTimeout, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(10));
        watchdog.Touch();
        // The last slice was taken by the kernel's send buffer: much of the 1 MiB chunk may still be unsent.
        watchdog.Touch(TransferDefaults.BufferedUploadTimeout(1024 * 1024));
        time.Advance(TimeSpan.FromSeconds(40));

        watchdog.Stalled.ShouldBeFalse("a slow uplink still sending the tail is not a stall");
        time.Advance(TimeSpan.FromSeconds(60));
        watchdog.Stalled.ShouldBeTrue();
        watchdog.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Without_progress_the_upload_watchdog_still_trips_after_the_stall_timeout()
    {
        var time = new SteppedTime();
        using var watchdog = new StallWatchdog(time, TransferDefaults.StallTimeout, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(14));
        watchdog.Stalled.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(2));
        watchdog.Stalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Progress_content_reports_each_slice_then_the_written_body()
    {
        var events = new List<string>();
        using var content = new ProgressContent(new byte[40 * 1024], null, () => events.Add("slice"), () => events.Add("written"));

        await content.CopyToAsync(Stream.Null);

        events.ShouldBe(["slice", "slice", "slice", "written"]);
    }

    [Fact]
    public async Task Media_kept_by_the_app_proxy_and_a_lower_limit_both_trim_the_cache()
    {
        var files = Substitute.For<IMediaFileStore>();
        files.IsSupported.Returns(true);
        files.GetLocalPathAsync(Arg.Any<Guid>(), Arg.Any<bool>()).Returns((string?)null);
        files.SaveAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns("/cache/t.jpg");
        var appSettings = new AppSettings();
        appSettings.Data.CacheLimitMb = 200;
        var settings = Fakes.Settings(appSettings);
        int Trims() => files.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMediaFileStore.TrimAsync));
        var trimmer = Fakes.Trimmer(files, settings, new InstantTime());
        await Wait.UntilAsync(() => Trims() == 1, "a trim shortly after start");

        var source = MediaContentSourceTests.Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) },
            files, trimmer);
        await using (await source.OpenBodyAsync($"/_sm/media/{Guid.CreateVersion7()}/thumbnail", null))
        {
        }

        await Wait.UntilAsync(() => Trims() == 2, "a thumbnail written to the cache by the app proxy requests a trim");
        await files.Received(2).TrimAsync(200L * 1024 * 1024);

        appSettings.Data.CacheLimitMb = 100;
        settings.Changed += Raise.Event<Action>();
        await Wait.UntilAsync(() => Trims() == 3, "lowering the limit trims at once");
        await files.Received(1).TrimAsync(100L * 1024 * 1024);
    }
}
