using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Contracts;

namespace SkyMessage.App.Services;

/// <summary>
/// Links that started or resumed the app: Android intents (launch, OnNewIntent, notification taps),
/// iOS/Mac URL opens and notification responses. A link that arrives before the UI subscribed is kept
/// and handed out once by <see cref="ConsumePending"/>.
/// </summary>
public sealed class DeepLinkSource(ILogger<DeepLinkSource> logger) : IDeepLinkSource
{
    private readonly object _gate = new();
    private Action<Uri>? _handlers;
    private Uri? _pending;

    public event Action<Uri>? DeepLinkReceived
    {
        add { lock (_gate) _handlers += value; }
        remove { lock (_gate) _handlers -= value; }
    }

    public Uri? ConsumePending()
    {
        lock (_gate)
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }
    }

    /// <summary>Called by the host for every incoming link; ignores anything that is not ours.</summary>
    public void Publish(Uri? uri)
    {
        if (uri is null || !IsAcceptable(uri)) return;

        Action<Uri>? handlers;
        lock (_gate)
        {
            handlers = _handlers;
            if (handlers is null)
            {
                _pending = uri;
                return;
            }
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<Uri>)handler)(uri);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Deep link handler failed");
            }
        }
    }

    public void Publish(string? uri)
    {
        if (!string.IsNullOrWhiteSpace(uri) && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) Publish(parsed);
    }

    private static bool IsAcceptable(Uri uri) =>
        uri.IsAbsoluteUri
        && (string.Equals(uri.Scheme, DeepLinks.AppScheme, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme is "http" or "https")
        && DeepLinks.TryParse(uri, out _);
}
