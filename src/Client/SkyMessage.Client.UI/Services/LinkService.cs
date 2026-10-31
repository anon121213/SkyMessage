using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Opening links found in untrusted content (messages, bios, descriptions). Only http, https, mailto and tel
/// are allowed; our own links (skymessage://…, https://our-server/u/…, /join/…, /chat/…) open in-app; when the
/// visible text names a different host than the real target the user is asked first. Scoped.
/// </summary>
public sealed class LinkService(
    NavigationService navigation,
    DialogService dialogs,
    ToastService toasts,
    IExternalLauncher launcher,
    IServerEndpointService endpoints,
    ILocalizer localizer,
    ILogger<LinkService> logger)
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto", "tel"];

    /// <summary>http, https, mailto or tel.</summary>
    public static bool IsAllowed(Uri uri) => uri.IsAbsoluteUri && AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses a link from text ("example.com/x" becomes https://example.com/x).</summary>
    public static bool TryParse(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.Length > Limits.LinkUrlMaxLength) return false;
        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (!IsAllowed(parsed)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>Our deployment's link (skymessage:// or a profile/invite/chat URL on our server's host).</summary>
    public bool IsOwnLink(Uri uri)
    {
        if (string.Equals(uri.Scheme, DeepLinks.AppScheme, StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (!NavigationService.IsAppLink(uri)) return false;
        return OwnHosts().Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Opens a link safely. <paramref name="visibleText"/> is the text the user saw (for the host check).</summary>
    public async Task OpenAsync(string url, string? visibleText = null)
    {
        Uri? uri;
        if (Uri.TryCreate(url, UriKind.Absolute, out var direct) && direct.Scheme.Equals(DeepLinks.AppScheme, StringComparison.OrdinalIgnoreCase))
            uri = direct;
        else if (!TryParse(url, out uri))
        {
            toasts.Warning(localizer["link.unsupported"]);
            return;
        }

        if (IsOwnLink(uri) && navigation.HandleDeepLink(uri)) return;
        if (uri.Scheme.Equals(DeepLinks.AppScheme, StringComparison.OrdinalIgnoreCase))
        {
            toasts.Warning(localizer["link.unsupported"]);
            return;
        }

        if (NeedsConfirmation(uri, visibleText))
        {
            var ok = await dialogs.ConfirmAsync(localizer["link.confirm.title"],
                localizer.Format("link.confirm.text", Display(uri)), localizer["link.confirm.open"]);
            if (!ok) return;
        }

        try
        {
            await launcher.OpenUriAsync(uri);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "External link could not be opened");
            toasts.Error(ex);
        }
    }

    /// <summary>
    /// True when the visible text looks like a link to another host than the target (phishing guard), or when
    /// a text link hides its target entirely (a named link: the user should see where it goes).
    /// </summary>
    public static bool NeedsConfirmation(Uri target, string? visibleText)
    {
        if (target.Scheme is "mailto" or "tel") return false;
        if (string.IsNullOrWhiteSpace(visibleText)) return false;
        var text = visibleText.Trim();
        var candidate = text.Contains("://", StringComparison.Ordinal) ? text
            : text.Contains('.') && !text.Contains(' ') ? "https://" + text
            : null;
        // A named link ("click here") hides its target: show the real address first.
        if (candidate is null || !TryParse(candidate, out var shown)) return true;
        var a = StripWww(shown.Host);
        var b = StripWww(target.Host);
        return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Human-readable form of a URL for confirmations (host + path, IDN-decoded, truncated).</summary>
    public static string Display(Uri uri)
    {
        if (uri.Scheme is "mailto" or "tel") return uri.OriginalString;
        var host = uri.IdnHost;
        try
        {
            host = new System.Globalization.IdnMapping().GetUnicode(uri.IdnHost);
        }
        catch (ArgumentException)
        {
        }
        var text = host + (uri.PathAndQuery == "/" ? "" : uri.PathAndQuery);
        return text.Length > 80 ? text[..79] + "…" : text;
    }

    private IEnumerable<string> OwnHosts()
    {
        yield return endpoints.BaseUri.Host;
        if (endpoints.Info?.PublicBaseUrl is { } publicBase && Uri.TryCreate(publicBase, UriKind.Absolute, out var p)) yield return p.Host;
        foreach (var mirror in endpoints.Info?.MirrorUrls ?? [])
            if (Uri.TryCreate(mirror, UriKind.Absolute, out var m)) yield return m.Host;
    }

    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
