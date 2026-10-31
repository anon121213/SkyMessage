using SkyMessage.Client.Core.Stores;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Services;

/// <summary>People rendering rules: the user store is the source of truth, embedded copies are fallbacks.</summary>
public static class PeopleExtensions
{
    /// <summary><c>IUserStore.Get(id) ?? embedded</c>.</summary>
    public static UserSummaryDto? Resolve(this IUserStore users, Guid userId, UserSummaryDto? embedded = null) =>
        users.Get(userId) ?? embedded;

    public static UserSummaryDto Resolve(this IUserStore users, UserSummaryDto embedded) =>
        users.Get(embedded.Id) ?? embedded;

    /// <summary>ContactName ?? DisplayName ("Deleted account" for tombstones).</summary>
    public static string NameOf(this UserSummaryDto user, ILocalizer localizer)
    {
        if (user.IsDeleted) return localizer["common.deletedAccount"];
        var name = string.IsNullOrWhiteSpace(user.ContactName) ? user.DisplayName : user.ContactName;
        return string.IsNullOrWhiteSpace(name) ? "@" + user.Username : name.Trim();
    }

    /// <summary>Name of a user id through the store (placeholder while unknown, which also triggers a batched fetch).</summary>
    public static string NameOf(this IUserStore users, Guid userId, ILocalizer localizer, UserSummaryDto? embedded = null)
    {
        var user = users.Get(userId) ?? embedded;
        return user is null ? users.DisplayNameOf(userId) : user.NameOf(localizer);
    }

    public static bool IsOnline(this UserSummaryDto user) => !user.IsDeleted && (user.Presence.IsOnline || user.Presence.Approx == LastSeenApprox.Online);

    /// <summary>"@username" (empty for deleted accounts).</summary>
    public static string Handle(this UserSummaryDto user) => user.IsDeleted || string.IsNullOrEmpty(user.Username) ? "" : "@" + user.Username;
}
