using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>Other people: profiles, batch summaries, search and phone-book discovery.</summary>
public sealed class UserDirectoryService(
    IAppDbContext db,
    IUserSummaryService summaries,
    IPrivacyService privacy,
    IIdentityDbFunctions dbFunctions)
{
    public const int DefaultSearchLimit = 30;
    private const int MaxSearchTermLength = 64;
    private const int MaxLookupRegions = 4;

    public async Task<UserProfileDto> GetProfileAsync(Guid viewerId, Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, ct))
            throw new NotFoundException(ErrorCodes.UserNotFound);
        return await BuildProfileAsync(viewerId, userId, ct);
    }

    public async Task<UserProfileDto> GetProfileByUsernameAsync(Guid viewerId, string username, CancellationToken ct)
    {
        var normalized = User.Normalize(username);
        if (normalized.Length is 0 or > Limits.UsernameMaxLength)
            throw new NotFoundException(ErrorCodes.UserNotFound);
        var userId = await db.Users.AsNoTracking()
            .Where(u => u.UsernameNormalized == normalized && !u.IsDeleted)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(ErrorCodes.UserNotFound);
        return await BuildProfileAsync(viewerId, userId, ct);
    }

    /// <summary>Unknown ids are skipped; order follows the request.</summary>
    public async Task<IReadOnlyList<UserSummaryDto>> GetBatchAsync(Guid viewerId, IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().Take(Limits.MaxBatchUsers).ToList();
        var map = await summaries.GetAsync(viewerId, ids, ct);
        return ids.Where(map.ContainsKey).Select(id => map[id]).ToList();
    }

    /// <summary>
    /// People I know (contacts, members of my chats) match by display name, my contact name or username
    /// prefix; everyone else only by a username prefix of at least
    /// <see cref="Limits.GlobalUsernameSearchMinLength"/> chars, so the directory cannot be enumerated by name.
    /// </summary>
    public async Task<IReadOnlyList<UserSummaryDto>> SearchAsync(Guid viewerId, string? query, int? limit, CancellationToken ct)
    {
        var term = (query ?? "").Trim().TrimStart('@').Trim();
        if (term.Length < Limits.SearchQueryMinLength) return [];
        if (term.Length > MaxSearchTermLength) term = term[..MaxSearchTermLength];
        var take = Math.Clamp(limit ?? DefaultSearchLimit, 1, Limits.SearchResultsMax);
        var lower = term.ToLowerInvariant();
        var pattern = IdentitySearch.ContainsPattern(term);

        var myChatIds = db.ChatMembers.Where(m => m.UserId == viewerId).Select(m => m.ChatId);
        var circle = db.ChatMembers.Where(m => myChatIds.Contains(m.ChatId)).Select(m => m.UserId)
            .Union(db.Contacts.Where(c => c.OwnerId == viewerId).Select(c => c.ContactUserId));
        var blockedMe = db.Blocks.Where(b => b.BlockedId == viewerId).Select(b => b.BlockerId);
        var namedByMe = db.Contacts
            .Where(c => c.OwnerId == viewerId)
            .Where(dbFunctions.ContactNameMatches(pattern))
            .Select(c => c.ContactUserId);
        var candidates = db.Users.AsNoTracking()
            .Where(u => u.Id != viewerId && !u.IsDeleted && !blockedMe.Contains(u.Id));

        var knownMatch = dbFunctions.DisplayNameMatches(pattern)
            .OrElse(u => u.UsernameNormalized.StartsWith(lower) || namedByMe.Contains(u.Id));
        var known = await candidates
            .Where(u => circle.Contains(u.Id))
            .Where(knownMatch)
            .Select(u => new
            {
                u.Id,
                u.UsernameNormalized,
                u.DisplayName,
                IsContact = db.Contacts.Any(c => c.OwnerId == viewerId && c.ContactUserId == u.Id),
            })
            .OrderByDescending(h => h.IsContact)
            .ThenByDescending(h => h.UsernameNormalized == lower)
            .ThenBy(h => h.DisplayName)
            .Take(take)
            .ToListAsync(ct);

        var hits = known
            .Select(h => new SearchHit(h.Id, h.IsContact, h.UsernameNormalized == lower, InCircle: true))
            .ToList();

        if (term.Length >= Limits.GlobalUsernameSearchMinLength)
        {
            var strangers = await candidates
                .Where(u => u.UsernameNormalized.StartsWith(lower) && !circle.Contains(u.Id))
                .OrderByDescending(u => u.UsernameNormalized == lower)
                .ThenBy(u => u.UsernameNormalized.Length)
                .ThenBy(u => u.UsernameNormalized)
                .Select(u => new { u.Id, u.UsernameNormalized })
                .Take(take)
                .ToListAsync(ct);
            hits.AddRange(strangers.Select(s => new SearchHit(s.Id, false, s.UsernameNormalized == lower, InCircle: false)));
        }

        // Contacts first, then the exact username match, then people I know, then the rest (stable sort).
        var ordered = hits
            .OrderByDescending(h => h.IsContact)
            .ThenByDescending(h => h.ExactUsername)
            .ThenByDescending(h => h.InCircle)
            .Select(h => h.UserId)
            .Distinct()
            .Take(take)
            .ToList();
        var map = await summaries.GetAsync(viewerId, ordered, ct);
        return ordered.Where(map.ContainsKey).Select(id => map[id]).ToList();
    }

    /// <summary>
    /// Phone-book discovery: users whose FindByPhone privacy allows the caller (no blocks either way).
    /// Numbers without '+' are read in each region of <see cref="FindByPhonesRequest.DefaultRegions"/> (else
    /// <see cref="FindByPhonesRequest.DefaultRegion"/>), then the caller's own region, then RU; the first
    /// region whose reading belongs to a discoverable user wins. Submitted strings are echoed.
    /// </summary>
    public async Task<IReadOnlyList<PhoneMatchDto>> FindByPhonesAsync(Guid viewerId, FindByPhonesRequest request, CancellationToken ct)
    {
        if (request.PhoneNumbers is not { Count: > 0 }) return [];

        var myPhone = await db.Users.AsNoTracking()
            .Where(u => u.Id == viewerId)
            .Select(u => u.PhoneNumber)
            .FirstOrDefaultAsync(ct);
        var regions = LookupRegions(request, PhoneNormalizer.RegionOf(myPhone));

        var submitted = new List<(string Raw, IReadOnlyList<string> Candidates)>();
        foreach (var raw in request.PhoneNumbers.Take(Limits.FindByPhonesMax))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var candidates = regions
                .Select(region => PhoneNormalizer.Normalize(raw, region))
                .OfType<string>()
                .Distinct()
                .ToList();
            if (candidates.Count > 0) submitted.Add((raw, candidates));
        }
        if (submitted.Count == 0) return [];

        var numbers = submitted.SelectMany(s => s.Candidates).Distinct().ToList();
        var matches = await db.Users.AsNoTracking()
            .Where(u => u.PhoneNumber != null && numbers.Contains(u.PhoneNumber) && u.Id != viewerId && !u.IsDeleted)
            .Where(u => u.Privacy.FindByPhone == PrivacyLevel.Everybody
                        || (u.Privacy.FindByPhone == PrivacyLevel.Contacts
                            && db.Contacts.Any(c => c.OwnerId == u.Id && c.ContactUserId == viewerId)))
            .Where(u => !db.Blocks.Any(b => (b.BlockerId == u.Id && b.BlockedId == viewerId) || (b.BlockerId == viewerId && b.BlockedId == u.Id)))
            .Select(u => new { u.Id, Phone = u.PhoneNumber! })
            .ToListAsync(ct);
        if (matches.Count == 0) return [];

        var userByPhone = matches.ToDictionary(m => m.Phone, m => m.Id);
        var map = await summaries.GetAsync(viewerId, matches.Select(m => m.Id), ct);
        var result = new List<PhoneMatchDto>();
        foreach (var (raw, candidates) in submitted)
        {
            foreach (var candidate in candidates)
            {
                if (userByPhone.TryGetValue(candidate, out var id) && map.TryGetValue(id, out var user))
                {
                    result.Add(new PhoneMatchDto { PhoneNumber = raw, User = user });
                    break;
                }
            }
        }
        return result;
    }

    internal static IReadOnlyList<string> LookupRegions(FindByPhonesRequest request, string? ownRegion)
    {
        IEnumerable<string?> preferred = [request.DefaultRegion];
        if (request.DefaultRegions is { Count: > 0 } regions)
            preferred = regions;
        return preferred
            .Take(MaxLookupRegions)
            .Append(ownRegion)
            .Append(PhoneNormalizer.FallbackRegion)
            .Select(PhoneNormalizer.NormalizeRegion)
            .OfType<string>()
            .Distinct()
            .ToList();
    }

    private async Task<UserProfileDto> BuildProfileAsync(Guid viewerId, Guid userId, CancellationToken ct)
    {
        var isSelf = viewerId == userId;
        var privateKey = isSelf ? Chat.SavedKeyFor(viewerId) : Chat.PrivateKeyFor(viewerId, userId);
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Bio,
                u.PhoneNumber,
                u.IsDeleted,
                PhoneLevel = u.Privacy.PhoneNumber,
                OwnerBlockedViewer = db.Blocks.Any(b => b.BlockerId == userId && b.BlockedId == viewerId),
                ViewerBlockedOwner = db.Blocks.Any(b => b.BlockerId == viewerId && b.BlockedId == userId),
                OwnerHasViewer = db.Contacts.Any(c => c.OwnerId == userId && c.ContactUserId == viewerId),
                ViewerHasOwner = db.Contacts.Any(c => c.OwnerId == viewerId && c.ContactUserId == userId),
                PrivateChatId = db.ChatMembers
                    .Where(m => m.UserId == viewerId && m.Chat!.PrivateKey == privateKey && !m.Chat.IsDeleted)
                    .Select(m => (Guid?)m.ChatId)
                    .FirstOrDefault(),
                CommonGroups = db.ChatMembers.Count(m => m.UserId == viewerId
                                                         && m.Chat!.Type == ChatType.Group && !m.Chat.IsDeleted
                                                         && db.ChatMembers.Any(o => o.ChatId == m.ChatId && o.UserId == userId)),
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(ErrorCodes.UserNotFound);
        var summary = await summaries.GetOneAsync(viewerId, userId, ct)
                      ?? throw new NotFoundException(ErrorCodes.UserNotFound);

        // A user who blocked the viewer (or a deleted account) shows nothing beyond the card itself.
        var hidden = !isSelf && (row.OwnerBlockedViewer || row.IsDeleted);
        var phoneVisible = isSelf || (!hidden && PrivacyRules.Allows(row.PhoneLevel, row.OwnerHasViewer));
        var canMessage = !row.IsDeleted && await privacy.CanMessageAsync(viewerId, userId, ct);
        var canCall = !isSelf && !row.IsDeleted && await privacy.CanCallAsync(viewerId, userId, ct);

        return new UserProfileDto
        {
            User = summary,
            Bio = hidden ? null : row.Bio,
            PhoneNumber = phoneVisible ? row.PhoneNumber : null,
            IsMutualContact = !isSelf && row.ViewerHasOwner && row.OwnerHasViewer,
            IsBlockedByMe = !isSelf && row.ViewerBlockedOwner,
            CanMessage = canMessage,
            CanCall = canCall,
            PrivateChatId = row.PrivateChatId,
            CommonGroupsCount = isSelf ? 0 : row.CommonGroups,
        };
    }

    private sealed record SearchHit(Guid UserId, bool IsContact, bool ExactUsername, bool InCircle);
}
