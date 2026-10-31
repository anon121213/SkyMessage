using System.Net;
using System.Text.Json;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Infrastructure;

/// <summary>Client-only error codes (not sent by the server).</summary>
internal static class CoreErrorCodes
{
    /// <summary>A pending attachment could not be read back (no durable outbox copy survived a restart).</summary>
    public const string AttachmentUnavailable = "attachment_unavailable";
    /// <summary>A transfer made no progress for the stall timeout.</summary>
    public const string Stalled = "transfer_stalled";
    /// <summary>Microphone/camera permission was denied.</summary>
    public const string PermissionDenied = ClientErrorCodes.PermissionDenied;
}

/// <summary>Maps HTTP failures to <see cref="ApiException"/>: problem details (status, title, code, errors) and network errors.</summary>
internal static class HttpErrors
{
    private const int MaxProblemBytes = 64 * 1024;

    public static async Task<ApiException> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string? code = null;
        string? title = null;
        IDictionary<string, string[]>? errors = null;
        try
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                && response.Content.Headers.ContentLength is null or <= MaxProblemBytes)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length is > 0 and <= MaxProblemBytes)
                    (code, title, errors) = Parse(bytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Not problem details (e.g. an HTML page from a proxy): fall back to the status code.
        }

        return new ApiException(status, string.IsNullOrEmpty(code) ? DefaultCode(response.StatusCode) : code, title, errors);
    }

    public static ApiException Network(Exception? inner = null) =>
        new(0, ClientErrorCodes.Network, inner?.Message ?? "Network error");

    public static ApiException Timeout() => new(0, ClientErrorCodes.Timeout, "The request timed out");

    public static ApiException Stalled() => new(0, CoreErrorCodes.Stalled, "The transfer stalled");

    /// <summary>
    /// Converts transport exceptions into ApiException(0, ...). Returns null for exceptions that must
    /// propagate unchanged (caller cancellation, ApiException).
    /// </summary>
    public static ApiException? Translate(Exception ex, CancellationToken callerToken)
    {
        switch (ex)
        {
            case ApiException:
                return null;
            case OperationCanceledException when callerToken.IsCancellationRequested:
                return null;
            case OperationCanceledException:
                return Timeout();
            case HttpRequestException http when http.StatusCode is { } status:
                return new ApiException((int)status, DefaultCode(status), http.Message);
            case HttpRequestException:
            case IOException:
            case System.Net.Sockets.SocketException:
            case System.Net.WebSockets.WebSocketException:
                return Network(ex);
            case ObjectDisposedException:
                // The HttpClient was retired mid-request (proxy change): retryable like a dropped connection.
                return Network(ex);
            case JsonException:
                return new ApiException(0, ClientErrorCodes.Unknown, "Malformed response");
            default:
                return null;
        }
    }

    private static (string? Code, string? Title, IDictionary<string, string[]>? Errors) Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (null, null, null);

        string? code = null, title = null;
        Dictionary<string, string[]>? errors = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("code") && property.Value.ValueKind == JsonValueKind.String)
                code = property.Value.GetString();
            else if (property.NameEquals("title") && property.Value.ValueKind == JsonValueKind.String)
                title = property.Value.GetString();
            else if (property.NameEquals("errors") && property.Value.ValueKind == JsonValueKind.Object)
            {
                errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in property.Value.EnumerateObject())
                {
                    errors[field.Name] = field.Value.ValueKind switch
                    {
                        JsonValueKind.Array => field.Value.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").ToArray(),
                        JsonValueKind.String => [field.Value.GetString() ?? ""],
                        _ => [],
                    };
                }
            }
        }
        return (code, title, errors);
    }

    public static string DefaultCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => ErrorCodes.Validation,
        HttpStatusCode.Unauthorized => ErrorCodes.Unauthorized,
        HttpStatusCode.Forbidden => ErrorCodes.Forbidden,
        HttpStatusCode.NotFound => ErrorCodes.NotFound,
        HttpStatusCode.Conflict => ErrorCodes.Conflict,
        HttpStatusCode.TooManyRequests => ErrorCodes.RateLimited,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ClientErrorCodes.Timeout,
        >= HttpStatusCode.InternalServerError => ErrorCodes.Internal,
        _ => ClientErrorCodes.Unknown,
    };
}
