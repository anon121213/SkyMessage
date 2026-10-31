namespace SkyMessage.Client.UI.Kit;

public enum ButtonVariant
{
    Primary,
    Secondary,
    Tonal,
    Ghost,
    Danger,
    DangerGhost,
    Link,
}

public enum ButtonSize
{
    Small,
    Medium,
    Large,
}

public enum IconButtonVariant
{
    Plain,
    Tonal,
    Filled,
    Danger,
    /// <summary>Translucent dark circle for use over photos/video.</summary>
    OnMedia,
    /// <summary>Frosted-glass circle for headers and controls floating over the sky.</summary>
    Glass,
}

public enum BadgeKind
{
    Count,
    /// <summary>"@" for unread mentions.</summary>
    Mention,
    Dot,
    /// <summary>Small text label ("Admin", "New").</summary>
    Label,
    Outline,
}

public enum SkeletonShape
{
    Line,
    Circle,
    Rect,
}

/// <summary>Which sky a <see cref="SkyBackdrop"/> paints: the app theme's or the chat wallpaper.</summary>
public enum SkyVariant
{
    App,
    Chat,
}

public enum TabsVariant
{
    Underline,
    Pill,
}

/// <summary>Viewport point where a context menu was requested (CSS px).</summary>
public readonly record struct MenuPosition(double X, double Y);
