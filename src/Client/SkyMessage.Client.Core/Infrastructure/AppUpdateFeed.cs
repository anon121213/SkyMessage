using System.Net;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>An open package download. <see cref="StartsAt"/> is where the body begins in the file (0 when the server sent all of it).</summary>
internal sealed record PackageDownload(long StartsAt, Stream Body) : IDisposable
{
    public void Dispose() => Body.Dispose();
}

/// <summary>The server side of self-updates (kept small for tests).</summary>
internal interface IAppUpdateFeed
{
    Task<AppVersionDto> GetVersionAsync(CancellationToken cancellationToken);

    /// <summary>Opens the package at <paramref name="apkUrl"/>, resuming at <paramref name="offset"/> when the server honours the range.</summary>
    Task<PackageDownload> OpenPackageAsync(string apkUrl, long offset, CancellationToken cancellationToken);
}

/// <summary>
/// Version checks and package downloads through the failover-aware transport (proxy, mirrors, stall detection):
/// a download that makes no progress for 15 s aborts and counts towards a failover like media transfers do.
/// </summary>
internal sealed class AppUpdateFeed(ApiTransport transport, IEndpointRouting endpoints, TimeProvider time) : IAppUpdateFeed
{
    public Task<AppVersionDto> GetVersionAsync(CancellationToken cancellationToken) =>
        transport.SendJsonAsync<AppVersionDto>(ApiRequest.Get(ApiRoutes.AppVersion), null, cancellationToken);

    public async Task<PackageDownload> OpenPackageAsync(string apkUrl, long offset, CancellationToken cancellationToken)
    {
        var request = ApiRequest.Get(apkUrl.Trim().TrimStart('/'), stream: true, range: offset > 0 ? $"bytes={offset}-" : null,
            timeout: TransferDefaults.HeadersTimeout);
        var response = await transport.SendAsync(request, null, cancellationToken).ConfigureAwait(false);
        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            // 206 for the requested start resumes; anything else (200, another range) is the file from its beginning.
            var resumed = response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset;
            var watched = new StallDetectingStream(body, response, time, TransferDefaults.StallTimeout, endpoints.ReportStall);
            if (resumed || response.StatusCode == HttpStatusCode.OK) return new PackageDownload(resumed ? offset : 0, watched);
            watched.Dispose();
            throw new IOException($"Unexpected response {(int)response.StatusCode} for the package");
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
