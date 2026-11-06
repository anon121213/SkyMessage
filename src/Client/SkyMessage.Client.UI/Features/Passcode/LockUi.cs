namespace SkyMessage.Client.UI.Features.Passcode;

/// <summary>
/// The lock screen's say in Back (Android) — on a computer Escape goes the same way (js/passcode.js): while its
/// "Forgot the passcode?" question is open, Back closes the question instead of leaving the app. Set by
/// <c>LockScreen</c> while it is rendered.
/// </summary>
public static class LockUi
{
    private static Func<bool>? _back;

    /// <summary>True when the lock screen used Back (it closed its question); false leaves Back to the caller.</summary>
    public static bool TryBack()
    {
        var back = Volatile.Read(ref _back);
        try
        {
            return back?.Invoke() == true;
        }
        catch
        {
            // A lock screen that is going away must not break Back.
            return false;
        }
    }

    internal static void SetBackHandler(Func<bool> handler) => Volatile.Write(ref _back, handler);

    internal static void ClearBackHandler(Func<bool> handler) => Interlocked.CompareExchange(ref _back, null, handler);
}
