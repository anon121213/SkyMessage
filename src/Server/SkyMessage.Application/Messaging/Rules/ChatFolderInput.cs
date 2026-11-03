using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Messaging.Rules;

/// <summary>A folder as saved: trimmed name, checked icon and chat lists (<see cref="ChatFolderInput.Normalize"/>).</summary>
internal sealed record ChatFolderDraft(string Name, string? Icon, IReadOnlyList<Guid> IncludedChatIds, IReadOnlyList<Guid> ExcludedChatIds);

/// <summary>What a saved folder must look like (also run by the request validator, so HTTP callers get every error at once).</summary>
internal static class ChatFolderInput
{
    public static IEnumerable<(string Field, string Error)> Errors(SaveChatFolderRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > Limits.ChatFolderNameMaxLength || MessageContentRules.HasForbiddenChars(name))
            yield return ("name", $"Name must be 1 to {Limits.ChatFolderNameMaxLength} characters.");
        if (!ChatFolderRules.IsValidIcon(request.Icon))
            yield return ("icon", "Icon must be one emoji or an icon key.");

        var included = request.IncludedChatIds ?? [];
        var excluded = request.ExcludedChatIds ?? [];
        foreach (var (field, ids) in new[] { ("includedChatIds", included), ("excludedChatIds", excluded) })
        {
            if (ids.Count > Limits.MaxChatFolderChats)
                yield return (field, $"At most {Limits.MaxChatFolderChats} chats.");
            else if (ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count)
                yield return (field, "Chat ids must be distinct and not empty.");
        }
        if (included.Intersect(excluded).Any())
            yield return ("excludedChatIds", "A chat cannot be both included and excluded.");
        if (!ChatFolderRules.TakesInAnything(request.IncludeContacts, request.IncludeNonContacts, request.IncludeGroups,
                request.IncludeChannels, included.Count))
            yield return ("includedChatIds", "Include at least one type of chat or one chat.");
    }

    /// <summary>The folder to store, or a validation error listing every problem.</summary>
    public static ChatFolderDraft Normalize(SaveChatFolderRequest request)
    {
        var errors = Errors(request).GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Error).ToArray());
        if (errors.Count > 0) throw new BadRequestException(ErrorCodes.Validation, "Validation failed", errors);
        return new ChatFolderDraft(
            request.Name.Trim(),
            string.IsNullOrEmpty(request.Icon) ? null : request.Icon,
            request.IncludedChatIds ?? [],
            request.ExcludedChatIds ?? []);
    }
}
