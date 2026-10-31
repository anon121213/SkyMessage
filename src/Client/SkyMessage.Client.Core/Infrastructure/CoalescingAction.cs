using Microsoft.Extensions.Logging;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>
/// Runs an action at most once per <c>delay</c> window: the first <see cref="Request"/> schedules a run,
/// further requests before it runs are folded into it. The action reads the latest state when it runs, so
/// nothing is lost (used for debounced cache persistence). Timers are WASM-safe (Task.Delay).
/// </summary>
internal sealed class CoalescingAction
{
    private readonly Func<CancellationToken, Task> _action;
    private readonly TimeSpan _delay;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _lock = new();
    private bool _scheduled;
    private CancellationTokenSource _lifetime = new();

    public CoalescingAction(TimeSpan delay, Func<CancellationToken, Task> action, TimeProvider time, ILogger logger)
    {
        _delay = delay;
        _action = action;
        _time = time;
        _logger = logger;
    }

    public void Request()
    {
        CancellationToken token;
        lock (_lock)
        {
            if (_scheduled) return;
            _scheduled = true;
            token = _lifetime.Token;
        }
        RunLaterAsync(token).Forget(_logger);
    }

    /// <summary>Runs a pending request right away (e.g. before the app suspends).</summary>
    public Task FlushAsync()
    {
        CancellationToken token;
        lock (_lock)
        {
            if (!_scheduled) return Task.CompletedTask;
            token = _lifetime.Token;
        }
        return RunNowAsync(token);
    }

    /// <summary>Drops pending runs (sign-out: the state they would persist is gone).</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _scheduled = false;
            _lifetime.Cancel();
            _lifetime = new CancellationTokenSource();
        }
    }

    private async Task RunLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_delay, _time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await RunNowAsync(token).ConfigureAwait(false);
    }

    private async Task RunNowAsync(CancellationToken token)
    {
        await _runGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (!_scheduled || token.IsCancellationRequested) return;
                _scheduled = false;
            }
            await _action(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deferred operation failed");
        }
        finally
        {
            _runGate.Release();
        }
    }
}
