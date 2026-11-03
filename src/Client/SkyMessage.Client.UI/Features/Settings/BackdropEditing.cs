using SkyMessage.Client.Core.Settings;

namespace SkyMessage.Client.UI.Features.Settings;

/// <summary>Whose background <see cref="BackdropEditor"/> edits.</summary>
public enum BackdropTarget
{
    /// <summary>The app's own background (behind the root screens), for both themes or each.</summary>
    App,
    /// <summary>The chats' background (unless a chat has its own).</summary>
    ChatDefault,
    /// <summary>One chat's background (the chat header's menu).</summary>
    Chat,
}

/// <summary>
/// What <see cref="BackdropEditor"/> saved: the app's background (<see cref="App"/>), a chat background
/// (<see cref="Backdrop"/>), or <see cref="Reset"/> — back to the default (the app's background for the chats, the chats'
/// one for a chat).
/// </summary>
public sealed record BackdropEditorResult(AppBackground? App, Backdrop? Backdrop)
{
    public bool Reset { get; init; }
}
