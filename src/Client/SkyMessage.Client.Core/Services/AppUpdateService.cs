using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>What the updater remembers across restarts ("update.v1" in <see cref="IPreferencesStore"/>).</summary>
internal sealed record UpdateState
{
    public DateTimeOffset? LastCheckAt { get; init; }
    /// <summary>The newer build the server offered at the last check, null when there was none.</summary>
    public AndroidReleaseDto? Release { get; init; }
    /// <summary>The release the stored package belongs to (it may still be partial).</summary>
    public int PackageVersionCode { get; init; }
    public string? PackageSha256 { get; init; }
    /// <summary>The stored package is complete and matched its SHA-256.</summary>
    public bool PackageVerified { get; init; }
    /// <summary>A versionCode Android refused for good (another signature, incompatible): not retried automatically.</summary>
    public int BlockedVersionCode { get; init; }
    public string? BlockedError { get; init; }
    /// <summary>A versionCode Android asked to confirm, or the user declined: no more silent attempts for it.</summary>
    public int ConfirmVersionCode { get; init; }
    /// <summary>When the user last asked to install: the trip to the system settings may restart the app on the way.</summary>
    public DateTimeOffset? InstallRequestedAt { get; init; }
    /// <summary>Web: the version a reload was already attempted for (it is not attempted twice).</summary>
    public string? WebReloadTarget { get; init; }
}

/// <summary>
/// <see cref="IAppUpdateService"/>: one run at a time (pending triggers merge), state persisted after every step so a
/// killed process resumes where it stopped — a partial download continues with a Range request, a verified package is
/// installed without downloading again. Never throws into callers; failures become <see cref="AppUpdateStatus.Error"/>.
/// </summary>
internal sealed class AppUpdateService : IAppUpdateService, IDisposable
{
    public const string StateKey = "update.v1";

    private readonly IAppUpdateFeed _feed;
    private readonly IAppUpdateInstaller _installer;
    private readonly IPlatformInfo _platform;
    private readonly IPreferencesStore _preferences;
    private readonly ISettingsService _settings;
    private readonly INetworkStatusService _network;
    private readonly IAppLifecycle _lifecycle;
    private readonly IRealtimeService _realtime;
    private readonly ICallService _calls;
    private readonly TimeProvider _time;
    private readonly ILogger<AppUpdateService> _logger;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ProgressThrottle _progress;

    private UpdateState _state = new();
    private AppUpdateStatus _status = new();
    private volatile bool _loaded;
    private bool _started;
    private bool _disposed;
    private UpdateTrigger? _next;
    private bool _looping;
    private CancellationTokenSource? _download;
    private CancellationTokenSource? _backgroundInstall;
    private CancellationTokenSource? _webReload;
    private bool _downloadRequested;
    private bool _installing;
    private int _failures;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private ITimer? _timer;
    private ITimer? _retryTimer;
    private ITimer? _installWatch;

    public AppUpdateService(
        IAppUpdateFeed feed,
        IAppUpdateInstaller installer,
        IPlatformInfo platform,
        IPreferencesStore preferences,
        ISettingsService settings,
        INetworkStatusService network,
        IAppLifecycle lifecycle,
        IRealtimeService realtime,
        ICallService calls,
        TimeProvider time,
        ILogger<AppUpdateService> logger)
    {
        _feed = feed;
        _installer = installer;
        _platform = platform;
        _preferences = preferences;
        _settings = settings;
        _network = network;
        _lifecycle = lifecycle;
        _realtime = realtime;
        _calls = calls;
        _time = time;
        _logger = logger;
        _progress = new ProgressThrottle(time);
    }

    public bool CanSelfUpdate => _installer.IsSupported;

    /// <summary>The browser picks up new web clients; iOS/Mac update through the stores, so they have nothing to do.</summary>
    private bool HasWork => _installer.IsSupported || !_platform.IsNativeApp;

    public AppUpdateStatus Status => _status;

    public event Action? Changed;

    public event Action? ReloadRequested;

    /// <summary>Hooks the triggers and schedules the first check (the bootstrapper calls it once).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed || !HasWork) return;
            _started = true;
        }
        _lifecycle.ForegroundChanged += OnForegroundChanged;
        _network.Changed += OnPolicyInputsChanged;
        _settings.Changed += OnPolicyInputsChanged;
        _realtime.Resynced += OnResynced;
        _calls.Changed += OnCallsChanged;
        _installer.InstallStatusChanged += OnInstallStatus;
        _timer = _time.CreateTimer(static s => ((AppUpdateService)s!).Kick(UpdateTrigger.Periodic), this,
            AppUpdateRules.TimerPeriod, AppUpdateRules.TimerPeriod);
        StartAsync().Forget(_logger);
    }

    private async Task StartAsync()
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        await Task.Delay(AppUpdateRules.StartDelay, _time, _lifetime.Token).ConfigureAwait(false);
        Kick(UpdateTrigger.Start);
    }

    // ---- Public actions ----------------------------------------------------------------------------------------

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!HasWork) return;
        await EnsureLoadedAsync().ConfigureAwait(false);
        SetDownloadRequested(_installer.IsSupported);
        if (!await CheckAsync(manual: true, cancellationToken).ConfigureAwait(false)) SetDownloadRequested(false);
        Kick(UpdateTrigger.Continue);
    }

    public Task DownloadNowAsync()
    {
        if (!_installer.IsSupported) return Task.CompletedTask;
        lock (_lock)
        {
            _downloadRequested = true;
            // The user asked: no back-off.
            _retryAt = DateTimeOffset.MinValue;
        }
        Kick(UpdateTrigger.Continue);
        return Task.CompletedTask;
    }

    public async Task InstallNowAsync()
    {
        if (!_installer.IsSupported) return;
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (_state.Release is not { } release) return;
        if (!await HasVerifiedPackageAsync(release).ConfigureAwait(false))
        {
            await DownloadNowAsync().ConfigureAwait(false);
            return;
        }
        await UpdateStateAsync(s => s with { InstallRequestedAt = _time.GetUtcNow() }).ConfigureAwait(false);
        await InstallAsync().ConfigureAwait(false);
    }

    public async Task<bool> RequestInstallPermissionAsync()
    {
        if (!_installer.IsSupported) return false;
        await EnsureLoadedAsync().ConfigureAwait(false);
        // Android may restart the app when the permission changes: remember that the user wants the install.
        await UpdateStateAsync(s => s with { InstallRequestedAt = _time.GetUtcNow() }).ConfigureAwait(false);
        bool granted;
        try
        {
            granted = await _installer.RequestInstallPermissionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the install permission screen");
            granted = false;
        }
        Publish();
        if (granted && _state.Release is { } release)
        {
            if (await HasVerifiedPackageAsync(release).ConfigureAwait(false)) await InstallAsync().ConfigureAwait(false);
            else await DownloadNowAsync().ConfigureAwait(false);
        }
        return granted;
    }

    public void ReloadWebNow()
    {
        if (_platform.IsNativeApp) return;
        RequestWebReloadAsync().Forget(_logger);
    }

    // ---- The run loop --------------------------------------------------------------------------------------------

    private void Kick(UpdateTrigger trigger)
    {
        lock (_lock)
        {
            if (_disposed || !_started) return;
            _next = _next is { } pending && pending > trigger ? pending : trigger;
            if (_looping) return;
            _looping = true;
        }
        LoopAsync().Forget(_logger);
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            UpdateTrigger trigger;
            lock (_lock)
            {
                if (_next is not { } next || _disposed)
                {
                    _looping = false;
                    return;
                }
                trigger = next;
                _next = null;
            }
            try
            {
                await RunOnceAsync(trigger, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Update run failed");
            }
        }
    }

    /// <summary>One pass: check the server when the trigger says so, then move the Android update forward.</summary>
    internal async Task RunOnceAsync(UpdateTrigger trigger, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (AppUpdateRules.ShouldCheck(trigger, _state.LastCheckAt, _time.GetUtcNow()))
            await CheckAsync(manual: false, cancellationToken).ConfigureAwait(false);
        if (_installer.IsSupported) await AdvanceAsync(cancellationToken).ConfigureAwait(false);
        else EvaluateWebReload();
    }

    private async Task<bool> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish(s => s with { IsChecking = true, Error = manual && s.Phase != AppUpdatePhase.Failed ? null : s.Error });
            AppVersionDto offer;
            try
            {
                offer = await _feed.GetVersionAsync(cancellationToken).ConfigureAwait(false) ?? new AppVersionDto();
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                // A server from before app updates: nothing is offered.
                offer = new AppVersionDto();
            }
            catch (ApiException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Update check failed: {Code}", ex.Code);
                if (manual) Publish(s => s with { Error = ex.Code });
                return false;
            }
            await ApplyOfferAsync(offer).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Publish(s => s with { IsChecking = false });
            _checkGate.Release();
        }
    }

    private async Task ApplyOfferAsync(AppVersionDto offer)
    {
        var now = _time.GetUtcNow();
        var installed = _installer.IsSupported ? _installer.InstalledVersionCode : 0;
        var release = _installer.IsSupported && AppUpdateRules.IsNewer(offer.Android, installed) ? offer.Android : null;
        string? newWeb = null;
        if (!_platform.IsNativeApp)
        {
            newWeb = AppUpdateRules.NewWebVersion(offer.Web?.Version, _platform.AppVersion);
            // A reload for it already happened and this page still runs the old client: another one would not help.
            if (newWeb is not null && string.Equals(newWeb, _state.WebReloadTarget, StringComparison.OrdinalIgnoreCase)) newWeb = null;
        }
        await UpdateStateAsync(s => s with { LastCheckAt = now, Release = release }).ConfigureAwait(false);
        Publish(s => s with
        {
            LastCheckedAt = now,
            Release = release,
            Required = AppUpdateRules.IsRequired(release, installed),
            NewWebVersion = newWeb,
            Phase = _installer.IsSupported ? s.Phase : AppUpdatePhase.UpToDate,
            Error = s.Phase == AppUpdatePhase.Failed ? s.Error : null,
        });
    }

    private async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _installing)) return;
        var installed = _installer.InstalledVersionCode;
        var release = _state.Release;
        if (release is null || release.VersionCode <= installed)
        {
            if (_state.PackageVersionCode != 0) await ForgetPackageAsync().ConfigureAwait(false);
            if (_state.LastCheckAt is not null)
            {
                Publish(s => s with
                {
                    Phase = AppUpdatePhase.UpToDate, Release = null, Required = false, Progress = 0, WaitingForWifi = false,
                    Error = s.Phase == AppUpdatePhase.Failed ? null : s.Error,
                });
            }
            return;
        }

        var required = AppUpdateRules.IsRequired(release, installed);
        bool manual;
        DateTimeOffset retryAt;
        lock (_lock)
        {
            manual = _downloadRequested;
            retryAt = _retryAt;
        }
        if (_state.BlockedVersionCode == release.VersionCode && !manual)
        {
            Publish(s => s with
            {
                Phase = AppUpdatePhase.Failed, Release = release, Required = required, WaitingForWifi = false,
                Error = _state.BlockedError ?? AppUpdateErrors.InstallFailed,
            });
            return;
        }
        if (await HasVerifiedPackageAsync(release).ConfigureAwait(false))
        {
            BecomeReady(release, required);
            return;
        }

        var updates = _settings.Current.Updates;
        var networkAllows = AppUpdateRules.NetworkAllows(updates.WifiOnly, _network.Kind, _network.IsMetered);
        var retryDue = _time.GetUtcNow() >= retryAt;
        if (!manual && !(updates.AutoUpdate && networkAllows && retryDue))
        {
            Publish(s => s with
            {
                Phase = s.Phase == AppUpdatePhase.Failed && !retryDue ? AppUpdatePhase.Failed : AppUpdatePhase.Available,
                Release = release, Required = required, Progress = 0,
                WaitingForWifi = updates.AutoUpdate && !networkAllows,
            });
            return;
        }
        await DownloadAndVerifyAsync(release, required, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadAndVerifyAsync(AndroidReleaseDto release, bool required, CancellationToken cancellationToken)
    {
        var name = AppUpdateRules.PackageFileName(release);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock) _download = cts;
        try
        {
            if (_state.PackageVersionCode != release.VersionCode
                || !string.Equals(_state.PackageSha256, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // Another build (or this version rebuilt): what is stored belongs to something else.
                await _installer.DeletePackagesAsync().ConfigureAwait(false);
                await UpdateStateAsync(s => s with { PackageVersionCode = release.VersionCode, PackageSha256 = release.Sha256, PackageVerified = false })
                    .ConfigureAwait(false);
            }
            var stored = Math.Max(0, await _installer.GetPackageLengthAsync(name).ConfigureAwait(false));
            if (stored > release.Size) stored = 0;
            Publish(s => s with
            {
                Phase = AppUpdatePhase.Downloading, Release = release, Required = required, Progress = Fraction(stored, release.Size),
                WaitingForWifi = false, Error = null,
            });
            if (stored < release.Size) await DownloadAsync(release, name, stored, cts.Token).ConfigureAwait(false);

            bool intact;
            await using (var package = await _installer.OpenPackageReadAsync(name, cts.Token).ConfigureAwait(false))
                intact = package is not null && await AppUpdateRules.VerifyAsync(package, release.Size, release.Sha256, cts.Token).ConfigureAwait(false);
            if (!intact)
            {
                _logger.LogWarning("Update {VersionCode} failed verification; downloading it again", release.VersionCode);
                await ForgetPackageAsync().ConfigureAwait(false);
                Fail(release, required, AppUpdateErrors.Integrity);
                return;
            }

            await UpdateStateAsync(s => s with { PackageVerified = true }).ConfigureAwait(false);
            lock (_lock)
            {
                _failures = 0;
                _downloadRequested = false;
            }
            _logger.LogInformation("Update {VersionCode} downloaded and verified", release.VersionCode);
            BecomeReady(release, required);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Paused because the network or the settings no longer allow it; the partial file stays for later.
            var updates = _settings.Current.Updates;
            Publish(s => s with
            {
                Phase = AppUpdatePhase.Available, Progress = 0,
                WaitingForWifi = updates.AutoUpdate && !AppUpdateRules.NetworkAllows(updates.WifiOnly, _network.Kind, _network.IsMetered),
            });
        }
        catch (StorageException ex)
        {
            _logger.LogWarning(ex.InnerException, "Writing the update package failed");
            Fail(release, required, AppUpdateErrors.Storage);
        }
        catch (InvalidDataException)
        {
            await ForgetPackageAsync().ConfigureAwait(false);
            Fail(release, required, AppUpdateErrors.Integrity);
        }
        catch (ApiException ex) when (ex.StatusCode == 416)
        {
            // The stored part does not fit the file on the server any more: start over.
            await ForgetPackageAsync().ConfigureAwait(false);
            Fail(release, required, ClientErrorCodes.Network);
        }
        catch (Exception ex) when (ex is ApiException or IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Update download interrupted: {Error}", ex is ApiException api ? api.Code : ex.Message);
            Fail(release, required, ex is ApiException { IsNetworkError: false } other ? other.Code : ClientErrorCodes.Network);
        }
        finally
        {
            lock (_lock)
            {
                if (_download == cts) _download = null;
            }
        }
    }

    private async Task DownloadAsync(AndroidReleaseDto release, string name, long offset, CancellationToken cancellationToken)
    {
        using var download = await _feed.OpenPackageAsync(release.ApkUrl, offset, cancellationToken).ConfigureAwait(false);
        var written = download.StartsAt;
        Stream output;
        try
        {
            output = await _installer.OpenPackageWriteAsync(name, written, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageException(ex);
        }
        await using (output)
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await download.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (written + read > release.Size) throw new InvalidDataException("The package is larger than announced.");
                try
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new StorageException(ex);
                }
                written += read;
                if (_progress.ShouldReport()) Publish(s => s with { Progress = Fraction(written, release.Size) });
            }
            try
            {
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new StorageException(ex);
            }
        }
        if (written != release.Size) throw new IOException("The download ended early.");
        Publish(s => s with { Progress = 1 });
    }

    private void BecomeReady(AndroidReleaseDto release, bool required)
    {
        Publish(s => s with
        {
            Phase = AppUpdatePhase.Ready, Release = release, Required = required, Progress = 1, WaitingForWifi = false, Error = null,
        });
        // The user went to allow installs and Android restarted the app on the way: carry on with what they asked.
        if (_state.InstallRequestedAt is { } asked && _time.GetUtcNow() - asked < AppUpdateRules.InstallRequestWindow
            && _lifecycle.IsForeground && _installer.CanInstallPackages)
        {
            InstallAsync().Forget(_logger);
            return;
        }
        ScheduleBackgroundInstall();
    }

    private void Fail(AndroidReleaseDto release, bool required, string code)
    {
        DateTimeOffset retryAt;
        lock (_lock)
        {
            _failures++;
            _downloadRequested = false;
            retryAt = _retryAt = _time.GetUtcNow() + AppUpdateRules.RetryDelay(_failures);
        }
        Publish(s => s with { Phase = AppUpdatePhase.Failed, Release = release, Required = required, Progress = 0, WaitingForWifi = false, Error = code });
        ScheduleRetry(retryAt);
    }

    private void ScheduleRetry(DateTimeOffset at)
    {
        var due = at - _time.GetUtcNow();
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        lock (_lock)
        {
            if (_disposed) return;
            _retryTimer?.Dispose();
            _retryTimer = _time.CreateTimer(static s => ((AppUpdateService)s!).Kick(UpdateTrigger.Continue), this, due, Timeout.InfiniteTimeSpan);
        }
    }

    // ---- Installing ----------------------------------------------------------------------------------------------

    private async Task InstallAsync()
    {
        if (_state.Release is not { } release) return;
        lock (_lock)
        {
            if (_installing) return;
            _installing = true;
        }
        CancelBackgroundInstall();
        AppInstallResult result;
        try
        {
            if (!await HasVerifiedPackageAsync(release).ConfigureAwait(false))
            {
                lock (_lock) _installing = false;
                Kick(UpdateTrigger.Continue);
                return;
            }
            Publish(s => s with { Phase = AppUpdatePhase.Installing, Error = null });
            result = await _installer.InstallAsync(AppUpdateRules.PackageFileName(release), _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handing the update to the installer failed");
            result = new AppInstallResult(AppInstallStatus.Failed, AppUpdateErrors.InstallFailed);
        }
        await HandleInstallResultAsync(release, result).ConfigureAwait(false);
    }

    private void OnInstallStatus(AppInstallResult result)
    {
        if (_state.Release is { } release) HandleInstallResultAsync(release, result).Forget(_logger);
    }

    private async Task HandleInstallResultAsync(AndroidReleaseDto release, AppInstallResult result)
    {
        _logger.LogInformation("Update {VersionCode} install: {Status} {Error}", release.VersionCode, result.Status, result.Error);
        var required = _status.Required;
        switch (result.Status)
        {
            case AppInstallStatus.Pending:
                Publish(s => s with { Phase = AppUpdatePhase.Installing, Error = null });
                WatchPendingInstall(release);
                return;
            case AppInstallStatus.Succeeded:
                await UpdateStateAsync(s => s with { InstallRequestedAt = null }).ConfigureAwait(false);
                // The process is about to be replaced by the new build.
                Publish(s => s with { Phase = AppUpdatePhase.Installing, Error = null });
                return;
            case AppInstallStatus.NeedsConfirmation:
            case AppInstallStatus.Cancelled:
                lock (_lock) _installing = false;
                await UpdateStateAsync(s => s with
                {
                    ConfirmVersionCode = release.VersionCode,
                    InstallRequestedAt = result.Status == AppInstallStatus.Cancelled ? null : s.InstallRequestedAt,
                }).ConfigureAwait(false);
                Publish(s => s with { Phase = AppUpdatePhase.Ready, Error = null });
                return;
            default:
                lock (_lock) _installing = false;
                var code = result.Error ?? AppUpdateErrors.InstallFailed;
                await UpdateStateAsync(s => s with { InstallRequestedAt = null }).ConfigureAwait(false);
                if (code is AppUpdateErrors.Conflict or AppUpdateErrors.Incompatible or AppUpdateErrors.Blocked)
                {
                    // Retrying cannot help: this build is never installed automatically.
                    await UpdateStateAsync(s => s with { BlockedVersionCode = release.VersionCode, BlockedError = code }).ConfigureAwait(false);
                    await ForgetPackageAsync().ConfigureAwait(false);
                    Publish(s => s with { Phase = AppUpdatePhase.Failed, Progress = 0, Error = code });
                    return;
                }
                if (code == AppUpdateErrors.Invalid) await ForgetPackageAsync().ConfigureAwait(false);
                Fail(release, required, code);
                return;
        }
    }

    /// <summary>
    /// Android reports how a committed install ended (or the process is replaced). Should it never say, the update must
    /// not stay "installing" for good: after a while it is ready again and can be retried.
    /// </summary>
    private void WatchPendingInstall(AndroidReleaseDto release)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _installWatch?.Dispose();
            _installWatch = _time.CreateTimer(static s =>
            {
                var (service, pending) = ((AppUpdateService, AndroidReleaseDto))s!;
                service.ReleaseStuckInstall(pending);
            }, (this, release), AppUpdateRules.InstallingTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReleaseStuckInstall(AndroidReleaseDto release)
    {
        lock (_lock)
        {
            if (!_installing || _status.Phase != AppUpdatePhase.Installing || _state.Release?.VersionCode != release.VersionCode) return;
            _installing = false;
        }
        _logger.LogInformation("No install outcome for update {VersionCode}; offering it again", release.VersionCode);
        Publish(s => s with { Phase = AppUpdatePhase.Ready });
    }

    private void ScheduleBackgroundInstall()
    {
        if (!_installer.IsSupported || !BackgroundInstallAllowed()) return;
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_disposed || _backgroundInstall is not null) return;
            cts = _backgroundInstall = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
        BackgroundInstallAsync(cts).Forget(_logger);
    }

    private async Task BackgroundInstallAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(AppUpdateRules.BackgroundInstallDelay, _time, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested || !BackgroundInstallAllowed()) return;
            lock (_lock)
            {
                if (_backgroundInstall == cts) _backgroundInstall = null;
            }
            _logger.LogInformation("Installing the update while the app is in the background");
            await InstallAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock)
            {
                if (_backgroundInstall == cts) _backgroundInstall = null;
            }
            cts.Dispose();
        }
    }

    private bool BackgroundInstallAllowed()
    {
        var release = _state.Release;
        return release is not null && _status.Phase == AppUpdatePhase.Ready && !Volatile.Read(ref _installing)
               && AppUpdateRules.MayInstallInBackground(_settings.Current.Updates.AutoUpdate, _lifecycle.IsForeground, InCall,
                   _installer.CanInstallSilently, _state.ConfirmVersionCode == release.VersionCode);
    }

    private void CancelBackgroundInstall()
    {
        CancellationTokenSource? pending;
        lock (_lock)
        {
            pending = _backgroundInstall;
            _backgroundInstall = null;
        }
        try
        {
            pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ---- Web reloads ---------------------------------------------------------------------------------------------

    private void EvaluateWebReload()
    {
        if (_platform.IsNativeApp || _status.NewWebVersion is null || _lifecycle.IsForeground || InCall) return;
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_disposed || _webReload is not null) return;
            cts = _webReload = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
        WebReloadAsync(cts).Forget(_logger);
    }

    private async Task WebReloadAsync(CancellationTokenSource cts)
    {
        try
        {
            // Hidden for a moment, not just switching tabs: reload in the background so the new client is there on return.
            await Task.Delay(AppUpdateRules.WebReloadDelay, _time, cts.Token).ConfigureAwait(false);
            if (!cts.IsCancellationRequested && !_lifecycle.IsForeground && !InCall) await RequestWebReloadAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock)
            {
                if (_webReload == cts) _webReload = null;
            }
            cts.Dispose();
        }
    }

    private void CancelWebReload()
    {
        CancellationTokenSource? pending;
        lock (_lock)
        {
            pending = _webReload;
            _webReload = null;
        }
        try
        {
            pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RequestWebReloadAsync()
    {
        if (_status.NewWebVersion is not { } target) return;
        // Remembered first: if the page comes back with the old client anyway, it is not reloaded for this version again.
        await UpdateStateAsync(s => s with { WebReloadTarget = target }).ConfigureAwait(false);
        _logger.LogInformation("Reloading for web client {Version}", target);
        EventRaiser.Raise(ReloadRequested, _logger);
    }

    // ---- Triggers --------------------------------------------------------------------------------------------------

    private void OnForegroundChanged(bool foreground)
    {
        if (foreground)
        {
            CancelBackgroundInstall();
            CancelWebReload();
            // The user may have allowed installs in the system settings meanwhile.
            Publish();
            Kick(UpdateTrigger.Resume);
        }
        else
        {
            ScheduleBackgroundInstall();
            EvaluateWebReload();
        }
    }

    /// <summary>Network or settings changed: pause an automatic download they no longer allow, else carry on.</summary>
    private void OnPolicyInputsChanged()
    {
        var updates = _settings.Current.Updates;
        CancellationTokenSource? download;
        bool manual;
        lock (_lock)
        {
            download = _download;
            manual = _downloadRequested;
        }
        if (download is not null && !manual
            && (!updates.AutoUpdate || !AppUpdateRules.NetworkAllows(updates.WifiOnly, _network.Kind, _network.IsMetered)))
        {
            try
            {
                download.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        if (!updates.AutoUpdate) CancelBackgroundInstall();
        if (_network.IsConnected) Kick(UpdateTrigger.Continue);
    }

    private void OnResynced() => Kick(UpdateTrigger.Reconnect);

    private void OnCallsChanged()
    {
        if (InCall)
        {
            CancelBackgroundInstall();
            CancelWebReload();
            return;
        }
        ScheduleBackgroundInstall();
        EvaluateWebReload();
    }

    private bool InCall => _calls.Current is not null || _calls.Incoming is not null;

    // ---- State -------------------------------------------------------------------------------------------------------

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            var state = await ReadStateAsync().ConfigureAwait(false);
            var loaded = state;
            if (!_platform.IsNativeApp && state.WebReloadTarget is { } target
                && string.Equals(target, _platform.AppVersion, StringComparison.OrdinalIgnoreCase))
                state = state with { WebReloadTarget = null }; // the reload brought the new client
            var installed = _installer.IsSupported ? _installer.InstalledVersionCode : 0;
            var installedUpdate = _installer.IsSupported && state.PackageVersionCode != 0 && state.PackageVersionCode <= installed;
            if (installedUpdate)
                state = state with { PackageVersionCode = 0, PackageSha256 = null, PackageVerified = false, ConfirmVersionCode = 0, InstallRequestedAt = null };
            if (state.Release is { } stale && (!_installer.IsSupported || stale.VersionCode <= installed)) state = state with { Release = null };
            if (state.BlockedVersionCode != 0 && state.BlockedVersionCode <= installed) state = state with { BlockedVersionCode = 0, BlockedError = null };
            lock (_lock) _state = state;
            if (installedUpdate)
            {
                _logger.LogInformation("Running the updated build {VersionCode}", installed);
                await DeletePackagesQuietlyAsync().ConfigureAwait(false);
            }
            if (state != loaded) await SaveStateAsync().ConfigureAwait(false);
            _loaded = true;

            var release = state.Release;
            Publish(s => s with
            {
                LastCheckedAt = state.LastCheckAt,
                Release = release,
                Required = AppUpdateRules.IsRequired(release, installed),
                Phase = release is not null
                    ? state.PackageVerified && state.PackageVersionCode == release.VersionCode ? AppUpdatePhase.Ready : AppUpdatePhase.Available
                    : state.LastCheckAt is not null ? AppUpdatePhase.UpToDate : AppUpdatePhase.Idle,
                Progress = release is not null && state.PackageVerified ? 1 : 0,
            });
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<UpdateState> ReadStateAsync()
    {
        try
        {
            var json = await _preferences.GetAsync(StateKey).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return new UpdateState();
            return JsonSerializer.Deserialize<UpdateState>(json, SkyJson.Options) ?? new UpdateState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The stored update state is unreadable; starting over");
            return new UpdateState();
        }
    }

    private async Task UpdateStateAsync(Func<UpdateState, UpdateState> change)
    {
        lock (_lock) _state = change(_state);
        await SaveStateAsync().ConfigureAwait(false);
    }

    private async Task SaveStateAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Every change is applied to _state before it is saved, so the newest one wins even when saves overlap.
            UpdateState current;
            lock (_lock) current = _state;
            await _preferences.SetAsync(StateKey, JsonSerializer.Serialize(current, SkyJson.Options)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not store the update state");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> HasVerifiedPackageAsync(AndroidReleaseDto release)
    {
        var state = _state;
        if (!state.PackageVerified || state.PackageVersionCode != release.VersionCode
            || !string.Equals(state.PackageSha256, release.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;
        // The OS may have cleared the cache it lives in.
        if (await _installer.GetPackageLengthAsync(AppUpdateRules.PackageFileName(release)).ConfigureAwait(false) == release.Size) return true;
        await UpdateStateAsync(s => s with { PackageVerified = false }).ConfigureAwait(false);
        return false;
    }

    private async Task ForgetPackageAsync()
    {
        await DeletePackagesQuietlyAsync().ConfigureAwait(false);
        await UpdateStateAsync(s => s with { PackageVersionCode = 0, PackageSha256 = null, PackageVerified = false }).ConfigureAwait(false);
    }

    private async Task DeletePackagesQuietlyAsync()
    {
        try
        {
            await _installer.DeletePackagesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete stored update packages");
        }
    }

    private void SetDownloadRequested(bool value)
    {
        lock (_lock)
        {
            _downloadRequested = value;
            if (value) _retryAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Applies a change to the status (and refreshes the install permission); raises Changed when something differs.</summary>
    private void Publish(Func<AppUpdateStatus, AppUpdateStatus>? change = null)
    {
        lock (_lock)
        {
            var next = change?.Invoke(_status) ?? _status;
            next = next with { NeedsInstallPermission = _installer.IsSupported && next.Release is not null && !CanInstallPackages() };
            if (next == _status) return;
            _status = next;
        }
        EventRaiser.Raise(Changed, _logger);
    }

    private bool CanInstallPackages()
    {
        try
        {
            return _installer.CanInstallPackages;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static double Fraction(long done, long total) => total <= 0 ? 0 : Math.Clamp((double)done / total, 0, 1);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _retryTimer?.Dispose();
            _installWatch?.Dispose();
        }
        if (_started)
        {
            _lifecycle.ForegroundChanged -= OnForegroundChanged;
            _network.Changed -= OnPolicyInputsChanged;
            _settings.Changed -= OnPolicyInputsChanged;
            _realtime.Resynced -= OnResynced;
            _calls.Changed -= OnCallsChanged;
            _installer.InstallStatusChanged -= OnInstallStatus;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Writing the package failed (as opposed to the network): reported as a storage problem.</summary>
    private sealed class StorageException(Exception inner) : Exception(inner.Message, inner);
}
