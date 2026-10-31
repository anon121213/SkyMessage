using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>What the transport needs from the endpoint service (kept small for tests).</summary>
internal interface IEndpointRouting
{
    Uri BaseUri { get; }

    /// <summary>
    /// A request to <paramref name="failedBase"/> hit a network error: fail over unless someone already did.
    /// True when <see cref="BaseUri"/> now points elsewhere.
    /// </summary>
    Task<bool> FailoverFromAsync(Uri failedBase, CancellationToken cancellationToken);

    /// <summary>Completes once tokens may be sent to <see cref="BaseUri"/> (identity verified).</summary>
    Task EnsureTrustedAsync(CancellationToken cancellationToken);

    /// <summary>A transfer stalled (no bytes for the stall timeout); repeated stalls trigger failover.</summary>
    void ReportStall();

    /// <summary>Bumped whenever the base address or the HTTP stack changes (realtime reconnects on a new value).</summary>
    int ConnectionVersion { get; }
}

/// <summary>Bearer tokens for the authorized pipeline (implemented by the auth service).</summary>
internal interface ITokenSource
{
    /// <summary>A valid access token (refreshed when it expires within 60 s); null when signed out.</summary>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The server rejected <paramref name="rejectedToken"/> with 401: refresh once (single-flight) and return
    /// the new token, or null when the session is gone.
    /// </summary>
    Task<string?> HandleUnauthorizedAsync(string rejectedToken, string code, CancellationToken cancellationToken);
}

internal sealed class ApiRequest
{
    public required HttpMethod Method { get; init; }
    /// <summary>Relative to the server base URI (no leading slash).</summary>
    public required string Path { get; init; }
    /// <summary>Content factory: a retry needs a fresh HttpContent.</summary>
    public Func<HttpContent>? Content { get; init; }
    /// <summary>Safe to repeat after a network failure (GET/PUT/DELETE, idempotency keys, read-only POSTs).</summary>
    public bool Idempotent { get; init; }
    public TimeSpan Timeout { get; init; } = ApiTransport.DefaultTimeout;
    /// <summary>Return as soon as headers arrive; the caller reads (and disposes) the body.</summary>
    public bool StreamResponse { get; init; }
    public string? Range { get; init; }
    public bool AllowFailover { get; init; } = true;
    /// <summary>The body carries a credential (a refresh token): like a bearer, it only goes to a verified address.</summary>
    public bool CarriesCredentials { get; init; }

    public static ApiRequest Get(string path, bool stream = false, string? range = null, TimeSpan? timeout = null) => new()
    {
        Method = HttpMethod.Get,
        Path = path,
        Idempotent = true,
        StreamResponse = stream,
        Range = range,
        Timeout = timeout ?? ApiTransport.DefaultTimeout,
    };

    public static ApiRequest Json<TBody>(HttpMethod method, string path, TBody body, bool idempotent, TimeSpan? timeout = null,
        bool carriesCredentials = false) => new()
    {
        Method = method,
        Path = path,
        Content = () => JsonContent.Create(body, options: SkyJson.Options),
        Idempotent = idempotent,
        Timeout = timeout ?? ApiTransport.DefaultTimeout,
        CarriesCredentials = carriesCredentials,
    };

    public static ApiRequest Empty(HttpMethod method, string path, bool idempotent) => new()
    {
        Method = method,
        Path = path,
        Idempotent = idempotent,
    };
}

/// <summary>
/// Sends requests to the active server: absolute URIs from <see cref="IEndpointRouting.BaseUri"/>, per-request
/// timeouts, problem details → <see cref="ApiException"/>, network errors → ApiException(0). On a network
/// error it fails over once; idempotent requests are then retried (GETs also when there was nowhere to go).
/// </summary>
internal sealed class ApiTransport(HttpClientProvider clients, IEndpointRouting endpoints, TimeProvider time)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SameServerRetryDelay = TimeSpan.FromSeconds(1);

    public IEndpointRouting Endpoints => endpoints;

    public async Task<HttpResponseMessage> SendAsync(ApiRequest request, string? bearer, CancellationToken cancellationToken)
    {
        var baseUri = endpoints.BaseUri;
        try
        {
            return await SendOnceAsync(baseUri, request, bearer, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.IsNetworkError && request.AllowFailover && !cancellationToken.IsCancellationRequested)
        {
            var switched = await endpoints.FailoverFromAsync(baseUri, cancellationToken).ConfigureAwait(false);
            if (!request.Idempotent) throw;
            if (!switched)
            {
                if (request.Method != HttpMethod.Get) throw;
                await Task.Delay(SameServerRetryDelay, time, cancellationToken).ConfigureAwait(false);
            }
            // The address may have changed meanwhile without a failover (a new server entered in the settings): a token
            // follows only once that address is verified as the same deployment, as before the first attempt.
            if (bearer is not null || request.CarriesCredentials)
                await endpoints.EnsureTrustedAsync(cancellationToken).ConfigureAwait(false);
            return await SendOnceAsync(endpoints.BaseUri, request, bearer, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<T> SendJsonAsync<T>(ApiRequest request, string? bearer, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, bearer, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return default!;
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(SkyJson.Options, cancellationToken).ConfigureAwait(false);
            return value!;
        }
        catch (JsonException)
        {
            throw new ApiException((int)response.StatusCode, ClientErrorCodes.Unknown, "Malformed response");
        }
        catch (Exception ex) when (HttpErrors.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Uri baseUri, ApiRequest request, string? bearer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout != Timeout.InfiniteTimeSpan)
            timeout.CancelAfter(request.Timeout);

        using var message = new HttpRequestMessage(request.Method, new Uri(baseUri, request.Path));
        if (request.Content is not null)
            message.Content = request.Content();
        if (bearer is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (request.Range is not null)
            message.Headers.TryAddWithoutValidation("Range", request.Range);
        if (!request.StreamResponse)
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await clients.Client.SendAsync(
                    message,
                    request.StreamResponse ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (HttpErrors.Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }

        if (response.IsSuccessStatusCode) return response;
        try
        {
            throw await HttpErrors.FromResponseAsync(response, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }
}

/// <summary>
/// The authorized pipeline: bearer token from <see cref="ITokenSource"/>, identity check of the server
/// before any token leaves the device, and on 401 exactly one refresh + retry.
/// </summary>
internal sealed class AuthorizedSender(ApiTransport transport, ITokenSource tokens)
{
    public ApiTransport Transport => transport;

    /// <param name="optionalAuth">Anonymous endpoints that behave better with a token (sent only when signed in).</param>
    public async Task<HttpResponseMessage> SendAsync(ApiRequest request, CancellationToken cancellationToken, bool optionalAuth = false)
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            if (optionalAuth) return await transport.SendAsync(request, null, cancellationToken).ConfigureAwait(false);
            throw new ApiException(401, ErrorCodes.Unauthorized, "Not signed in");
        }

        await transport.Endpoints.EnsureTrustedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await transport.SendAsync(request, token, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            var fresh = await tokens.HandleUnauthorizedAsync(token, ex.Code, cancellationToken).ConfigureAwait(false);
            if (fresh is null || fresh == token) throw;
            await transport.Endpoints.EnsureTrustedAsync(cancellationToken).ConfigureAwait(false);
            return await transport.SendAsync(request, fresh, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<T> SendJsonAsync<T>(ApiRequest request, CancellationToken cancellationToken, bool optionalAuth = false)
    {
        using var response = await SendAsync(request, cancellationToken, optionalAuth).ConfigureAwait(false);
        return await ApiTransport.ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendNoContentAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
