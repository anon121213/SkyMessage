namespace SkyMessage.Client.UI.Features.Auth;

/// <summary>
/// The brand intro of the signed-out screens (the logo pops in, the text follows) plays once per app run: the first
/// screen shown gets it, later ones (welcome ↔ sign in ↔ new account) only fade in. See docs/MOTION.md.
/// </summary>
internal static class AuthIntro
{
    private static int _played;

    /// <summary>True for the first caller in this app run.</summary>
    public static bool TakeFirst() => Interlocked.Exchange(ref _played, 1) == 0;
}
