using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace SkyMessage.App.Services;

/// <summary>
/// RFC 8305-style connection racing for <see cref="SocketsHttpHandler.ConnectCallback"/>: addresses are
/// interleaved IPv6-first, a new attempt starts every 250 ms (or immediately after a failure) and the first
/// established socket wins. Broken IPv6 on mobile networks therefore costs 250 ms instead of a full timeout.
/// </summary>
internal static class HappyEyeballs
{
    private static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(10);

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsCts.CancelAfter(DnsTimeout);
            IPAddress[] resolved;
            try
            {
                resolved = await Dns.GetHostAddressesAsync(endpoint.Host, dnsCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SocketException((int)SocketError.TimedOut);
            }
            addresses = Interleave(resolved);
        }
        if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        var socket = await RaceAsync(addresses, endpoint.Port, cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    private static async Task<Socket> RaceAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = new List<Task<Socket>>();
        Exception? lastError = null;
        var next = 0;

        while (true)
        {
            if (next < addresses.Count) attempts.Add(AttemptAsync(addresses[next++], port, raceCts.Token));
            if (attempts.Count == 0) break;

            var anyAttempt = Task.WhenAny(attempts);
            Task completed;
            if (next < addresses.Count)
            {
                var delay = Task.Delay(AttemptDelay, raceCts.Token);
                completed = await Task.WhenAny(anyAttempt, delay).ConfigureAwait(false);
                if (completed == delay)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    continue; // Nothing yet: start the next address in parallel.
                }
            }
            var finished = await anyAttempt.ConfigureAwait(false);
            attempts.Remove(finished);

            if (finished.IsCompletedSuccessfully)
            {
                raceCts.Cancel();
                foreach (var loser in attempts) DisposeWhenDone(loser);
                return finished.Result;
            }
            cancellationToken.ThrowIfCancellationRequested();
            lastError = finished.Exception?.GetBaseException() ?? lastError;
        }

        throw lastError as SocketException
              ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            TryEnableKeepAlive(socket);
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(AttemptTimeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), attemptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SocketException((int)SocketError.TimedOut);
            }
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void DisposeWhenDone(Task<Socket> attempt) =>
        attempt.ContinueWith(static t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
            else _ = t.Exception; // observed
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static void TryEnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception)
        {
            // Not every platform exposes every TCP keep-alive knob.
        }
    }

    /// <summary>IPv6 first, then alternate families, keeping the resolver's order within each family.</summary>
    internal static IReadOnlyList<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        var v6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList();
        var v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        var result = new List<IPAddress>(addresses.Count);
        for (var i = 0; i < Math.Max(v6.Count, v4.Count); i++)
        {
            if (i < v6.Count) result.Add(v6[i]);
            if (i < v4.Count) result.Add(v4[i]);
        }
        return result;
    }
}
