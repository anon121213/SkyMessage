using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using SkyMessage.Application.Calls;
using SkyMessage.Infrastructure.Calls;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>
/// Extra API hosts over the shared test database with calls-specific configuration. Realtime events of a host reach
/// only hub connections of that same host, so a test that uses one talks to it exclusively (users may be
/// registered anywhere, the database is shared).
/// </summary>
internal static class CallHosts
{
    public static WebApplicationFactory<Program> WithSettings(this ApiFactory api, IReadOnlyDictionary<string, string?> settings,
        Action<IServiceCollection>? services = null) =>
        api.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            if (services is not null) b.ConfigureTestServices(services);
        });

    /// <summary>A host whose LiveKit (Twirp + /livekit proxy upstream) is <paramref name="liveKit"/>.</summary>
    public static WebApplicationFactory<Program> WithFakeLiveKit(this ApiFactory api, FakeLiveKit liveKit, Action<IServiceCollection>? services = null) =>
        api.WithSettings(new Dictionary<string, string?> { ["LiveKit:InternalUrl"] = liveKit.Url }, services);

    /// <summary>
    /// The real <see cref="CallGraceScheduler"/>, but on a <see cref="ManualTimeProvider"/>, and every grace check
    /// reported to <paramref name="checks"/> when it finished.
    /// </summary>
    public static void UseManualGraceClock(this IServiceCollection services, ManualTimeProvider time, GraceChecks checks)
    {
        services.AddScoped<ICallGraceHandler>(sp => new RecordingGraceHandler(sp.GetRequiredService<CallLifecycle>(), checks));
        services.AddSingleton<ICallGraceScheduler>(sp => new CallGraceScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            time,
            sp.GetRequiredService<ILogger<CallGraceScheduler>>()));
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> that only moves when told to; timers (and so <c>Task.Delay(…, provider)</c>) fire
/// inside <see cref="Advance"/>.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>How far in the future each armed timer fires.</summary>
    public IReadOnlyList<TimeSpan> PendingDelays
    {
        get
        {
            lock (_gate) return _timers.Where(t => t.DueAt is not null).Select(t => t.DueAt!.Value - _now).Order().ToList();
        }
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt <= _now).OrderBy(t => t.DueAt).ToList();
            foreach (var timer in due) timer.Rearm(_now);
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    owner._timers.Remove(this);
                }
                else
                {
                    DueAt = owner._now + dueTime;
                    if (!owner._timers.Contains(this)) owner._timers.Add(this);
                }
            }
            return true;
        }

        /// <summary>Called under the owner's lock when the timer fires.</summary>
        public void Rearm(DateTimeOffset now)
        {
            if (_period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
            else
            {
                DueAt = now + _period;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record GraceCheck(Guid CallId, Guid UserId, TimeSpan? Retry, Exception? Error);

/// <summary>Grace checks that ran to completion (the scheduler runs them in the background).</summary>
internal sealed class GraceChecks
{
    private readonly Lock _gate = new();
    private readonly List<GraceCheck> _done = [];
    private readonly List<(Func<GraceCheck, bool> Match, TaskCompletionSource<GraceCheck> Done)> _waiters = [];

    public IReadOnlyList<GraceCheck> Completed
    {
        get
        {
            lock (_gate) return _done.ToList();
        }
    }

    public void Add(GraceCheck check)
    {
        lock (_gate)
        {
            _done.Add(check);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Match(check)) continue;
                _waiters[i].Done.TrySetResult(check);
                _waiters.RemoveAt(i);
            }
        }
    }

    /// <summary>The <paramref name="nth"/> completed check of this call and user.</summary>
    public async Task<GraceCheck> WaitAsync(Guid callId, Guid userId, int nth = 1)
    {
        Task<GraceCheck> pending;
        lock (_gate)
        {
            var seen = _done.Where(c => c.CallId == callId && c.UserId == userId).ToList();
            if (seen.Count >= nth) return Checked(seen[nth - 1]);
            var count = seen.Count;
            var done = new TaskCompletionSource<GraceCheck>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((c => c.CallId == callId && c.UserId == userId && ++count == nth, done));
            pending = done.Task;
        }
        try
        {
            return Checked(await pending.WaitAsync(CallKit.EventTimeout, CallKit.Ct));
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException($"Grace check #{nth} of call {callId} / user {userId} did not complete; completed: {Completed.Count}");
        }
    }

    private static GraceCheck Checked(GraceCheck check) =>
        check.Error is null ? check : throw new ShouldAssertException($"Grace check failed: {check.Error}");
}

internal sealed class RecordingGraceHandler(ICallGraceHandler inner, GraceChecks checks) : ICallGraceHandler
{
    public async Task<TimeSpan?> RunGraceCheckAsync(Guid callId, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var retry = await inner.RunGraceCheckAsync(callId, userId, cancellationToken);
            checks.Add(new GraceCheck(callId, userId, retry, null));
            return retry;
        }
        catch (Exception ex)
        {
            checks.Add(new GraceCheck(callId, userId, null, ex));
            throw;
        }
    }
}
