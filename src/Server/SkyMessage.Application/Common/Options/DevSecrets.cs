namespace SkyMessage.Application.Common.Options;

/// <summary>
/// Secrets committed to the repo for local development. The API refuses to start outside the
/// Development/Docker-dev environments if any configured secret equals one of these.
/// </summary>
public static class DevSecrets
{
    public const string JwtSigningKey = "dev-only-signing-key-change-me-0123456789abcdef";
    public const string StorageMasterKey = "ZGV2LW9ubHktbWFzdGVyLWtleS0zMi1ieXRlcyEhISE=";
    public const string LiveKitApiKey = "devkey";
    public const string LiveKitApiSecret = "devsecret-change-me-0123456789abcdef0123456789";

    public static bool IsDevSecret(string? value) =>
        value is JwtSigningKey or StorageMasterKey or LiveKitApiSecret;
}
