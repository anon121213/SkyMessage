using SkyMessage.Client.UI.Services;

namespace SkyMessage.Client.UI.Shell;

/// <summary>Where a component is rendered by the shell.</summary>
public enum ShellPane
{
    /// <summary>Phone: the top-level screen under the page stack (chat list, contacts...).</summary>
    Base,
    /// <summary>Phone: a full-screen page above the base.</summary>
    Page,
    /// <summary>The chat host (phone layer or wide main pane), kept mounted under profile/info pages.</summary>
    Chat,
    /// <summary>Wide: the routed content column.</summary>
    Main,
    /// <summary>Wide: the resizable list column.</summary>
    Sidebar,
    /// <summary>Wide (≥ 1280 px): the third column for chat info / shared media.</summary>
    Info,
    /// <summary>Tablet: chat info / shared media floating over the chat.</summary>
    InfoOverlay,
}

/// <summary>
/// Cascaded by MainLayout to everything it renders: <c>[CascadingParameter] public ShellContext? Shell { get; set; }</c>.
/// A chat that <see cref="IsCovered"/> is hidden under another page: pause read tracking, clear
/// IChatListStore.ActiveChatId and stop autoplay until it is uncovered.
/// </summary>
public sealed record ShellContext(LayoutMode Mode, ShellPane Pane, bool IsCovered)
{
    public bool IsPhone => Mode == LayoutMode.Phone;
    public bool IsWide => Mode != LayoutMode.Phone;
    /// <summary>Rendered in a side column / overlay (show a close button instead of back).</summary>
    public bool IsSidePane => Pane is ShellPane.Info or ShellPane.InfoOverlay;
}

/// <summary>Page background: grouped (light grey behind cards, settings-like) or plain surface (lists).</summary>
public enum PageBackground
{
    Grouped,
    Surface,
}

/// <summary>Illustrations drawn by <see cref="SkyArt"/> (our own artwork in the sky palette).</summary>
public enum SkyArtKind
{
    /// <summary>Two message capsules floating over a cloud in the morning sun (no chats yet).</summary>
    Chats,
    /// <summary>Two squircle faces with a heart between them (no contacts yet).</summary>
    Contacts,
    /// <summary>A cloud with a band-aid, a little rain and the sun behind it (something went wrong).</summary>
    Crash,
    /// <summary>A crescent moon, a cloud and a floating question mark among stars (unknown address).</summary>
    NotFound,
    /// <summary>A phone with contact rows and a "found" check (phone-book import).</summary>
    PhoneBook,
}
