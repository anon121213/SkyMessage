using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>Shareable https links: <see cref="ServerInfoDto.PublicBaseUrl"/> ?? the address in use, + <see cref="DeepLinks"/> paths.</summary>
internal sealed class LinkBuilder(IServerEndpointService endpoints) : ILinkBuilder
{
    public string ProfileLink(string username) =>
        Build(DeepLinks.UserProfilePath(Uri.EscapeDataString(username.Trim().TrimStart('@'))));

    public string InviteLink(string token) => Build(DeepLinks.JoinChatPath(Uri.EscapeDataString(token.Trim())));

    private string Build(string path)
    {
        var baseUri = endpoints.BaseUri;
        if (endpoints.Info?.PublicBaseUrl is { Length: > 0 } configured
            && Uri.TryCreate(configured.EndsWith('/') ? configured : configured + "/", UriKind.Absolute, out var publicBase)
            && publicBase.Scheme is "https" or "http")
        {
            baseUri = publicBase;
        }
        return new Uri(baseUri, path.TrimStart('/')).ToString();
    }
}
