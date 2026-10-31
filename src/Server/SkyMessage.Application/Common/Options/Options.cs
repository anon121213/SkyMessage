using SkyMessage.Contracts;

namespace SkyMessage.Application.Common.Options;

/// <summary>Section "Server".</summary>
public sealed class ServerOptions
{
    public const string Section = "Server";
    public string Name { get; set; } = "SkyMessage";
    /// <summary>Public https base URL (used for profile/invite links). Optional.</summary>
    public string? PublicBaseUrl { get; set; }
    public RegistrationMode RegistrationMode { get; set; } = RegistrationMode.Open;
    /// <summary>Required code when <see cref="RegistrationMode"/> is InviteCode.</summary>
    public string? RegistrationCode { get; set; }
    /// <summary>Oldest supported client version (reported in server info).</summary>
    public string? MinClientVersion { get; set; }
    /// <summary>Other public addresses of this deployment (mirrors/relays) advertised to clients for failover.</summary>
    public string[] MirrorUrls { get; set; } = [];
    /// <summary>Extra CORS origins for a separately hosted web client (dev).</summary>
    public string[] CorsOrigins { get; set; } = [];
    /// <summary>
    /// Reverse proxies whose X-Forwarded-* headers are trusted, as CIDRs (e.g. the Caddy container network).
    /// Loopback is always trusted; nothing else by default.
    /// </summary>
    public string[] TrustedProxyNetworks { get; set; } = [];
    public bool MigrateOnStartup { get; set; } = true;
}

/// <summary>Section "Jwt".</summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "skymessage";
    public string Audience { get; set; } = "skymessage-clients";
    /// <summary>HMAC-SHA256 key, at least 32 bytes (UTF-8). Must be set outside of Development.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 180;
}

/// <summary>Section "Storage". Media bytes are stored in PostgreSQL chunks.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>Base64 32-byte key enabling encryption at rest (AES-256-GCM). Losing it loses all media.</summary>
    public string? MasterKey { get; set; }
    public int ChunkSize { get; set; } = Limits.DefaultChunkSize;
    public long MaxUploadBytes { get; set; } = Limits.DefaultMaxUploadBytes;
    /// <summary>Scratch directory for processing; defaults to {temp}/skymessage.</summary>
    public string? TempPath { get; set; }
    public int IncompleteUploadTtlHours { get; set; } = 24;
}

/// <summary>Section "MediaProcessing".</summary>
public sealed class MediaProcessingOptions
{
    public const string Section = "MediaProcessing";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public int MaxConcurrency { get; set; } = 2;
    /// <summary>Uploads already within limits (JPEG/PNG/WebP ≤ this) are kept as-is; larger ones are downscaled.</summary>
    public int PhotoMaxDimension { get; set; } = 4096;
    public int ThumbnailMaxDimension { get; set; } = 480;
    /// <summary>H.264/AAC MP4 within this size is only remuxed (faststart); others are transcoded.</summary>
    public int VideoMaxDimension { get; set; } = 1920;
    public int VideoCrf { get; set; } = 26;
    public int VideoNoteSize { get; set; } = 480;
}

/// <summary>Section "LiveKit".</summary>
public sealed class LiveKitOptions
{
    public const string Section = "LiveKit";
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    /// <summary>Server-to-LiveKit URL (API calls and the /livekit reverse proxy), e.g. http://livekit:7880.</summary>
    public string InternalUrl { get; set; } = "http://localhost:7880";
    /// <summary>What clients connect to: absolute ws(s):// URL or a path proxied by the API.</summary>
    public string PublicUrl { get; set; } = HubRoutes.LiveKitProxy;
    /// <summary>Only needed to connect (LiveKit refreshes connected clients itself), so keep it short.</summary>
    public int TokenTtlMinutes { get; set; } = 15;
}

/// <summary>Section "Push".</summary>
public sealed class PushOptions
{
    public const string Section = "Push";
    public bool Enabled { get; set; }
    public string? FcmServiceAccountPath { get; set; }
}
