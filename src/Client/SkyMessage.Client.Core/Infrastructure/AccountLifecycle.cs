using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>Per-account state holder (stores). Registered with <see cref="AccountLifecycle"/> in its constructor.</summary>
internal interface IAccountScoped
{
    /// <summary>A session for <paramref name="userId"/> became active: restore local caches (no network).</summary>
    Task OnSignedInAsync(Guid userId);

    /// <summary>The session ended: cancel work and drop every piece of in-memory state of that account.</summary>
    Task OnSignedOutAsync(Guid userId);
}

/// <summary>
/// Sign-in/sign-out fan-out. Activation restores every account-scoped store from the local cache first, then
/// runs the sign-in hooks (which start network work in the background). Sign-out runs the hooks first
/// (realtime, calls, notifications), then every account-scoped store, then wipes the account's cache prefix
/// and the media file cache, so nothing of the old account survives on the device.
/// </summary>
internal sealed class AccountLifecycle(LocalCache cache, IMediaFileStore mediaFiles, ILogger<AccountLifecycle> logger)
{
    private readonly object _lock = new();
    private readonly List<IAccountScoped> _scoped = [];
    private readonly List<Func<Guid, Task>> _signInHooks = [];
    private readonly List<Func<Guid, Task>> _beforeSignOutHooks = [];
    private readonly List<Func<Guid, Task>> _signOutHooks = [];
    private CancellationTokenSource _account = CreateCancelled();
    private Guid? _activeUserId;
    private Task _activation = Task.CompletedTask;

    /// <summary>The account whose stores are currently populated (null while signed out).</summary>
    public Guid? ActiveUserId
    {
        get
        {
            lock (_lock) return _activeUserId;
        }
    }

    /// <summary>Cancelled when the active account signs out: late results of the old account must be dropped.</summary>
    public CancellationToken AccountToken
    {
        get
        {
            lock (_lock) return _account.Token;
        }
    }

    public void Register(IAccountScoped scoped)
    {
        Guid? active;
        lock (_lock)
        {
            _scoped.Add(scoped);
            active = _activeUserId;
        }
        // Constructed after activation (lazy resolution): restore it right away.
        if (active is { } userId)
            RunIsolatedAsync(() => scoped.OnSignedInAsync(userId), scoped.GetType().Name).Forget(logger);
    }

    /// <summary>Runs after the stores restored their caches; must not block on the network.</summary>
    public void OnSignIn(Func<Guid, Task> hook)
    {
        lock (_lock) _signInHooks.Add(hook);
    }

    /// <summary>Runs while the session is still valid (only when the user signs out voluntarily).</summary>
    public void OnBeforeSignOut(Func<Guid, Task> hook)
    {
        lock (_lock) _beforeSignOutHooks.Add(hook);
    }

    public void OnSignOut(Func<Guid, Task> hook)
    {
        lock (_lock) _signOutHooks.Add(hook);
    }

    /// <summary>Idempotent per account: a second call for the active account returns the first activation.</summary>
    public Task ActivateAsync(Guid userId)
    {
        lock (_lock)
        {
            if (_activeUserId == userId) return _activation;
            _activeUserId = userId;
            _account = new CancellationTokenSource();
            _activation = ActivateCoreAsync(userId);
            return _activation;
        }
    }

    private async Task ActivateCoreAsync(Guid userId)
    {
        await Task.Yield();
        IAccountScoped[] scoped;
        Func<Guid, Task>[] hooks;
        lock (_lock)
        {
            scoped = [.. _scoped];
            hooks = [.. _signInHooks];
        }
        await Task.WhenAll(scoped.Select(s => RunIsolatedAsync(() => s.OnSignedInAsync(userId), s.GetType().Name))).ConfigureAwait(false);
        foreach (var hook in hooks)
        {
            if (ActiveUserId != userId) return;
            await RunIsolatedAsync(() => hook(userId), "sign-in hook").ConfigureAwait(false);
        }
    }

    public async Task BeforeSignOutAsync(Guid userId)
    {
        Func<Guid, Task>[] hooks;
        lock (_lock) hooks = [.. _beforeSignOutHooks];
        foreach (var hook in hooks)
            await RunIsolatedAsync(() => hook(userId), "before-sign-out hook").ConfigureAwait(false);
    }

    public async Task SignedOutAsync(Guid userId)
    {
        Func<Guid, Task>[] hooks;
        IAccountScoped[] scoped;
        Task activation;
        lock (_lock)
        {
            hooks = [.. _signOutHooks];
            scoped = [.. _scoped];
            activation = _activation;
            if (_activeUserId == userId || _activeUserId is null)
            {
                _activeUserId = null;
                _account.Cancel();
            }
        }

        // A restore that is still running would otherwise repopulate the stores after they were cleared.
        try
        {
            await activation.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Activation did not finish before sign-out");
        }

        foreach (var hook in hooks)
            await RunIsolatedAsync(() => hook(userId), "sign-out hook").ConfigureAwait(false);
        await Task.WhenAll(scoped.Select(s => RunIsolatedAsync(() => s.OnSignedOutAsync(userId), s.GetType().Name))).ConfigureAwait(false);

        await cache.ClearAsync(LocalCache.AccountPrefix(userId)).ConfigureAwait(false);
        if (mediaFiles.IsSupported)
            await RunIsolatedAsync(mediaFiles.ClearAsync, "media cache").ConfigureAwait(false);
    }

    private async Task RunIsolatedAsync(Func<Task> action, string name)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Account lifecycle step {Step} failed", name);
        }
    }

    private static CancellationTokenSource CreateCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts;
    }
}
