namespace SkyMessage.Client.UI.Features.Auth;

/// <summary>
/// The brand intro of the signed-out screens (the logo settles in and its horizon opens, the text follows) plays once
/// per app run: the first screen shown gets it, later ones (welcome ↔ sign in ↔ new account) only rise in and show the
/// logo finished. See docs/MOTION.md, "The logo".
/// </summary>
internal static class AuthIntro
{
    private static int _played;

    /// <summary>True for the first caller in this app run.</summary>
    public static bool TakeFirst() => Interlocked.Exchange(ref _played, 1) == 0;
}
