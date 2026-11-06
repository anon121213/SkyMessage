namespace SkyMessage.Domain.Entities;

/// <summary>
/// A photo in a profile's (<see cref="UserId"/>) or a group's (<see cref="ChatId"/>) avatar history; exactly one of
/// them is set. The current avatar stays <c>User.AvatarMediaId</c> / <c>Chat.AvatarMediaId</c>; setting a photo
/// (again) moves its entry to the front (<see cref="SetAt"/>). Removing the current avatar removes its entry too.
/// </summary>
public sealed class AvatarPhoto
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? UserId { get; set; }
    public Guid? ChatId { get; set; }
    public Guid MediaId { get; set; }
    /// <summary>Who made it the avatar last (a group's admin; the user for a profile).</summary>
    public Guid SetById { get; set; }
    public DateTimeOffset SetAt { get; set; }
}
