namespace SkyMessage.Application.Common.Security;

/// <summary>JWT claim names (inbound claim mapping is disabled, so these arrive verbatim).</summary>
public static class SkyClaims
{
    public const string UserId = "sub";
    public const string SessionId = "sid";
    public const string Username = "name";
    /// <summary>Which refresh token the access token was issued with (see <c>RefreshTokens.Binding</c>).</summary>
    public const string RefreshBinding = "rtb";
}
