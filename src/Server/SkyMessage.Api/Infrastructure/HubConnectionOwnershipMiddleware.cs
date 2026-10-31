using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SkyMessage.Application.Common.Security;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Infrastructure;

/// <summary>
/// SignalR only authenticates each transport request, it does not check that the caller is the user who
/// negotiated the connection. Without this, any signed-in user who learns a connection token (it appears in
/// long-polling/SSE/WebSocket URLs and proxy logs) could read or close someone else's realtime stream.
/// Records the owner of every token at negotiate time and rejects transport requests from anyone else.
/// </summary>
internal sealed class HubConnectionOwnershipMiddleware(RequestDelegate next, IMemoryCache cache)
{
    private static readonly MemoryCacheEntryOptions EntryOptions = new() { SlidingExpiration = TimeSpan.FromDays(1) };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(HubRoutes.Realtime, out var rest))
        {
            await next(context);
            return;
        }

        var user = context.User.FindFirst(SkyClaims.UserId)?.Value;
        if (rest.Equals("/negotiate", StringComparison.OrdinalIgnoreCase))
        {
            await NegotiateAsync(context, user);
            return;
        }

        var id = context.Request.Query["id"].ToString();
        if (id.Length > 0 && cache.TryGetValue(Key(id), out string? owner) && !string.Equals(owner, user, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private async Task NegotiateAsync(HttpContext context, string? user)
    {
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        if (context.Response.StatusCode == StatusCodes.Status200OK && user is not null)
            Remember(buffer, user);

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted);
    }

    private void Remember(MemoryStream body, string user)
    {
        try
        {
            body.Position = 0;
            using var json = JsonDocument.Parse(body);
            // negotiateVersion 1 transports use connectionToken as ?id=; version 0 used connectionId.
            foreach (var name in new[] { "connectionToken", "connectionId" })
            {
                if (json.RootElement.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } id)
                    cache.Set(Key(id), user, EntryOptions);
            }
        }
        catch (JsonException)
        {
            // Not a negotiate payload we understand: SignalR itself will reject unknown ids.
        }
    }

    private static string Key(string id) => $"hub-owner:{id}";
}
