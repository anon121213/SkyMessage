using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Session handling (SPEC §8.1 IAuthService). Tokens live in <see cref="ISecureStore"/> ("auth.v1"). The access
/// token is refreshed 60 s before it expires; refresh is single-flight in the process and serialized across
/// tabs/processes by <see cref="IInterProcessLock"/> ("sky-auth-refresh"), re-reading the stored session inside
/// the lock first because another instance may already have rotated the refresh token. A rejected refresh
/// (401 refresh_token_invalid and other permanent errors) signs out locally; network errors keep the session.
/// </summary>
internal sealed class AuthService : IAuthService, ITokenSource
{
    public const string RefreshLockName = "sky-auth-refresh";
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(6);

    private readonly AuthSessionStore _sessions;
    private readonly IAuthChannel _channel;
    private readonly IInterProcessLock _interProcessLock;
    private readonly IPlatformInfo _platform;
    private readonly AccountLifecycle _lifecycle;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthService> _logger;
    private readonly object _lock = new();
    // Serializes writes of the stored session (refresh vs. profile updates) so a rotated refresh token is never lost.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private volatile AuthState _state = AuthState.Initializing;
    private volatile MeDto? _me;
    private volatile bool _signingOut;
    private Task? _initialize;
    private Task<StoredSession?>? _refresh;
    private Task? _logout;

    public AuthService(
        AuthSessionStore sessions,
        IAuthChannel channel,
        IInterProcessLock interProcessLock,
        IPlatformInfo platform,
        AccountLifecycle lifecycle,
        TimeProvider time,
        ILogger<AuthService> logger)
    {
        _sessions = sessions;
        _channel = channel;
        _interProcessLock = interProcessLock;
        _platform = platform;
        _lifecycle = lifecycle;
        _time = time;
        _logger = logger;
    }

    public AuthState State => _state;

    public MeDto? Me => _me ?? _sessions.Current?.Me;

    public Guid? UserId => _sessions.Current?.UserId;

    public Guid? SessionId => _sessions.Current?.SessionId;

    public string? MediaKey => _sessions.Current?.MediaKey is { Length: > 0 } key ? key : null;

    public event Action? Changed;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock) return _initialize ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        var session = await _sessions.LoadAsync().ConfigureAwait(false);
        _me = session?.Me;
        _state = session is null ? AuthState.SignedOut : AuthState.SignedIn;
        RaiseChanged();
    }

    public async Task<ApiResult> LoginAsync(string login, string password, CancellationToken cancellationToken = default)
    {
        await PrepareForSignInAsync().ConfigureAwait(false);
        try
        {
            var response = await _channel.LoginAsync(new LoginRequest
            {
                Login = login.Trim(),
                Password = password,
                Device = DeviceInfo(),
                DefaultRegion = Region(),
            }, cancellationToken).ConfigureAwait(false);
            await CompleteSignInAsync(response).ConfigureAwait(false);
            return ApiResult.Ok;
        }
        catch (ApiException ex)
        {
            return ApiResult.Fail(ex);
        }
    }

    public async Task<ApiResult> RegisterAsync(string username, string password, string displayName, string? inviteCode = null,
        CancellationToken cancellationToken = default)
    {
        await PrepareForSignInAsync().ConfigureAwait(false);
        try
        {
            var response = await _channel.RegisterAsync(new RegisterRequest
            {
                Username = username.Trim().TrimStart('@'),
                Password = password,
                DisplayName = displayName.Trim(),
                Device = DeviceInfo(),
                InviteCode = string.IsNullOrWhiteSpace(inviteCode) ? null : inviteCode.Trim(),
            }, cancellationToken).ConfigureAwait(false);
            await CompleteSignInAsync(response).ConfigureAwait(false);
            return ApiResult.Ok;
        }
        catch (ApiException ex)
        {
            return ApiResult.Fail(ex);
        }
    }

    public async Task<UsernameAvailabilityDto> CheckUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        string? token = null;
        if (_sessions.Current is not null)
        {
            try
            {
                token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException)
            {
                // Anonymous check still works (only the own username would be reported as taken).
            }
        }
        return await _channel.CheckUsernameAsync(username.Trim().TrimStart('@'), token, cancellationToken).ConfigureAwait(false);
    }

    public Task LogoutAsync(bool revokeOnServer = true)
    {
        lock (_lock)
        {
            if (_logout is { IsCompleted: false } running) return running;
            _logout = LogoutCoreAsync(revokeOnServer);
            return _logout;
        }
    }

    private async Task LogoutCoreAsync(bool revokeOnServer)
    {
        await Task.Yield();
        var session = _sessions.Current;
        if (session is null)
        {
            if (_state != AuthState.SignedOut)
            {
                _state = AuthState.SignedOut;
                RaiseChanged();
            }
            return;
        }

        _signingOut = true;
        try
        {
            if (revokeOnServer)
            {
                // Calls are hung up while the token still works, then the session is revoked server-side.
                await _lifecycle.BeforeSignOutAsync(session.UserId).ConfigureAwait(false);
                await RevokeOnServerAsync(session).ConfigureAwait(false);
            }

            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _sessions.ClearAsync(session.SessionId).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            _me = null;
            _state = AuthState.SignedOut;
            _logger.LogInformation("Signed out (server revoke requested: {Revoke})", revokeOnServer);
            RaiseChanged();
            await _lifecycle.SignedOutAsync(session.UserId).ConfigureAwait(false);
        }
        finally
        {
            _signingOut = false;
        }
    }

    private async Task RevokeOnServerAsync(StoredSession session)
    {
        using var timeout = new CancellationTokenSource(LogoutTimeout);
        try
        {
            var token = session.AccessToken;
            if (NeedsRefresh(session))
                token = await GetAccessTokenAsync(timeout.Token).ConfigureAwait(false) ?? token;
            await _channel.LogoutAsync(token, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or OperationCanceledException)
        {
            _logger.LogInformation("Server-side logout did not complete; the session ends locally");
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var session = _sessions.Current;
        if (session is null) return null;
        if (!NeedsRefresh(session)) return session.AccessToken;

        try
        {
            var refreshed = await RefreshAsync(session.AccessToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            return refreshed?.AccessToken;
        }
        catch (ApiException ex) when (ex.IsTransient)
        {
            // Offline: the current token is still good until it actually expires.
            var current = _sessions.Current;
            if (current is not null && !IsExpired(current)) return current.AccessToken;
            throw;
        }
    }

    public async Task<string?> HandleUnauthorizedAsync(string rejectedToken, string code, CancellationToken cancellationToken)
    {
        if (_signingOut) return null;
        var session = _sessions.Current;
        if (session is null) return null;
        if (!string.Equals(session.AccessToken, rejectedToken, StringComparison.Ordinal)) return session.AccessToken;

        var refreshed = await RefreshAsync(rejectedToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        return refreshed?.AccessToken;
    }

    public void UpdateMe(MeDto me)
    {
        var session = _sessions.Current;
        if (session is null || me.Id != session.UserId) return;
        _me = me;
        RaiseChanged();
        PersistMeAsync(me).Forget(_logger);
    }

    public async Task RefreshMeAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return;
        MeDto me;
        try
        {
            me = await _channel.GetMeAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            var fresh = await HandleUnauthorizedAsync(token, ex.Code, cancellationToken).ConfigureAwait(false);
            if (fresh is null || fresh == token) return;
            me = await _channel.GetMeAsync(fresh, cancellationToken).ConfigureAwait(false);
        }
        UpdateMe(me);
    }

    /// <summary>
    /// Stores the profile in the persisted session. Another tab or process may have rotated the tokens since this
    /// instance read them, and writing this instance's copy back would restore a rotated-out refresh token, whose next
    /// use the server treats as theft (every session signed out). So the stored session is re-read under the refresh
    /// lock and only its profile changes; the newer of the two token sets is kept (and adopted here).
    /// </summary>
    private async Task PersistMeAsync(MeDto me)
    {
        // Without the lock another instance could rotate between the read and the write: the profile is only an
        // offline cache, so it is better not stored than risking the tokens.
        var held = await TryAcquireRefreshLockAsync().ConfigureAwait(false);
        if (held is null) return;
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var current = _sessions.Current;
                if (current is null || current.UserId != me.Id) return;
                var (readOk, stored) = await _sessions.TryReadAsync().ConfigureAwait(false);
                // Unreadable, signed out or replaced by another instance: the store is not this session's to write.
                if (!readOk || stored is null || stored.SessionId != current.SessionId) return;
                // Usually the stored tokens are the newer ones; this instance's are when its own last write failed.
                var newest = stored.AccessTokenExpiresAt >= current.AccessTokenExpiresAt ? stored : current;
                await _sessions.SaveAsync(newest with { Me = me }).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            await ReleaseRefreshLockAsync(held).ConfigureAwait(false);
        }
    }

    private async Task PrepareForSignInAsync()
    {
        Task? logout;
        lock (_lock) logout = _logout;
        if (logout is not null)
        {
            try
            {
                await logout.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Previous sign-out failed");
            }
        }
        // Never mix two accounts' data: end the old session first.
        if (_sessions.Current is not null)
            await LogoutAsync(revokeOnServer: true).ConfigureAwait(false);
    }

    private async Task CompleteSignInAsync(AuthResponse response)
    {
        var session = StoredSession.From(response, _time.GetUtcNow());
        if (session.UserId == Guid.Empty || session.SessionId == Guid.Empty || session.AccessToken.Length == 0)
            throw new ApiException(0, ClientErrorCodes.Unknown, "Malformed sign-in response");

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _sessions.SaveAsync(session).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        _me = session.Me;
        _state = AuthState.SignedIn;
        _logger.LogInformation("Signed in as {UserId}", session.UserId);
        RaiseChanged();
        await _lifecycle.ActivateAsync(session.UserId).ConfigureAwait(false);
    }

    private Task<StoredSession?> RefreshAsync(string staleAccessToken)
    {
        lock (_lock)
        {
            if (_refresh is { IsCompleted: false } running) return running;
            _refresh = RefreshCoreAsync(staleAccessToken);
            return _refresh;
        }
    }

    private async Task<StoredSession?> RefreshCoreAsync(string staleAccessToken)
    {
        await Task.Yield();
        var signOut = false;
        StoredSession? result = null;

        // A stuck lock must not freeze the app; refreshing without it is the lesser evil.
        var held = await TryAcquireRefreshLockAsync().ConfigureAwait(false);

        try
        {
            var current = _sessions.Current;
            if (current is null) return null;

            var (readOk, stored) = await _sessions.TryReadAsync().ConfigureAwait(false);
            if (readOk && stored is null)
            {
                _logger.LogInformation("The session was removed by another instance");
                signOut = true;
            }
            else if (readOk && stored!.SessionId != current.SessionId)
            {
                // Another tab signed in to a different session; this one cannot share the store with it.
                _logger.LogInformation("Another instance replaced the stored session");
                signOut = true;
            }
            else
            {
                var source = readOk ? stored! : current;
                if (!string.Equals(source.AccessToken, staleAccessToken, StringComparison.Ordinal) && !NeedsRefresh(source))
                {
                    // Another instance refreshed already: adopt its tokens.
                    result = source with { Me = source.Me ?? current.Me };
                    _sessions.Set(result);
                }
                else
                {
                    using var timeout = new CancellationTokenSource(RefreshTimeout);
                    try
                    {
                        var response = await _channel.RefreshAsync(source.RefreshToken, timeout.Token).ConfigureAwait(false);
                        var next = StoredSession.From(response, _time.GetUtcNow(), current.Me);
                        if (next.SessionId != current.SessionId || next.UserId != current.UserId || next.AccessToken.Length == 0)
                        {
                            signOut = true;
                        }
                        else
                        {
                            await _writeGate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                if (_sessions.Current?.SessionId == next.SessionId)
                                {
                                    await _sessions.SaveAsync(next).ConfigureAwait(false);
                                    result = next;
                                }
                            }
                            finally
                            {
                                _writeGate.Release();
                            }
                            if (result is not null && next.Me is { } freshMe) _me = freshMe;
                        }
                    }
                    catch (ApiException ex) when (!ex.IsTransient)
                    {
                        _logger.LogInformation("Token refresh rejected ({Code}); signing out", ex.Code);
                        signOut = true;
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        throw HttpErrors.Timeout();
                    }
                }
            }
        }
        finally
        {
            await ReleaseRefreshLockAsync(held).ConfigureAwait(false);
        }

        if (signOut)
        {
            // Not awaited: sign-out stops realtime, whose connection attempt may be waiting for this refresh.
            LogoutAsync(revokeOnServer: false).Forget(_logger);
            return null;
        }
        return result;
    }

    /// <summary>The cross-instance lock for writes of rotating tokens, or null when it is unavailable after <see cref="LockTimeout"/>.</summary>
    private async Task<IAsyncDisposable?> TryAcquireRefreshLockAsync()
    {
        using var timeout = new CancellationTokenSource(LockTimeout);
        try
        {
            return await _interProcessLock.AcquireAsync(RefreshLockName, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh lock unavailable");
            return null;
        }
    }

    private async Task ReleaseRefreshLockAsync(IAsyncDisposable? held)
    {
        if (held is null) return;
        try
        {
            await held.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Releasing the refresh lock failed");
        }
    }

    private bool NeedsRefresh(StoredSession session) => session.LocalAccessExpiresAt - _time.GetUtcNow() < RefreshAhead;

    private bool IsExpired(StoredSession session) => _time.GetUtcNow() >= session.LocalAccessExpiresAt;

    private DeviceInfoDto DeviceInfo()
    {
        var name = string.IsNullOrWhiteSpace(_platform.DeviceName) ? _platform.Platform.ToString() : _platform.DeviceName.Trim();
        if (name.Length > Limits.DeviceNameMaxLength) name = name[..Limits.DeviceNameMaxLength];
        return new DeviceInfoDto
        {
            DeviceName = name,
            Platform = _platform.Platform,
            AppVersion = _platform.AppVersion,
            OsVersion = _platform.OsVersion,
        };
    }

    private string? Region() => _platform.CountryCode is { Length: 2 } code ? code.ToUpperInvariant() : null;

    private void RaiseChanged() => EventRaiser.Raise(Changed, _logger);
}
