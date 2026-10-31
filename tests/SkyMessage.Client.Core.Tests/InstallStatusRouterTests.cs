using Shouldly;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class InstallStatusRouterTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);
    private static readonly AppInstallResult Pending = new(AppInstallStatus.Pending);
    private static readonly AppInstallResult Conflict = new(AppInstallStatus.Failed, AppUpdateErrors.Conflict);

    private readonly InstallStatusRouter _router = new();

    [Fact]
    public async Task The_first_status_goes_to_the_install_call_and_later_ones_to_the_event()
    {
        var first = _router.Expect(7);
        var waiting = first.WaitAsync(Long, TimeProvider.System, default);

        _router.TryHandToWaiter(7, Pending).ShouldBeTrue();
        (await waiting).ShouldBe(Pending);
        _router.TryHandToWaiter(7, new AppInstallResult(AppInstallStatus.Succeeded)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_status_that_arrives_before_the_wait_starts_is_kept_for_it()
    {
        var first = _router.Expect(7);
        _router.TryHandToWaiter(7, Conflict).ShouldBeTrue();

        (await first.WaitAsync(Long, TimeProvider.System, default)).ShouldBe(Conflict);
    }

    [Fact]
    public async Task A_status_after_the_wait_gave_up_goes_to_the_event()
    {
        var first = _router.Expect(7);

        (await first.WaitAsync(TimeSpan.FromMilliseconds(30), TimeProvider.System, default)).ShouldBeNull();

        // A slow silent install that fails after the timeout: Core must hear about it.
        _router.TryHandToWaiter(7, Conflict).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_wait_stops_waiting_too()
    {
        var first = _router.Expect(7);
        using var cts = new CancellationTokenSource();
        var waiting = first.WaitAsync(Long, TimeProvider.System, cts.Token);

        await cts.CancelAsync();

        (await waiting).ShouldBeNull();
        _router.TryHandToWaiter(7, Conflict).ShouldBeFalse();
    }

    [Fact]
    public void Statuses_of_unknown_or_abandoned_sessions_go_to_the_event()
    {
        _router.TryHandToWaiter(3, Pending).ShouldBeFalse();

        var first = _router.Expect(7);
        first.Abandon();
        _router.TryHandToWaiter(7, Pending).ShouldBeFalse();
    }

    [Fact]
    public async Task Sessions_are_routed_independently()
    {
        var a = _router.Expect(1);
        var b = _router.Expect(2);
        (await a.WaitAsync(TimeSpan.FromMilliseconds(30), TimeProvider.System, default)).ShouldBeNull();

        _router.TryHandToWaiter(1, Conflict).ShouldBeFalse();
        _router.TryHandToWaiter(2, Pending).ShouldBeTrue();
        (await b.WaitAsync(Long, TimeProvider.System, default)).ShouldBe(Pending);
    }
}
