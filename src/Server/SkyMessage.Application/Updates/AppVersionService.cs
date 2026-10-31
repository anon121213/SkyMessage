using SkyMessage.Application.Common;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Updates;

/// <summary>The release files CI publishes into the updates directory (Infrastructure reads them from disk).</summary>
public interface IReleaseStore
{
    /// <summary>The current manifest, null when there is none or it cannot be read.</summary>
    ReleaseManifest? ReadManifest();

    /// <summary>Length of a file in the updates directory, null when it does not exist (or the name is unsafe).</summary>
    long? PackageLength(string fileName);
}

/// <summary>What api/app/version answers: the newest Android build, the served web client and the server version.</summary>
public sealed class AppVersionService(IReleaseStore releases)
{
    /// <param name="servesWebClient">Whether this host serves the web client (its version is the server's own build).</param>
    public AppVersionDto Get(bool servesWebClient) => new()
    {
        Android = LatestAndroid(),
        // The web client is built and published together with the server, so it carries the same version.
        Web = servesWebClient ? new WebReleaseDto { Version = ServerBuild.Version } : null,
        ServerVersion = ServerBuild.Version,
    };

    /// <summary>The Android build clients may install now, null when none is published.</summary>
    public AndroidReleaseDto? LatestAndroid() => ReleaseManifestRules.ToDto(releases.ReadManifest()?.Android, releases.PackageLength);
}
