using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>Process-wide cache of the deployment's instance id (it never changes once stored).</summary>
public sealed class ServerInstance
{
    private volatile StrongBox<Guid>? _id;

    public Guid? Id => _id?.Value;

    public void Set(Guid id) => _id ??= new StrongBox<Guid>(id);
}

/// <summary>Public facts about this deployment, identity proofs for failover and throughput probes.</summary>
public sealed class ServerInfoService(
    IAppDbContext db,
    ServerInstance instance,
    IOptions<ServerOptions> serverOptions,
    IOptions<StorageOptions> storageOptions,
    IOptions<LiveKitOptions> liveKitOptions,
    IClock clock,
    ILogger<ServerInfoService> logger)
{
    public const int ApiVersion = 1;
    public const int ProbeMaxBytes = 256 * 1024;
    public const int ProbeDefaultBytes = 64 * 1024;
    public const int NonceMinLength = 8;
    public const int NonceMaxLength = 128;

    private static readonly string ServerVersion = ReadVersion();

    public async Task<ServerInfoDto> GetAsync(CancellationToken ct)
    {
        var server = serverOptions.Value;
        var storage = storageOptions.Value;
        var callsEnabled = CallsEnabled(liveKitOptions.Value);

        List<string> features =
        [
            "groups", "media", "voice-messages", "video-notes", "reactions", "pins", "invite-links",
            "contacts", "phone-linking", "media-signatures", "server-proof",
        ];
        if (callsEnabled)
            features.AddRange(["calls", "video-calls", "group-calls"]);

        return new ServerInfoDto
        {
            InstanceId = await GetInstanceIdAsync(ct),
            Name = string.IsNullOrWhiteSpace(server.Name) ? "SkyMessage" : server.Name,
            Version = ServerVersion,
            ApiVersion = ApiVersion,
            MinClientVersion = server.MinClientVersion,
            PublicBaseUrl = string.IsNullOrWhiteSpace(server.PublicBaseUrl) ? null : server.PublicBaseUrl,
            RegistrationMode = server.RegistrationMode,
            MaxUploadBytes = storage.MaxUploadBytes,
            ChunkSize = storage.ChunkSize,
            CallsEnabled = callsEnabled,
            ServerTime = clock.UtcNow,
            Features = features,
            MirrorUrls = server.MirrorUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct().ToList(),
        };
    }

    /// <summary>
    /// Proves this server holds the session's media key (an impostor copying <c>InstanceId</c> cannot):
    /// Proof = <see cref="MediaSignature.CreateProof"/>(key, nonce, host). Unknown or ended sessions → 404.
    /// </summary>
    /// <param name="host">Host name the client addressed (Host header, without port).</param>
    public async Task<ServerProofDto> GetProofAsync(Guid sessionId, string? nonce, string host, CancellationToken ct)
    {
        var trimmedNonce = nonce?.Trim() ?? "";
        if (trimmedNonce.Length is < NonceMinLength or > NonceMaxLength || string.IsNullOrEmpty(host))
            throw new BadRequestException(ErrorCodes.Validation);

        var now = clock.UtcNow;
        var key = await db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.RevokedAt == null && s.RefreshTokenExpiresAt > now)
            .Select(s => s.MediaKey)
            .FirstOrDefaultAsync(ct);
        if (key is not { Length: MediaSignature.KeyLength })
            throw new NotFoundException();

        return new ServerProofDto
        {
            InstanceId = await GetInstanceIdAsync(ct),
            Proof = MediaSignature.CreateProof(key, trimmedNonce, host),
        };
    }

    public static int ClampProbeSize(int? size) => Math.Clamp(size ?? ProbeDefaultBytes, 1, ProbeMaxBytes);

    /// <summary>Reads the stored instance id, creating it on first use (race-safe on the primary key).</summary>
    public async Task<Guid> GetInstanceIdAsync(CancellationToken ct)
    {
        if (instance.Id is { } known) return known;

        var id = await ReadInstanceIdAsync(ct);
        if (id is null)
        {
            var created = new ServerProperty { Key = ServerProperty.InstanceIdKey, Value = Guid.CreateVersion7().ToString() };
            db.ServerProperties.Add(created);
            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Generated server instance id {InstanceId}", created.Value);
            }
            catch (DbUpdateException)
            {
                // Another request (or node) stored it first: theirs wins.
                db.ChangeTracker.Entries<ServerProperty>().ToList().ForEach(e => e.State = EntityState.Detached);
            }
            id = await ReadInstanceIdAsync(ct) ?? throw new InvalidOperationException("The server instance id could not be stored.");
        }

        instance.Set(id.Value);
        return id.Value;
    }

    private async Task<Guid?> ReadInstanceIdAsync(CancellationToken ct)
    {
        var value = await db.ServerProperties.AsNoTracking()
            .Where(p => p.Key == ServerProperty.InstanceIdKey)
            .Select(p => p.Value)
            .FirstOrDefaultAsync(ct);
        if (value is null) return null;
        // Never regenerate: clients would take a new id for a different server and sign out.
        return Guid.TryParse(value, out var id)
            ? id
            : throw new InvalidOperationException($"server_properties['{ServerProperty.InstanceIdKey}'] is not a GUID.");
    }

    // Same rule as the LiveKit token service: HS256 needs a secret of at least 32 bytes, otherwise calls answer 503.
    private static bool CallsEnabled(LiveKitOptions options) =>
        options.Enabled && !string.IsNullOrWhiteSpace(options.ApiKey)
        && System.Text.Encoding.UTF8.GetByteCount(options.ApiSecret ?? "") >= 32;

    private static string ReadVersion()
    {
        var assembly = typeof(ServerInfoService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
            return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
