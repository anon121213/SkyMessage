using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Rules;

/// <summary>A folder's name, icon and rules as they are stored (<see cref="ChatFolderInput.Apply"/>).</summary>
internal sealed record ChatFolderDraft
{
    public string Name { get; init; } = "";
    public string? Icon { get; init; }
    public bool IncludeContacts { get; init; }
    public bool IncludeNonContacts { get; init; }
    public bool IncludeGroups { get; init; }
    public bool IncludeChannels { get; init; }
    public bool ExcludeMuted { get; init; }
    public bool ExcludeRead { get; init; }
    public bool ExcludeArchived { get; init; }
    public IReadOnlyList<Guid> IncludedChatIds { get; init; } = [];
    public IReadOnlyList<Guid> ExcludedChatIds { get; init; } = [];

    public static ChatFolderDraft Of(ChatFolder folder) => new()
    {
        Name = folder.Name,
        Icon = folder.Icon,
        IncludeContacts = folder.IncludeContacts,
        IncludeNonContacts = folder.IncludeNonContacts,
        IncludeGroups = folder.IncludeGroups,
        IncludeChannels = folder.IncludeChannels,
        ExcludeMuted = folder.ExcludeMuted,
        ExcludeRead = folder.ExcludeRead,
        ExcludeArchived = folder.ExcludeArchived,
        IncludedChatIds = folder.IncludedChatIds,
        ExcludedChatIds = folder.ExcludedChatIds,
    };
}

/// <summary>
/// What a saved folder must look like. <see cref="Errors"/> checks the fields a request sets (also run by the request
/// validator, so HTTP callers get every error at once); <see cref="Apply"/> lays them over the stored folder — null keeps
/// a field — and checks what only the whole folder shows.
/// </summary>
internal static class ChatFolderInput
{
    public static IEnumerable<(string Field, string Error)> Errors(SaveChatFolderRequest request)
    {
        if (request.Name is not null || request.Create)
        {
            if (!ChatFolderRules.IsValidName(ChatFolderRules.NormalizeName(request.Name)))
                yield return ("name", $"Name must be 1 to {Limits.ChatFolderNameMaxLength} characters, with something visible and no control characters.");
        }
        if (!string.IsNullOrEmpty(request.Icon) && !ChatFolderRules.IsValidIcon(request.Icon))
            yield return ("icon", "Icon must be one emoji or an icon key.");

        foreach (var (field, ids) in new[] { ("includedChatIds", request.IncludedChatIds), ("excludedChatIds", request.ExcludedChatIds) })
        {
            if (ids is null) continue;
            if (ids.Count > Limits.MaxChatFolderChats)
                yield return (field, $"At most {Limits.MaxChatFolderChats} chats.");
            else if (ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count)
                yield return (field, "Chat ids must be distinct and not empty.");
        }
        if (request.IncludedChatIds is { } included && request.ExcludedChatIds is { } excluded && included.Intersect(excluded).Any())
            yield return ("excludedChatIds", "A chat cannot be both included and excluded.");
        // A new folder is the request alone; an edit is checked once it is laid over the stored folder (Apply).
        if (request.Create && !TakesInAnything(request))
            yield return ("includedChatIds", "Include at least one type of chat or one chat.");
    }

    /// <summary>A validation error listing every problem of the fields the request sets (<see cref="Errors"/>).</summary>
    public static void Validate(SaveChatFolderRequest request)
    {
        var errors = Errors(request).ToList();
        if (errors.Count > 0) throw Failure(errors);
    }

    /// <summary>
    /// The folder the request leaves: <paramref name="current"/> (null = a new folder) with the fields the request sets,
    /// or a validation error listing every problem.
    /// </summary>
    public static ChatFolderDraft Apply(SaveChatFolderRequest request, ChatFolderDraft? current)
    {
        var errors = Errors(request).ToList();
        var basis = current ?? new ChatFolderDraft();
        var draft = basis with
        {
            Name = request.Name is null ? basis.Name : ChatFolderRules.NormalizeName(request.Name),
            Icon = request.Icon is null ? basis.Icon : request.Icon.Length == 0 ? null : request.Icon,
            IncludeContacts = request.IncludeContacts ?? basis.IncludeContacts,
            IncludeNonContacts = request.IncludeNonContacts ?? basis.IncludeNonContacts,
            IncludeGroups = request.IncludeGroups ?? basis.IncludeGroups,
            IncludeChannels = request.IncludeChannels ?? basis.IncludeChannels,
            ExcludeMuted = request.ExcludeMuted ?? basis.ExcludeMuted,
            ExcludeRead = request.ExcludeRead ?? basis.ExcludeRead,
            ExcludeArchived = request.ExcludeArchived ?? basis.ExcludeArchived,
            IncludedChatIds = request.IncludedChatIds ?? basis.IncludedChatIds,
            ExcludedChatIds = request.ExcludedChatIds ?? basis.ExcludedChatIds,
        };
        if (errors.Count == 0)
        {
            // An edit of only one list may meet the other one as stored.
            if (draft.IncludedChatIds.Intersect(draft.ExcludedChatIds).Any())
                errors.Add(("excludedChatIds", "A chat cannot be both included and excluded."));
            if (!ChatFolderRules.TakesInAnything(draft.IncludeContacts, draft.IncludeNonContacts, draft.IncludeGroups, draft.IncludeChannels,
                    draft.IncludedChatIds.Count))
                errors.Add(("includedChatIds", "Include at least one type of chat or one chat."));
        }
        if (errors.Count > 0) throw Failure(errors);
        return draft;
    }

    private static BadRequestException Failure(IEnumerable<(string Field, string Error)> errors) =>
        new(ErrorCodes.Validation, "Validation failed",
            errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Error).Distinct().ToArray()));

    private static bool TakesInAnything(SaveChatFolderRequest request) =>
        ChatFolderRules.TakesInAnything(request.IncludeContacts == true, request.IncludeNonContacts == true, request.IncludeGroups == true,
            request.IncludeChannels == true, request.IncludedChatIds?.Count ?? 0);
}
