using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;

namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Tap feedback through <see cref="IHapticsService"/>, only when the device supports it and the user keeps
/// "Vibrate in app" on. Singleton, no JS.
/// </summary>
public sealed class HapticFeedback(IHapticsService haptics, ISettingsService settings)
{
    private bool Enabled => haptics.IsSupported && settings.Current.Notifications.InAppVibrate;

    /// <summary>Toggles, selections, tab switches.</summary>
    public void Selection() => Perform(HapticKind.Selection);

    /// <summary>Ordinary button tap.</summary>
    public void Tap() => Perform(HapticKind.Light);

    /// <summary>Long-press menus, drag starts.</summary>
    public void LongPress() => Perform(HapticKind.Medium);

    public void Success() => Perform(HapticKind.Success);

    public void Warning() => Perform(HapticKind.Warning);

    public void Perform(HapticKind kind)
    {
        if (!Enabled) return;
        try
        {
            haptics.Perform(kind);
        }
        catch
        {
            // Feedback is cosmetic; never let it break an interaction.
        }
    }
}
