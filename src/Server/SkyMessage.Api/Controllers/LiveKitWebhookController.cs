using System.Buffers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkyMessage.Application.Calls;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

/// <summary>
/// LiveKit → API events. Anonymous: the request carries LiveKit's own signature over the raw body, so the body is
/// read as bytes (no model binding; the content type is <c>application/webhook+json</c>).
/// </summary>
[ApiController]
public sealed class LiveKitWebhookController(ICallWebhookService webhooks) : ControllerBase
{
    /// <summary>Webhook events are a few KB; anything far larger is not LiveKit.</summary>
    private const int MaxBodyBytes = 256 * 1024;

    [AllowAnonymous]
    [HttpPost(ApiRoutes.Calls.LiveKitWebhook)]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(Request.Body, cancellationToken);
        if (body is null)
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload too large");

        await webhooks.HandleAsync(body, Request.Headers.Authorization.ToString(), cancellationToken);
        return Ok();
    }

    /// <summary>The raw bytes exactly as signed; null when over <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxBodyBytes) return null;
                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
        return buffer.ToArray();
    }
}
