using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Client.Core.Tests;

public sealed class AppUpdateServiceTests : IDisposable
{
    private const string Package = "skymessage-57.apk";

    private readonly FakeFeed _feed = new();
    private readonly FakeInstaller _installer = new() { InstalledVersionCode = 50 };
    private readonly InMemorySecureStore _preferences = new();
    private readonly AppSettings _settings = new();
    private readonly FakeNetwork _network = new();
    private readonly FakeLifecycle _lifecycle = new() { IsForeground = true };
    private readonly ICallService _calls = Substitute.For<ICallService>();
    private readonly IPlatformInfo _platform = Substitute.For<IPlatformInfo>();
    private readonly List<AppUpdateService> _services = [];
    private TimeProvider _time = new ManualClock(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    public AppUpdateServiceTests()
    {
        _platform.IsNativeApp.Returns(true);
        _platform.AppVersion.Returns("0.2.50");
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
    }

    private AppUpdateService Service(IAppUpdateInstaller? installer = null)
    {
        var service = new AppUpdateService(_feed, installer ?? _installer, _platform, _preferences, Fakes.Settings(_settings), _network,
            _lifecycle, Substitute.For<IRealtimeService>(), _calls, _time, Log.For<AppUpdateService>());
        _services.Add(service);
        return service;
    }

    private (AndroidReleaseDto Release, byte[] Apk) Publish(int code = 57, int size = 300_000, int min = 0)
    {
        var apk = RandomNumberGenerator.GetBytes(size);
        var release = new AndroidReleaseDto
        {
            VersionCode = code, VersionName = $"0.2.{code}", ApkUrl = $"updates/SkyMessage-0.2.{code}.apk",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(apk)), Size = size, MinVersionCode = min,
        };
        _feed.Version = new AppVersionDto { Android = release, ServerVersion = $"0.2.{code}" };
        _feed.Files[release.ApkUrl] = apk;
        return (release, apk);
    }

    private UpdateState StoredState() =>
        JsonSerializer.Deserialize<UpdateState>(_preferences.Values[AppUpdateService.StateKey], SkyJson.Options)!;

    // ---- Downloads ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_newer_build_downloads_verifies_and_waits_ready_while_the_app_is_in_use()
    {
        var (_, apk) = Publish();
        var service = Service();

        await service.RunOnceAsync(UpdateTrigger.Start, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Ready);
        service.Status.Release!.VersionCode.ShouldBe(57);
        service.Status.Progress.ShouldBe(1);
        service.Status.Required.ShouldBeFalse();
        _installer.Packages[Package].ShouldBe(apk);
        StoredState().PackageVerified.ShouldBeTrue();
        _installer.Installs.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_interrupted_download_resumes_with_a_range_from_what_is_stored()
    {
        var (release, apk) = Publish();
        _feed.CutAfter = 100_000;
        var service = Service();

        await service.RunOnceAsync(UpdateTrigger.Start, default);
        service.Status.Phase.ShouldBe(AppUpdatePhase.Failed);
        service.Status.Error.ShouldBe(ClientErrorCodes.Network);
        _installer.Packages[Package].Length.ShouldBe(100_000);

        // Back-off pending: an automatic run waits, the user's "Download" goes ahead.
        await service.RunOnceAsync(UpdateTrigger.Continue, default);
        _feed.Opens.Count.ShouldBe(1);
        await service.DownloadNowAsync();
        await service.RunOnceAsync(UpdateTrigger.Continue, default);

        _feed.Opens.ShouldBe([(release.ApkUrl, 0L), (release.ApkUrl, 100_000L)]);
        service.Status.Phase.ShouldBe(AppUpdatePhase.Ready);
        _installer.Packages[Package].ShouldBe(apk);
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_rewrites_the_package_from_the_start()
    {
        var (_, apk) = Publish();
        _feed.HonourRanges = false;
        _feed.CutAfter = 120_000;
        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);

        await service.DownloadNowAsync();
        await service.RunOnceAsync(UpdateTrigger.Continue, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Ready);
        _installer.Packages[Package].ShouldBe(apk);
    }

    [Fact]
    public async Task A_package_that_fails_its_hash_is_deleted_and_retried_only_after_the_back_off()
    {
        var (release, apk) = Publish();
        var tampered = apk.ToArray();
        tampered[4242] ^= 0xFF;
        _feed.Files[release.ApkUrl] = tampered;
        var service = Service();

        await service.RunOnceAsync(UpdateTrigger.Start, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Failed);
        service.Status.Error.ShouldBe(AppUpdateErrors.Integrity);
        _installer.Packages.ShouldBeEmpty();
        StoredState().PackageVersionCode.ShouldBe(0);
        await service.RunOnceAsync(UpdateTrigger.Continue, default);
        _feed.Opens.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_rebuilt_release_with_another_hash_replaces_the_stored_package()
    {
        Publish();
        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);
        var (_, rebuilt) = Publish(size: 280_000);

        await service.RunOnceAsync(UpdateTrigger.Manual, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Ready);
        _installer.Packages[Package].ShouldBe(rebuilt);
        _feed.Opens.Select(o => o.Offset).ShouldBe([0L, 0L]);
    }

    [Fact]
    public async Task Wifi_only_waits_on_mobile_data_until_the_user_asks()
    {
        Publish();
        _settings.Updates.WifiOnly = true;
        _network.Kind = NetworkKind.Cellular;
        _network.IsMetered = true;
        var service = Service();

        await service.RunOnceAsync(UpdateTrigger.Start, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Available);
        service.Status.WaitingForWifi.ShouldBeTrue();
        _feed.Opens.ShouldBeEmpty();

        await service.DownloadNowAsync();
        await service.RunOnceAsync(UpdateTrigger.Continue, default);
        service.Status.Phase.ShouldBe(AppUpdatePhase.Ready);
    }

    [Fact]
    public async Task A_phone_hotspot_counts_as_mobile_data()
    {
        Publish();
        _settings.Updates.WifiOnly = true;
        _network.IsMetered = true;
        var service = Service();

        await service.RunOnceAsync(UpdateTrigger.Start, default);

        service.Status.WaitingForWifi.ShouldBeTrue();
        _feed.Opens.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_auto_update_off_the_build_is_only_offered()
    {
        Publish();
        _settings.Updates.AutoUpdate = false;
        var service = Service();

        await service.RunOnceAsync(UpdateTrigger.Start, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Available);
        service.Status.WaitingForWifi.ShouldBeFalse();
        _feed.Opens.ShouldBeEmpty();
    }

    [Fact]
    public async Task Offline_nothing_is_downloaded()
    {
        Publish();
        _network.IsConnected = false;
        _network.Kind = NetworkKind.None;
        var service = Service();

        await service.RunOnceAsync(UpdateTrigger.Start, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Available);
        service.Status.WaitingForWifi.ShouldBeFalse();
        _feed.Opens.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_check_that_found_nothing_leaves_wifi_only_in_force_for_the_next_release()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        _time = clock;
        _settings.Updates.WifiOnly = true;
        _network.Kind = NetworkKind.Cellular;
        _network.IsMetered = true;
        var service = Service();
        await service.CheckNowAsync();
        service.Status.Phase.ShouldBe(AppUpdatePhase.UpToDate);

        Publish();
        clock.Advance(TimeSpan.FromHours(5));
        await service.RunOnceAsync(UpdateTrigger.Periodic, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Available);
        service.Status.WaitingForWifi.ShouldBeTrue();
        _feed.Opens.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_check_that_found_nothing_leaves_auto_update_off_in_force_for_the_next_release()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        _time = clock;
        _settings.Updates.AutoUpdate = false;
        var service = Service();
        await service.CheckNowAsync();

        Publish();
        clock.Advance(TimeSpan.FromHours(5));
        await service.RunOnceAsync(UpdateTrigger.Periodic, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Available);
        _feed.Opens.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_manual_check_offers_what_it_finds_within_the_settings()
    {
        Publish();
        _settings.Updates.WifiOnly = true;
        _network.Kind = NetworkKind.Cellular;
        _network.IsMetered = true;
        var service = Service();

        await service.CheckNowAsync();
        await service.RunOnceAsync(UpdateTrigger.Continue, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Available);
        service.Status.WaitingForWifi.ShouldBeTrue();
        _feed.Opens.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_the_user_asked_for_does_not_carry_over_to_the_next_release()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        _time = clock;
        Publish();
        _settings.Updates.WifiOnly = true;
        _network.Kind = NetworkKind.Cellular;
        _network.IsMetered = true;
        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);
        await service.DownloadNowAsync();
        await service.RunOnceAsync(UpdateTrigger.Continue, default);
        service.Status.Phase.ShouldBe(AppUpdatePhase.Ready);

        Publish(code: 58);
        clock.Advance(TimeSpan.FromHours(5));
        await service.RunOnceAsync(UpdateTrigger.Periodic, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Available);
        service.Status.Release!.VersionCode.ShouldBe(58);
        _feed.Opens.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_request_for_one_build_does_not_download_the_build_that_replaced_it()
    {
        Publish();
        _settings.Updates.AutoUpdate = false;
        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);
        await service.DownloadNowAsync();

        // The server moved on before the download started.
        Publish(code: 58);
        await service.RunOnceAsync(UpdateTrigger.Manual, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Available);
        service.Status.Release!.VersionCode.ShouldBe(58);
        _feed.Opens.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_installed_build_is_up_to_date_and_a_withdrawn_release_is_forgotten()
    {
        Publish();
        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);
        service.Status.Phase.ShouldBe(AppUpdatePhase.Ready);

        _feed.Version = new AppVersionDto { ServerVersion = "0.2.50" };
        await service.RunOnceAsync(UpdateTrigger.Manual, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.UpToDate);
        service.Status.Release.ShouldBeNull();
        _installer.Packages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_build_below_the_servers_minimum_must_update()
    {
        Publish(min: 55);

        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);

        service.Status.Required.ShouldBeTrue();
    }

    [Fact]
    public async Task A_server_from_before_app_updates_offers_nothing_without_an_error()
    {
        _feed.VersionError = new ApiException(404, ErrorCodes.NotFound);
        var service = Service();

        await service.CheckNowAsync();

        service.Status.Phase.ShouldBe(AppUpdatePhase.UpToDate);
        service.Status.Error.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_manual_check_reports_why_and_never_throws()
    {
        var service = Service();
        _feed.VersionError = new ApiException(0, ClientErrorCodes.Network);
        await service.CheckNowAsync();
        service.Status.Error.ShouldBe(ClientErrorCodes.Network);
        service.Status.IsChecking.ShouldBeFalse();

        _feed.VersionError = new InvalidOperationException("boom");
        await service.CheckNowAsync();
        service.Status.Error.ShouldBe(ClientErrorCodes.Unknown);
    }

    // ---- Installing ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_hands_the_verified_package_to_the_system_installer()
    {
        Publish();
        _installer.Results.Enqueue(new AppInstallResult(AppInstallStatus.Pending));
        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);

        await service.InstallNowAsync();

        _installer.Installs.ShouldBe([Package]);
        service.Status.Phase.ShouldBe(AppUpdatePhase.Installing);
        StoredState().InstallRequestedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_declined_confirmation_keeps_the_update_ready_and_stops_silent_attempts()
    {
        // Started for real (the status arrives through the installer's event); delays and timers fire at once.
        _time = new InstantTime();
        Publish();
        _installer.Results.Enqueue(new AppInstallResult(AppInstallStatus.Pending));
        var service = Service();
        service.Start();
        await Wait.UntilAsync(() => service.Status.Phase == AppUpdatePhase.Ready, "downloaded");
        await service.InstallNowAsync();
        _installer.Installs.Count.ShouldBe(1);

        _installer.RaiseStatus(new AppInstallResult(AppInstallStatus.Cancelled));
        await Wait.UntilAsync(() => service.Status.Phase == AppUpdatePhase.Ready);
        StoredState().ConfirmVersionCode.ShouldBe(57);

        _lifecycle.SetForeground(false);
        await Task.Delay(200);
        _installer.Installs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_package_android_refuses_for_good_is_not_downloaded_again()
    {
        Publish();
        _installer.Results.Enqueue(new AppInstallResult(AppInstallStatus.Failed, AppUpdateErrors.Conflict));
        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);

        await service.InstallNowAsync();

        service.Status.Phase.ShouldBe(AppUpdatePhase.Failed);
        service.Status.Error.ShouldBe(AppUpdateErrors.Conflict);
        _installer.Packages.ShouldBeEmpty();
        await service.RunOnceAsync(UpdateTrigger.Start, default);
        service.Status.Error.ShouldBe(AppUpdateErrors.Conflict);
        _feed.Opens.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_install_is_not_retried_before_its_back_off()
    {
        Publish();
        _installer.Results.Enqueue(new AppInstallResult(AppInstallStatus.Failed, AppUpdateErrors.InstallFailed));
        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);
        await service.InstallNowAsync();
        service.Status.Phase.ShouldBe(AppUpdatePhase.Failed);

        await service.RunOnceAsync(UpdateTrigger.Continue, default);

        service.Status.Phase.ShouldBe(AppUpdatePhase.Failed);
        _installer.Installs.Count.ShouldBe(1);
        _feed.Opens.Count.ShouldBe(1);
    }

    [Fact]
    public async Task In_the_background_a_ready_update_installs_by_itself()
    {
        _time = new InstantTime();
        _lifecycle.IsForeground = false;
        Publish();
        var service = Service();

        await service.RunOnceAsync(UpdateTrigger.Start, default);

        await Wait.UntilAsync(() => _installer.Installs.Count == 1, "background install");
    }

    [Fact]
    public async Task No_silent_install_during_a_call_or_where_android_would_ask()
    {
        _time = new InstantTime();
        _lifecycle.IsForeground = false;
        _calls.Current.Returns(new CallSession { Call = new CallDto() });
        Publish();
        var service = Service();
        await service.RunOnceAsync(UpdateTrigger.Start, default);
        await Task.Delay(200);
        _installer.Installs.ShouldBeEmpty();

        _calls.Current.Returns((CallSession?)null);
        _installer.CanInstallSilently = false;
        await service.RunOnceAsync(UpdateTrigger.Continue, default);
        await Task.Delay(200);
        _installer.Installs.ShouldBeEmpty();
    }

    // ---- Restarts -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_verified_package_survives_a_restart_without_downloading_again()
    {
        Publish();
        await Service().RunOnceAsync(UpdateTrigger.Start, default);

        var restarted = Service();
        await restarted.RunOnceAsync(UpdateTrigger.Continue, default);

        restarted.Status.Phase.ShouldBe(AppUpdatePhase.Ready);
        _feed.Opens.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_updated_build_cleans_up_the_package_it_was_installed_from()
    {
        Publish();
        await Service().RunOnceAsync(UpdateTrigger.Start, default);

        _installer.InstalledVersionCode = 57;
        var updated = Service();
        await updated.RunOnceAsync(UpdateTrigger.Continue, default);

        updated.Status.Phase.ShouldBe(AppUpdatePhase.UpToDate);
        updated.Status.Release.ShouldBeNull();
        _installer.Packages.ShouldBeEmpty();
        StoredState().PackageVersionCode.ShouldBe(0);
    }

    [Fact]
    public async Task Hosts_without_self_updates_do_nothing_but_watch_the_web_client()
    {
        _platform.IsNativeApp.Returns(true);
        var service = Service(new UnsupportedAppUpdateInstaller());

        await service.CheckNowAsync();

        service.CanSelfUpdate.ShouldBeFalse();
        _feed.VersionRequests.ShouldBe(0);
    }

    // ---- Web ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_hidden_web_page_reloads_once_for_a_new_server_build()
    {
        _time = new InstantTime();
        _platform.IsNativeApp.Returns(false);
        _platform.AppVersion.Returns("0.2.56");
        _lifecycle.IsForeground = false;
        _feed.Version = new AppVersionDto { Web = new WebReleaseDto { Version = "0.2.57" }, ServerVersion = "0.2.57" };
        var service = Service(new UnsupportedAppUpdateInstaller());
        var reloads = 0;
        service.ReloadRequested += () => Interlocked.Increment(ref reloads);

        await service.RunOnceAsync(UpdateTrigger.Start, default);

        await Wait.UntilAsync(() => Volatile.Read(ref reloads) == 1, "reload");
        service.Status.NewWebVersion.ShouldBe("0.2.57");

        // The reload brought the old client back (a cache in the way): no reload loop.
        var reloaded = Service(new UnsupportedAppUpdateInstaller());
        var again = 0;
        reloaded.ReloadRequested += () => Interlocked.Increment(ref again);
        await reloaded.RunOnceAsync(UpdateTrigger.Start, default);
        await Task.Delay(200);
        again.ShouldBe(0);
        reloaded.Status.NewWebVersion.ShouldBeNull();
    }

    [Fact]
    public async Task A_visible_web_page_offers_the_new_build_instead_of_reloading()
    {
        _time = new InstantTime();
        _platform.IsNativeApp.Returns(false);
        _platform.AppVersion.Returns("0.2.56");
        _feed.Version = new AppVersionDto { Web = new WebReleaseDto { Version = "0.2.57" }, ServerVersion = "0.2.57" };
        var service = Service(new UnsupportedAppUpdateInstaller());
        var reloads = 0;
        service.ReloadRequested += () => Interlocked.Increment(ref reloads);

        await service.RunOnceAsync(UpdateTrigger.Start, default);
        await Task.Delay(200);
        reloads.ShouldBe(0);
        service.Status.NewWebVersion.ShouldBe("0.2.57");

        service.ReloadWebNow();
        await Wait.UntilAsync(() => Volatile.Read(ref reloads) == 1);
    }

    [Fact]
    public async Task The_web_client_the_server_serves_is_not_an_update()
    {
        _platform.IsNativeApp.Returns(false);
        _platform.AppVersion.Returns("0.2.57");
        _feed.Version = new AppVersionDto { Web = new WebReleaseDto { Version = "0.2.57" }, ServerVersion = "0.2.57" };
        var service = Service(new UnsupportedAppUpdateInstaller());

        await service.RunOnceAsync(UpdateTrigger.Start, default);

        service.Status.NewWebVersion.ShouldBeNull();
        service.Status.Phase.ShouldBe(AppUpdatePhase.UpToDate);
    }

    // ---- Fakes --------------------------------------------------------------------------------------------------

    private sealed class FakeFeed : IAppUpdateFeed
    {
        public AppVersionDto Version { get; set; } = new();
        public Exception? VersionError { get; set; }
        public Dictionary<string, byte[]> Files { get; } = [];
        public bool HonourRanges { get; set; } = true;
        /// <summary>The next download breaks after this many bytes.</summary>
        public int? CutAfter { get; set; }
        public List<(string Url, long Offset)> Opens { get; } = [];
        public int VersionRequests;

        public Task<AppVersionDto> GetVersionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref VersionRequests);
            return VersionError is { } error ? Task.FromException<AppVersionDto>(error) : Task.FromResult(Version);
        }

        public Task<PackageDownload> OpenPackageAsync(string apkUrl, long offset, CancellationToken cancellationToken)
        {
            lock (Opens) Opens.Add((apkUrl, offset));
            var bytes = Files[apkUrl];
            var start = HonourRanges ? (int)offset : 0;
            Stream body = new MemoryStream(bytes, start, bytes.Length - start, writable: false);
            if (CutAfter is { } cut)
            {
                CutAfter = null;
                body = new BreakingStream(body, cut);
            }
            return Task.FromResult(new PackageDownload(start, body));
        }
    }

    /// <summary>A connection that drops after <c>limit</c> bytes.</summary>
    private sealed class BreakingStream(Stream inner, int limit) : Stream
    {
        private int _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= limit) throw new IOException("connection reset");
            var read = inner.Read(buffer, offset, Math.Min(count, limit - _read));
            _read += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= limit) throw new IOException("connection reset");
            var read = inner.Read(buffer.Span[..Math.Min(buffer.Length, limit - _read)]);
            _read += read;
            return ValueTask.FromResult(read);
        }
    }

    private sealed class FakeInstaller : IAppUpdateInstaller
    {
        public ConcurrentDictionary<string, byte[]> Packages { get; } = new();
        public ConcurrentQueue<AppInstallResult> Results { get; } = new();
        public List<string> Installs { get; } = [];

        public bool IsSupported => true;
        public int InstalledVersionCode { get; set; }
        public bool CanInstallPackages { get; set; } = true;
        public bool CanInstallSilently { get; set; } = true;

        public event Action<AppInstallResult>? InstallStatusChanged;

        public void RaiseStatus(AppInstallResult result) => InstallStatusChanged?.Invoke(result);

        public Task<bool> RequestInstallPermissionAsync() => Task.FromResult(CanInstallPackages = true);

        public Task<long> GetPackageLengthAsync(string fileName) =>
            Task.FromResult(Packages.TryGetValue(fileName, out var bytes) ? bytes.Length : -1L);

        public Task<Stream> OpenPackageWriteAsync(string fileName, long offset, CancellationToken cancellationToken = default)
        {
            var existing = Packages.TryGetValue(fileName, out var bytes) ? bytes : [];
            var stream = new PackageStream(data => Packages[fileName] = data);
            stream.Write(existing, 0, (int)Math.Min(offset, existing.Length));
            Packages[fileName] = stream.ToArray();
            return Task.FromResult<Stream>(stream);
        }

        public Task<Stream?> OpenPackageReadAsync(string fileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(Packages.TryGetValue(fileName, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

        public Task DeletePackagesAsync(string? keep = null)
        {
            foreach (var name in Packages.Keys.Where(k => k != keep).ToList()) Packages.TryRemove(name, out _);
            return Task.CompletedTask;
        }

        public Task<AppInstallResult> InstallAsync(string fileName, CancellationToken cancellationToken = default)
        {
            lock (Installs) Installs.Add(fileName);
            return Task.FromResult(Results.TryDequeue(out var result) ? result : new AppInstallResult(AppInstallStatus.Pending));
        }

        /// <summary>Keeps what was written even when the writer stops halfway (a file on disk would).</summary>
        private sealed class PackageStream(Action<byte[]> store) : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                base.Write(buffer, offset, count);
                store(ToArray());
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.ToArray(), 0, buffer.Length);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeNetwork : INetworkStatusService
    {
        public bool IsConnected { get; set; } = true;
        public NetworkKind Kind { get; set; } = NetworkKind.Wifi;
        public bool IsMetered { get; set; }
        public event Action? Changed;

        public void RaiseChanged() => Changed?.Invoke();
    }

    private sealed class FakeLifecycle : IAppLifecycle
    {
        public bool IsForeground { get; set; }
        public event Action<bool>? ForegroundChanged;

        public void SetForeground(bool value)
        {
            IsForeground = value;
            ForegroundChanged?.Invoke(value);
        }
    }
}
