using System.Collections.Frozen;
using System.Text.RegularExpressions;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>Username format (<see cref="Limits.UsernamePattern"/>) plus names nobody may take.</summary>
public static partial class UsernameRules
{
    // Service-like names that would let someone impersonate the operator or another messenger.
    private static readonly FrozenSet<string> ReservedNames = new[]
    {
        "admin", "administrator", "admins", "root", "superuser", "sysadmin", "system", "support", "helpdesk",
        "help", "info", "official", "staff", "team", "moderator", "moderators", "security", "service", "server",
        "notifications", "notification", "noreply", "no_reply", "postmaster", "webmaster", "hostmaster", "abuse",
        "owner", "bot", "bots", "api", "null", "undefined", "anonymous", "everyone", "username", "user", "users",
        "account", "accounts", "settings", "contacts", "profile", "login", "logout", "register", "signup",
        "signin", "saved", "saved_messages", "favorites", "unknown", "skymsg", "viber", "signal",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Prefixes: our brand, tombstones ("deleted_{id}") and other messengers' brands.
    private static readonly string[] ReservedPrefixes = ["skymessage", "sky_message", "deleted", "telegram", "whatsapp"];

    [GeneratedRegex(Limits.UsernamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Strips surrounding whitespace and leading '@' (the way people type usernames).</summary>
    public static string Clean(string? username) => (username ?? "").Trim().TrimStart('@').Trim();

    public static bool IsWellFormed(string cleaned) =>
        cleaned.Length is >= Limits.UsernameMinLength and <= Limits.UsernameMaxLength && Pattern().IsMatch(cleaned);

    /// <param name="normalized">Lower-case form (<see cref="User.Normalize"/>).</param>
    public static bool IsReserved(string normalized) =>
        ReservedNames.Contains(normalized) || ReservedPrefixes.Any(p => normalized.StartsWith(p, StringComparison.Ordinal));

    /// <summary>Null when <paramref name="cleaned"/> may be registered, otherwise <see cref="ErrorCodes.UsernameInvalid"/>.</summary>
    public static string? Validate(string cleaned) =>
        IsWellFormed(cleaned) && !IsReserved(User.Normalize(cleaned)) ? null : ErrorCodes.UsernameInvalid;
}
