using SkyMessage.Application.Common.Interfaces;

namespace SkyMessage.UnitTests.Calls;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal static class LiveKitDev
{
    /// <summary>The development key pair from docker/livekit and appsettings.Development.json.</summary>
    public const string ApiKey = "devkey";
    public const string ApiSecret = "devsecret-change-me-0123456789abcdef0123456789";
}
