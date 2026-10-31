using System.Buffers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Options;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

[ApiController]
public sealed class ServerController(ServerInfoService serverInfo, IOptions<ServerOptions> server, IWebHostEnvironment environment) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet(ApiRoutes.ServerInfo)]
    public Task<ServerInfoDto> Info(CancellationToken ct) => serverInfo.GetAsync(ct);

    /// <summary>
    /// Identity proof for failover candidates. The host is the one the client addressed (Host header, no
    /// port), so a proof obtained through one address cannot be replayed for another.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.UsernameCheck)]
    [HttpGet(ApiRoutes.ServerProof)]
    public Task<ServerProofDto> Proof([FromQuery] Guid sid, [FromQuery] string? nonce, CancellationToken ct)
    {
        // Only for hosts this deployment serves: otherwise an impostor could relay the request with its own
        // Host header and present our proof for its name.
        var host = Request.Host.Host;
        if (!IsServedHost(host)) throw new NotFoundException();
        return serverInfo.GetProofAsync(sid, nonce, host, ct);
    }

    private bool IsServedHost(string host)
    {
        var options = server.Value;
        var configured = new[] { options.PublicBaseUrl }.Concat(options.MirrorUrls)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        if (configured) return true;

        // Local development: localhost, the emulator alias 10.0.2.2 and LAN addresses (phones on Wi-Fi).
        return environment.IsDevelopment()
               && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                   || (System.Net.IPAddress.TryParse(host, out var ip) && (System.Net.IPAddress.IsLoopback(ip) || IsPrivate(ip))));
    }

    private static bool IsPrivate(System.Net.IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>Random, incompressible bytes for throughput probes (some networks pass handshakes but stall transfers).</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.UsernameCheck)]
    [HttpGet(ApiRoutes.ServerProbe)]
    public async Task Probe([FromQuery] int? size, CancellationToken ct)
    {
        var length = ServerInfoService.ClampProbeSize(size);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Random.Shared.NextBytes(buffer.AsSpan(0, length));
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = length;
            Response.Headers.CacheControl = "no-store, no-transform";
            await Response.Body.WriteAsync(buffer.AsMemory(0, length), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
