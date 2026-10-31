using CoreFoundation;
using Microsoft.Extensions.Logging;
using Network;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Apple;

/// <summary>
/// NWPathMonitor-based status. "Connected" = the path is satisfied (an interface with a route is up), which is
/// not an internet reachability check — filtered networks still count. Expensive/constrained paths are metered.
/// </summary>
public sealed class AppleNetworkStatusService : INetworkStatusService
{
    private readonly ILogger<AppleNetworkStatusService> _logger;
    private readonly NWPathMonitor _monitor;
    private readonly object _gate = new();
    private Snapshot _current = new(true, NetworkKind.Unknown, false);
    private Action? _handlers;

    public AppleNetworkStatusService(ILogger<AppleNetworkStatusService> logger)
    {
        _logger = logger;
        _monitor = new NWPathMonitor();
        _monitor.SnapshotHandler = OnPath;
        _monitor.SetQueue(new DispatchQueue("com.skymessage.network"));
        _monitor.Start();
    }

    public bool IsConnected => _current.Connected;

    public NetworkKind Kind => _current.Kind;

    public bool IsMetered => _current.Metered;

    public event Action? Changed
    {
        add { lock (_gate) _handlers += value; }
        remove { lock (_gate) _handlers -= value; }
    }

    private void OnPath(NWPath path)
    {
        Snapshot next;
        try
        {
            var connected = path.Status == NWPathStatus.Satisfied;
            var kind = !connected ? NetworkKind.None
                : path.UsesInterfaceType(NWInterfaceType.Wifi) ? NetworkKind.Wifi
                : path.UsesInterfaceType(NWInterfaceType.Cellular) ? NetworkKind.Cellular
                : path.UsesInterfaceType(NWInterfaceType.Wired) ? NetworkKind.Ethernet
                : NetworkKind.Other;
            next = new Snapshot(connected, kind, connected && (path.IsExpensive || path.IsConstrained));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Network path inspection failed");
            next = new Snapshot(true, NetworkKind.Unknown, false);
        }

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

    private readonly record struct Snapshot(bool Connected, NetworkKind Kind, bool Metered);
}
