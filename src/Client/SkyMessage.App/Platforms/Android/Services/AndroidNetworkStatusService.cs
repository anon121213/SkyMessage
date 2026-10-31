using Android.Content;
using Android.Net;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>
/// Default-network tracking via <see cref="ConnectivityManager.NetworkCallback"/>. "Connected" means the
/// default network has the INTERNET capability — deliberately not VALIDATED, because OS validation fails on
/// filtered networks while our server may still be reachable. Changed fires only when (connected, kind,
/// metered) actually changes.
/// </summary>
public sealed class AndroidNetworkStatusService : INetworkStatusService
{
    private readonly ILogger<AndroidNetworkStatusService> _logger;
    private readonly ConnectivityManager? _connectivity;
    private readonly object _gate = new();
    private Snapshot _current;
    private Action? _handlers;

    public AndroidNetworkStatusService(ILogger<AndroidNetworkStatusService> logger)
    {
        _logger = logger;
        _connectivity = Android.App.Application.Context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
        _current = Compute();
        try
        {
            _connectivity?.RegisterDefaultNetworkCallback(new Callback(this));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Network callback registration failed; network status will not update");
        }
    }

    public bool IsConnected => _current.Connected;

    public NetworkKind Kind => _current.Kind;

    public bool IsMetered => _current.Metered;

    public event Action? Changed
    {
        add { lock (_gate) _handlers += value; }
        remove { lock (_gate) _handlers -= value; }
    }

    private void Refresh()
    {
        var next = Compute();
        Action? handlers;
        lock (_gate)
        {
            if (next == _current) return;
            _current = next;
            handlers = _handlers;
        }
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Network change handler failed");
            }
        }
    }

    private Snapshot Compute()
    {
        try
        {
            if (_connectivity is null) return new Snapshot(true, NetworkKind.Unknown, false);
            var network = _connectivity.ActiveNetwork;
            var caps = network is null ? null : _connectivity.GetNetworkCapabilities(network);
            if (caps is null || !caps.HasCapability(NetCapability.Internet))
            {
                // No default network yet (e.g. switching): any other network with INTERNET still counts.
#pragma warning disable CA1422 // Still the only way to enumerate networks without a callback per network.
                foreach (var other in _connectivity.GetAllNetworks() ?? [])
#pragma warning restore CA1422
                {
                    var otherCaps = _connectivity.GetNetworkCapabilities(other);
                    if (otherCaps is not null && otherCaps.HasCapability(NetCapability.Internet))
                        return new Snapshot(true, KindOf(otherCaps), !otherCaps.HasCapability(NetCapability.NotMetered));
                }
                return new Snapshot(false, NetworkKind.None, false);
            }
            return new Snapshot(true, KindOf(caps), !caps.HasCapability(NetCapability.NotMetered));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Network status query failed");
            return new Snapshot(true, NetworkKind.Unknown, false);
        }
    }

    private static NetworkKind KindOf(NetworkCapabilities caps)
    {
        if (caps.HasTransport(TransportType.Wifi)) return NetworkKind.Wifi;
        if (caps.HasTransport(TransportType.Cellular)) return NetworkKind.Cellular;
        if (caps.HasTransport(TransportType.Ethernet)) return NetworkKind.Ethernet;
        return NetworkKind.Other;
    }

    private readonly record struct Snapshot(bool Connected, NetworkKind Kind, bool Metered);

    private sealed class Callback(AndroidNetworkStatusService owner) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => owner.Refresh();

        public override void OnLost(Network network) => owner.Refresh();

        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities networkCapabilities) => owner.Refresh();

        public override void OnUnavailable() => owner.Refresh();
    }
}
