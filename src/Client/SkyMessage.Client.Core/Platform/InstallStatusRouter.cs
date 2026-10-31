namespace SkyMessage.Client.Core.Platform;

/// <summary>
/// Routes the statuses the system installer reports for install sessions (Android: PackageInstaller broadcasts).
/// <see cref="IAppUpdateInstaller.InstallAsync"/> waits a while for its session's first status and returns it. Every
/// other status belongs to <see cref="IAppUpdateInstaller.InstallStatusChanged"/>: the later ones, and any that arrive
/// after that call stopped waiting, so the outcome of a slow install (a silent one on a busy phone) is never lost.
/// </summary>
public sealed class InstallStatusRouter
{
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<AppInstallResult>> _waiting = [];

    /// <summary>Starts waiting for a session's first status; call it before the commit (the status may come at once).</summary>
    public FirstStatus Expect(int sessionId)
    {
        var waiter = new TaskCompletionSource<AppInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waiting[sessionId] = waiter;
        return new FirstStatus(this, sessionId, waiter);
    }

    /// <summary>A status arrived: true when the waiting install call took it, false when it is for the event.</summary>
    public bool TryHandToWaiter(int sessionId, AppInstallResult result)
    {
        lock (_gate)
        {
            if (!_waiting.Remove(sessionId, out var waiter)) return false;
            // Completed under the lock: a wait that gives up at the same moment then finds the result.
            waiter.TrySetResult(result);
            return true;
        }
    }

    /// <summary>Stops waiting; true when nothing had arrived yet (later statuses then go to the event).</summary>
    /// <remarks>False means the status was handed over meanwhile, which completes the wait under the same lock.</remarks>
    private bool StopWaiting(int sessionId, TaskCompletionSource<AppInstallResult> waiter)
    {
        lock (_gate)
        {
            if (!_waiting.TryGetValue(sessionId, out var current) || current != waiter) return false;
            _waiting.Remove(sessionId);
            return true;
        }
    }

    /// <summary>The wait for one session's first status.</summary>
    public sealed class FirstStatus
    {
        private readonly InstallStatusRouter _router;
        private readonly TaskCompletionSource<AppInstallResult> _waiter;

        internal FirstStatus(InstallStatusRouter router, int sessionId, TaskCompletionSource<AppInstallResult> waiter)
        {
            _router = router;
            SessionId = sessionId;
            _waiter = waiter;
        }

        public int SessionId { get; }

        /// <summary>
        /// The first status within <paramref name="timeout"/>, or null when none came (or the wait was cancelled): the
        /// install goes on and its outcome arrives as an event.
        /// </summary>
        public async Task<AppInstallResult?> WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
        {
            try
            {
                return await _waiter.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            if (_router.StopWaiting(SessionId, _waiter) || !_waiter.Task.IsCompleted) return null;
            // It arrived just as the wait gave up.
            return await _waiter.Task.ConfigureAwait(false);
        }

        /// <summary>The session was never committed: nothing will report on it.</summary>
        public void Abandon() => _router.StopWaiting(SessionId, _waiter);
    }
}
