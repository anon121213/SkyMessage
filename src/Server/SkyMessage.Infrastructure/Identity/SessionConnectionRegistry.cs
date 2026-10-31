using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SkyMessage.Application.Identity;

namespace SkyMessage.Infrastructure.Identity;

/// <summary>
/// Session id → live hub connections, filled by the realtime hub. Lets a revocation cut a device off at once
/// (hub connections otherwise outlive their access token).
/// </summary>
public sealed class SessionConnectionRegistry(ILogger<SessionConnectionRegistry> logger) : ISessionConnections
{
    /// <summary>Time for <c>SessionRevoked</c> to reach the client before its connections are cut.</summary>
    public static readonly TimeSpan AbortDelay = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Dictionary<string, HubCallerContext>> _sessions = new();

    public void Add(Guid sessionId, HubCallerContext connection)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var connections))
                _sessions[sessionId] = connections = new Dictionary<string, HubCallerContext>(StringComparer.Ordinal);
            connections[connection.ConnectionId] = connection;
        }
    }

    public void Remove(Guid sessionId, string connectionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var connections) && connections.Remove(connectionId) && connections.Count == 0)
                _sessions.Remove(sessionId);
        }
    }

    public int CountFor(Guid sessionId)
    {
        lock (_gate)
            return _sessions.TryGetValue(sessionId, out var connections) ? connections.Count : 0;
    }

    public void AbortSession(Guid sessionId) => _ = AbortLaterAsync(sessionId);

    /// <summary>Aborts the session's connections immediately (also those that registered after the revoke).</summary>
    public int AbortNow(Guid sessionId)
    {
        HubCallerContext[] connections;
        lock (_gate)
            connections = _sessions.TryGetValue(sessionId, out var registered) ? registered.Values.ToArray() : [];
        foreach (var connection in connections)
            connection.Abort();
        return connections.Length;
    }

    private async Task AbortLaterAsync(Guid sessionId)
    {
        try
        {
            await Task.Delay(AbortDelay);
            var aborted = AbortNow(sessionId);
            if (aborted > 0)
                logger.LogInformation("Aborted {Count} realtime connections of revoked session {SessionId}", aborted, sessionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to abort the connections of session {SessionId}", sessionId);
        }
    }
}
