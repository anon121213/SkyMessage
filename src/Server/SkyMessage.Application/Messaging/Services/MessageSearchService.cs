using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

internal sealed class MessageSearchService(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IMessagingDbFunctions dbFunctions,
    IUserSummaryService userSummaries,
    ChatSummaryBuilder summaries,
    MessageDtoLoader loader,
    MessagePageBuilder pages) : IMessageSearchService
{
    /// <summary>Per-section size of the combined search (search-as-you-type).</summary>
    private const int GlobalSectionLimit = 20;

    public async Task<MessagesPageDto> SearchInChatAsync(Guid chatId, string? query, long? beforeSeq, int? limit, CancellationToken cancellationToken = default)
    {
        var q = RequireQuery(query);
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        var take = PageWindow.ClampLimit(limit, Limits.SearchResultsMax, Limits.SearchResultsMax);
        var readAt = clock.UtcNow;

        var hits = db.VisibleMessages(member).AsNoTracking()
            .Where(m => m.DeletedAt == null)
            .Where(dbFunctions.MessageTextMatches(LikePattern.Contains(q)));
        if (beforeSeq is { } before) hits = hits.Where(m => m.Seq < before);

        var ids = await hits.OrderByDescending(m => m.Seq).Select(m => m.Id).Take(take + 1).ToListAsync(cancellationToken);
        return await pages.BuildAsync(me, ids.Take(take).ToList(), hasMoreBefore: ids.Count > take, hasMoreAfter: false, readAt, cancellationToken);
    }

    public async Task<MessagesPageDto> GetSharedMediaAsync(Guid chatId, SharedMediaFilter filter, long? beforeSeq, int? limit, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(filter)) throw MessagingErrors.Validation("kind", "Unknown shared media filter.");
        var me = currentUser.UserId;
        var member = await db.RequireMemberAsync(chatId, me, cancellationToken);
        var take = PageWindow.ClampLimit(limit, Limits.MessagesPageDefault, Limits.MessagesPageMax);
        var readAt = clock.UtcNow;

        var items = db.VisibleMessages(member).AsNoTracking().Where(m => m.DeletedAt == null);
        items = filter switch
        {
            SharedMediaFilter.Media => items.Where(m => m.Type == MessageType.Photo || m.Type == MessageType.Video || m.Type == MessageType.Album),
            SharedMediaFilter.Files => items.Where(m => m.Type == MessageType.File || m.Type == MessageType.Audio),
            SharedMediaFilter.Voice => items.Where(m => m.Type == MessageType.Voice),
            SharedMediaFilter.VideoNotes => items.Where(m => m.Type == MessageType.VideoNote),
            SharedMediaFilter.Links => items.Where(m => m.Text != null)
                .Where(m => m.Entities.Any(e => e.Type == MessageEntityType.Url || e.Type == MessageEntityType.TextLink)),
            _ => items,
        };
        if (beforeSeq is { } before) items = items.Where(m => m.Seq < before);

        var ids = await items.OrderByDescending(m => m.Seq).Select(m => m.Id).Take(take + 1).ToListAsync(cancellationToken);
        return await pages.BuildAsync(me, ids.Take(take).ToList(), hasMoreBefore: ids.Count > take, hasMoreAfter: false, readAt, cancellationToken);
    }

    public async Task<IReadOnlyList<MessageSearchHitDto>> SearchMessagesAsync(string? query, int? limit, CancellationToken cancellationToken = default)
    {
        var q = RequireQuery(query);
        var take = PageWindow.ClampLimit(limit, Limits.SearchResultsMax, Limits.SearchResultsMax);
        return await FindMessagesAsync(currentUser.UserId, q, take, cancellationToken);
    }

    public async Task<GlobalSearchResultDto> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        var q = RequireQuery(query);
        var me = currentUser.UserId;

        var people = SearchQuery.NormalizeForUsers(q);
        var matchedUsers = people is null ? [] : await FindUsersAsync(me, people, GlobalSectionLimit, cancellationToken);
        var chats = await FindChatsAsync(me, q, people, cancellationToken);
        var messages = await FindMessagesAsync(me, q, GlobalSectionLimit, cancellationToken);

        return new GlobalSearchResultDto
        {
            Chats = chats,
            Users = matchedUsers,
            Messages = messages,
        };
    }

    private static string RequireQuery(string? query) =>
        SearchQuery.Normalize(query)
        ?? throw MessagingErrors.Validation("q", $"The search query must be at least {Limits.SearchQueryMinLength} characters.");

    /// <summary>Text hits in every chat of the viewer, newest first.</summary>
    private async Task<List<MessageSearchHitDto>> FindMessagesAsync(Guid me, string q, int take, CancellationToken ct)
    {
        var rows = await db.Messages.AsNoTracking()
            .Where(dbFunctions.MessageTextMatches(LikePattern.Contains(q)))
            .Where(m => m.DeletedAt == null
                        && db.ChatMembers.Any(cm => cm.ChatId == m.ChatId && cm.UserId == me && m.Seq > cm.ClearedUpToSeq)
                        && !db.HiddenMessages.Any(h => h.MessageId == m.Id && h.UserId == me))
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Select(m => new { m.Id, m.ChatId })
            .Take(take)
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var messages = (await loader.LoadAsync(rows.Select(r => r.Id).ToList(), ct)).ToDictionary(m => m.Id);
        var chatIds = rows.Select(r => r.ChatId).Distinct().ToList();
        var chats = await db.Chats.AsNoTracking()
            .Where(c => chatIds.Contains(c.Id))
            .Select(c => new
            {
                c.Id,
                c.Type,
                c.Title,
                c.AvatarMediaId,
                PeerId = c.Type == ChatType.Private
                    ? db.ChatMembers.Where(o => o.ChatId == c.Id && o.UserId != me).OrderBy(o => o.JoinedAt).Select(o => (Guid?)o.UserId).FirstOrDefault()
                    : null,
            })
            .ToDictionaryAsync(c => c.Id, ct);
        var peerIds = chats.Values.Where(c => c.PeerId is not null).Select(c => c.PeerId!.Value).Distinct().ToList();
        var peers = peerIds.Count == 0
            ? new Dictionary<Guid, UserSummaryDto>()
            : await userSummaries.GetAsync(me, peerIds, ct);

        var hits = new List<MessageSearchHitDto>(rows.Count);
        foreach (var row in rows)
        {
            if (!messages.TryGetValue(row.Id, out var message) || !chats.TryGetValue(row.ChatId, out var chat)) continue;
            var peer = chat.PeerId is { } peerId ? peers.GetValueOrDefault(peerId) : null;
            hits.Add(new MessageSearchHitDto
            {
                ChatId = chat.Id,
                ChatType = chat.Type,
                ChatTitle = chat.Type switch
                {
                    ChatType.Group => chat.Title ?? "",
                    ChatType.Private => peer is null ? "" : peer.ContactName ?? peer.DisplayName,
                    _ => ChatSummaryBuilder.SavedMessagesTitle,
                },
                ChatAvatarMediaId = chat.Type switch
                {
                    ChatType.Group => chat.AvatarMediaId,
                    ChatType.Private => peer?.AvatarMediaId,
                    _ => null,
                },
                Message = message,
            });
        }
        return hits;
    }

    /// <summary>My visible groups whose title matches and private chats whose peer matches (name, contact name, username prefix).</summary>
    private async Task<List<ChatSummaryDto>> FindChatsAsync(Guid me, string q, string? people, CancellationToken ct)
    {
        var pattern = LikePattern.Contains(q);
        var groupIds = await db.Chats.AsNoTracking()
            .Where(dbFunctions.ChatTitleMatches(pattern))
            .Where(c => c.Type == ChatType.Group && !c.IsDeleted
                        && db.ChatMembers.Any(cm => cm.ChatId == c.Id && cm.UserId == me && !cm.IsHidden))
            .OrderByDescending(c => c.LastActivityAt)
            .Select(c => c.Id)
            .Take(GlobalSectionLimit)
            .ToListAsync(ct);

        var privateIds = new List<Guid>();
        if (people is not null)
        {
            var peoplePattern = LikePattern.Contains(people);
            var usernamePrefix = people.ToLowerInvariant();
            var nameMatches = db.Users.Where(dbFunctions.UserDisplayNameMatches(peoplePattern)).Select(u => u.Id);
            var contactNameMatches = db.Contacts
                .Where(c => c.OwnerId == me)
                .Where(dbFunctions.ContactNameMatches(peoplePattern))
                .Select(c => c.ContactUserId);

            privateIds = await db.ChatMembers.AsNoTracking()
                .Where(cm => cm.UserId == me && !cm.IsHidden && cm.Chat!.Type == ChatType.Private && !cm.Chat.IsDeleted
                             && db.ChatMembers.Any(o => o.ChatId == cm.ChatId && o.UserId != me
                                                        && (nameMatches.Contains(o.UserId)
                                                            || contactNameMatches.Contains(o.UserId)
                                                            || o.User!.UsernameNormalized.StartsWith(usernamePrefix))))
                .OrderByDescending(cm => cm.Chat!.LastActivityAt)
                .Select(cm => cm.ChatId)
                .Take(GlobalSectionLimit)
                .ToListAsync(ct);
        }

        var ids = groupIds.Concat(privateIds).Distinct().ToList();
        if (ids.Count == 0) return [];
        var found = await summaries.BuildAsync(me, ids, visibleOnly: true, ct);
        return found
            .OrderByDescending(c => c.LastActivityAt)
            .Take(GlobalSectionLimit)
            .ToList();
    }

    private sealed record UserHit(Guid Id, string UsernameNormalized, string DisplayName, bool IsContact, bool IsRelated);

    /// <summary>
    /// People search with the same rules as <c>GET api/users/search</c>: contacts and members of my chats match by
    /// display name, contact name or username prefix; everybody else only by a username prefix of at least
    /// <see cref="Limits.GlobalUsernameSearchMinLength"/> characters (no enumeration by display name). Excludes me,
    /// deleted users and users who blocked me. Contacts first, then the exact username match.
    /// </summary>
    private async Task<List<UserSummaryDto>> FindUsersAsync(Guid me, string q, int limit, CancellationToken ct)
    {
        var pattern = LikePattern.Contains(q);
        var prefix = q.ToLowerInvariant();

        var myContacts = db.Contacts.Where(c => c.OwnerId == me).Select(c => c.ContactUserId);
        var myChats = db.ChatMembers.Where(cm => cm.UserId == me).Select(cm => cm.ChatId);
        var coMembers = db.ChatMembers.Where(cm => myChats.Contains(cm.ChatId)).Select(cm => cm.UserId);
        var nameMatches = db.Users.Where(dbFunctions.UserDisplayNameMatches(pattern)).Select(u => u.Id);
        var contactNameMatches = db.Contacts
            .Where(c => c.OwnerId == me)
            .Where(dbFunctions.ContactNameMatches(pattern))
            .Select(c => c.ContactUserId);
        var blockedMe = db.Blocks.Where(b => b.BlockedId == me).Select(b => b.BlockerId);

        var eligible = db.Users.AsNoTracking().Where(u => u.Id != me && !u.IsDeleted && !blockedMe.Contains(u.Id));

        var related = await eligible
            .Where(u => myContacts.Contains(u.Id) || coMembers.Contains(u.Id))
            .Where(u => nameMatches.Contains(u.Id) || contactNameMatches.Contains(u.Id) || u.UsernameNormalized.StartsWith(prefix))
            .OrderByDescending(u => myContacts.Contains(u.Id))
            .ThenByDescending(u => u.UsernameNormalized == prefix)
            .ThenBy(u => u.DisplayName)
            .ThenBy(u => u.Id)
            .Select(u => new UserHit(u.Id, u.UsernameNormalized, u.DisplayName, myContacts.Contains(u.Id), true))
            .Take(limit)
            .ToListAsync(ct);

        var hits = related;
        if (q.Length >= Limits.GlobalUsernameSearchMinLength && hits.Count < limit)
        {
            var known = hits.Select(h => h.Id).ToList();
            var global = await eligible
                .Where(u => u.UsernameNormalized.StartsWith(prefix) && !known.Contains(u.Id))
                .OrderBy(u => u.UsernameNormalized.Length)
                .ThenBy(u => u.UsernameNormalized)
                .Select(u => new UserHit(u.Id, u.UsernameNormalized, u.DisplayName, false, false))
                .Take(limit - hits.Count)
                .ToListAsync(ct);
            hits = [.. hits, .. global];
        }
        if (hits.Count == 0) return [];

        var ordered = hits
            .OrderByDescending(h => h.IsContact)
            .ThenByDescending(h => h.UsernameNormalized == prefix)
            .ThenByDescending(h => h.IsRelated)
            .ThenBy(h => h.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(h => h.Id)
            .Take(limit)
            .ToList();
        var cards = await userSummaries.GetAsync(me, ordered.Select(h => h.Id), ct);
        return ordered.Select(h => cards.GetValueOrDefault(h.Id)).OfType<UserSummaryDto>().ToList();
    }
}
