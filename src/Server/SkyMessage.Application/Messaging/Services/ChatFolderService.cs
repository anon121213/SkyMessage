using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging.Services;

/// <summary>
/// My chat folders and chat list settings (SPEC §6.3 "Folders and the archive"). Every change runs in a transaction that
/// first locks my <see cref="ChatListPreferences"/> row (created by the first change), so changes from several devices
/// apply one after another, each on top of the previous one. A real change bumps the revision and sends the whole new
/// state to all my devices; a change that changes nothing is silent.
/// </summary>
internal sealed class ChatFolderService(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    ChatSummaryBuilder summaries,
    MessagingEvents events) : IChatFolderService
{
    public async Task<ChatFoldersDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        // The revision first: a change committed between the two reads then shows up with an older revision, which the
        // apps replace when that change's event arrives (the other way round they would keep stale folders).
        var prefs = await db.ChatListPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == me, cancellationToken);
        var folders = await db.ChatFolders.AsNoTracking().Where(f => f.UserId == me).ToListAsync(cancellationToken);
        return ToDto(prefs, folders);
    }

    public Task<ChatFoldersDto> SaveFolderAsync(Guid folderId, SaveChatFolderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (folderId == Guid.Empty) throw MessagingErrors.Validation("folderId", "Folder id is required.");
        // The fields alone first (every problem at once, before any lock); the whole folder once it is laid over the stored one.
        ChatFolderInput.Validate(request);
        var me = currentUser.UserId;
        return MutateAsync(me, async (folders, now, ct) =>
        {
            var folder = folders.FirstOrDefault(f => f.Id == folderId);
            if (folder is null)
            {
                // Someone else's folder id gets the same answer as one that does not exist, and an edit of a folder that
                // was deleted meanwhile (on another device) does not bring it back.
                if (!request.Create || await db.ChatFolders.AnyAsync(f => f.Id == folderId, ct))
                    throw new NotFoundException(ErrorCodes.FolderNotFound);
                if (folders.Count >= Limits.MaxChatFolders) throw new BadRequestException(ErrorCodes.FolderLimit);
            }
            var draft = ChatFolderInput.Apply(request, folder is null ? null : ChatFolderDraft.Of(folder));
            // Chats that are not (or no longer) mine are dropped from every list, the folder's pins too; the rest keep their order.
            var mine = await MyChatIdsAsync(me, draft.IncludedChatIds.Concat(draft.ExcludedChatIds).Concat(folder?.PinnedChatIds ?? []), ct);

            if (folder is null)
            {
                folder = new ChatFolder
                {
                    Id = folderId,
                    UserId = me,
                    Position = folders.Count == 0 ? 0 : folders.Max(f => f.Position) + 1,
                    CreatedAt = now,
                };
                db.ChatFolders.Add(folder);
                folders.Add(folder);
            }

            folder.Name = draft.Name;
            folder.Icon = draft.Icon;
            folder.IncludeContacts = draft.IncludeContacts;
            folder.IncludeNonContacts = draft.IncludeNonContacts;
            folder.IncludeGroups = draft.IncludeGroups;
            folder.IncludeChannels = draft.IncludeChannels;
            folder.ExcludeMuted = draft.ExcludeMuted;
            folder.ExcludeRead = draft.ExcludeRead;
            folder.ExcludeArchived = draft.ExcludeArchived;
            if (Changed(folder.IncludedChatIds, draft.IncludedChatIds.Where(mine.Contains)) is { } included) folder.IncludedChatIds = included;
            if (Changed(folder.ExcludedChatIds, draft.ExcludedChatIds.Where(mine.Contains)) is { } excluded) folder.ExcludedChatIds = excluded;
            if (Changed(folder.PinnedChatIds, folder.PinnedChatIds.Where(mine.Contains)) is { } pins) folder.PinnedChatIds = pins;
        }, cancellationToken);
    }

    public Task<ChatFoldersDto> DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken = default) =>
        MutateAsync(currentUser.UserId, (folders, _, _) =>
        {
            var folder = Find(folders, folderId);
            db.ChatFolders.Remove(folder);
            folders.Remove(folder);
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<ChatFoldersDto> ReorderFoldersAsync(ReorderChatFoldersRequest request, CancellationToken cancellationToken = default)
    {
        var requested = request.FolderIds ?? [];
        if (requested.Count is 0 or > Limits.MaxChatFolders)
            throw MessagingErrors.Validation("folderIds", $"List 1 to {Limits.MaxChatFolders} folders.");
        if (requested.Contains(Guid.Empty) || requested.Distinct().Count() != requested.Count)
            throw MessagingErrors.Validation("folderIds", "Folder ids must be distinct and not empty.");
        return MutateAsync(currentUser.UserId, (folders, _, _) =>
        {
            var byId = folders.ToDictionary(f => f.Id);
            var arranged = PinOrderRules.Arrange(Ordered(folders).Select(f => f.Id).ToList(), requested);
            for (var i = 0; i < arranged.Count; i++)
                byId[arranged[i]].Position = i;
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public Task<ChatFoldersDto> PinAsync(Guid folderId, Guid chatId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        return MutateAsync(me, async (folders, _, ct) =>
        {
            var folder = Find(folders, folderId);
            if (!await db.ChatMembers.AnyAsync(m => m.ChatId == chatId && m.UserId == me && !m.IsHidden && !m.Chat!.IsDeleted, ct))
                throw new NotFoundException(ErrorCodes.ChatNotFound);
            // Pins of chats I left or that were deleted do not take up room.
            await PrunePinsAsync(me, folder, ct);
            if (folder.PinnedChatIds.Contains(chatId)) return;
            if (folder.PinnedChatIds.Count >= Limits.MaxChatFolderChats)
                throw MessagingErrors.Validation("chatId", $"A folder pins at most {Limits.MaxChatFolderChats} chats.");
            // On top of the folder's other pins.
            folder.PinnedChatIds = [chatId, .. folder.PinnedChatIds];
        }, cancellationToken);
    }

    public Task<ChatFoldersDto> UnpinAsync(Guid folderId, Guid chatId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        return MutateAsync(me, async (folders, _, ct) =>
        {
            var folder = Find(folders, folderId);
            if (folder.PinnedChatIds.Contains(chatId))
                folder.PinnedChatIds = folder.PinnedChatIds.Where(id => id != chatId).ToList();
            await PrunePinsAsync(me, folder, ct);
        }, cancellationToken);
    }

    public Task<ChatFoldersDto> ReorderPinnedAsync(Guid folderId, ReorderPinnedChatsRequest request, CancellationToken cancellationToken = default)
    {
        var requested = request.ChatIds ?? [];
        if (requested.Count is 0 or > Limits.MaxPinnedChatsReorder)
            throw MessagingErrors.Validation("chatIds", $"List 1 to {Limits.MaxPinnedChatsReorder} pinned chats.");
        if (requested.Contains(Guid.Empty) || requested.Distinct().Count() != requested.Count)
            throw MessagingErrors.Validation("chatIds", "Chat ids must be distinct and not empty.");
        var me = currentUser.UserId;
        return MutateAsync(me, async (folders, _, ct) =>
        {
            var folder = Find(folders, folderId);
            await PrunePinsAsync(me, folder, ct);
            // The same arrangement as the global pins: the listed pinned chats take the places they held, in this order.
            var arranged = PinOrderRules.Arrange(folder.PinnedChatIds, requested);
            if (!arranged.SequenceEqual(folder.PinnedChatIds)) folder.PinnedChatIds = arranged.ToList();
        }, cancellationToken);
    }

    public Task<ChatFoldersDto> UpdateSettingsAsync(UpdateChatListSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Only the settings the request sets: another device switching another one at the same moment keeps its change.
        return MutateAsync(currentUser.UserId, (_, _, _) => Task.CompletedTask, cancellationToken, prefs =>
        {
            if (request.ArchiveNewFromStrangers is { } strangers) prefs.ArchiveNewFromStrangers = strangers;
            if (request.KeepArchived is { } keep) prefs.KeepArchived = keep;
            if (request.HideArchive is { } hide) prefs.HideArchive = hide;
        });
    }

    public async Task MarkReadAsync(Guid? folderId, CancellationToken cancellationToken = default)
    {
        var me = currentUser.UserId;
        ChatFolderDto? folder = null;
        if (folderId is { } id)
        {
            var row = await db.ChatFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && f.UserId == me, cancellationToken)
                      ?? throw new NotFoundException(ErrorCodes.FolderNotFound);
            folder = ToDto(row);
        }

        // The folder's chats exactly as the apps see them: the same summaries, the same rules.
        var now = clock.UtcNow;
        var chats = await summaries.BuildAsync(me, chatIds: null, visibleOnly: true, cancellationToken);
        var unread = chats
            .Where(c => ChatFolderRules.IsUnread(c)
                        && (folder is null ? !c.IsArchived : ChatFolderRules.Contains(folder, c, c.Peer?.IsContact == true, now)))
            .ToList();
        if (unread.Count == 0) return;

        var read = new List<(Guid ChatId, long UpToSeq)>();
        foreach (var chat in unread)
        {
            var chatId = chat.Id;
            var upTo = chat.LastMessageSeq;
            // Only ever forward, like a read from the chat itself (another device may be ahead).
            var updated = await db.ChatMembers
                .Where(m => m.ChatId == chatId && m.UserId == me && m.LastReadSeq < upTo)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, upTo), cancellationToken);
            if (updated > 0) read.Add((chatId, upTo));
        }
        if (read.Count == 0) return;

        var readIds = read.Select(r => r.ChatId).ToList();
        var members = (await db.ChatMembers.AsNoTracking()
                .Where(m => readIds.Contains(m.ChatId))
                .Select(m => new { m.ChatId, m.UserId })
                .ToListAsync(CancellationToken.None))
            .ToLookup(m => m.ChatId, m => m.UserId);
        foreach (var (chatId, upTo) in read)
        {
            await events.ToUsersAsync(members[chatId].ToList(), "MessagesRead",
                c => c.MessagesRead(new MessagesReadEvent { ChatId = chatId, UserId = me, UpToSeq = upTo }));
        }
    }

    /// <summary>
    /// Runs one change under my preferences row lock. <paramref name="change"/> edits the tracked folders (and keeps the
    /// list in step when it adds or removes one); <paramref name="settings"/> edits the preferences row.
    /// </summary>
    private async Task<ChatFoldersDto> MutateAsync(Guid me, Func<List<ChatFolder>, DateTimeOffset, CancellationToken, Task> change,
        CancellationToken ct, Action<ChatListPreferences>? settings = null)
    {
        var now = clock.UtcNow;
        ChatFoldersDto state;
        bool changed;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var prefs = await LockPreferencesAsync(me, now, ct);
            var folders = await db.ChatFolders.Where(f => f.UserId == me).ToListAsync(ct);
            await change(folders, now, ct);
            settings?.Invoke(prefs);

            changed = db.ChangeTracker.HasChanges();
            if (changed)
            {
                foreach (var entry in db.ChangeTracker.Entries<ChatFolder>())
                    if (entry.State is EntityState.Added or EntityState.Modified) entry.Entity.UpdatedAt = now;
                prefs.Revision++;
                prefs.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }
            // Built while the lock is held: exactly the state this change left.
            state = ToDto(prefs, folders);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        if (changed) await events.ToUserAsync(me, "ChatFoldersChanged", c => c.ChatFoldersChanged(state));
        return state;
    }

    /// <summary>Creates my row on the first change (race-free), then locks it for the rest of the transaction.</summary>
    private async Task<ChatListPreferences> LockPreferencesAsync(Guid me, DateTimeOffset now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO chat_list_preferences (user_id, archive_new_from_strangers, keep_archived, hide_archive, revision, updated_at)
            VALUES ({me}, FALSE, TRUE, FALSE, 0, {now})
            ON CONFLICT (user_id) DO NOTHING
            """, ct);
        // SqlQuery over non-composable SQL: materialize with ToListAsync (see ChatSequence).
        await db.Database
            .SqlQuery<Guid>($"SELECT user_id AS \"Value\" FROM chat_list_preferences WHERE user_id = {me} FOR UPDATE")
            .ToListAsync(ct);
        return await db.ChatListPreferences.FirstAsync(p => p.UserId == me, ct);
    }

    private async Task<HashSet<Guid>> MyChatIdsAsync(Guid me, IEnumerable<Guid> chatIds, CancellationToken ct)
    {
        var ids = chatIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var mine = await db.ChatMembers.AsNoTracking()
            .Where(m => m.UserId == me && ids.Contains(m.ChatId) && !m.Chat!.IsDeleted)
            .Select(m => m.ChatId)
            .ToListAsync(ct);
        return mine.ToHashSet();
    }

    /// <summary>Drops the folder's pins of chats that are no longer mine (left, removed, deleted for everyone).</summary>
    private async Task PrunePinsAsync(Guid me, ChatFolder folder, CancellationToken ct)
    {
        if (folder.PinnedChatIds.Count == 0) return;
        var mine = await MyChatIdsAsync(me, folder.PinnedChatIds, ct);
        if (Changed(folder.PinnedChatIds, folder.PinnedChatIds.Where(mine.Contains)) is { } pins) folder.PinnedChatIds = pins;
    }

    /// <summary>The new list when it differs, else null (an unchanged folder stays unchanged for the change tracker).</summary>
    private static List<Guid>? Changed(List<Guid> current, IEnumerable<Guid> next)
    {
        var list = next.ToList();
        return current.SequenceEqual(list) ? null : list;
    }

    private static ChatFolder Find(List<ChatFolder> folders, Guid folderId) =>
        folders.FirstOrDefault(f => f.Id == folderId) ?? throw new NotFoundException(ErrorCodes.FolderNotFound);

    private static IEnumerable<ChatFolder> Ordered(IEnumerable<ChatFolder> folders) =>
        folders.OrderBy(f => f.Position).ThenBy(f => f.CreatedAt).ThenBy(f => f.Id);

    private static ChatFoldersDto ToDto(ChatListPreferences? prefs, IEnumerable<ChatFolder> folders) => new()
    {
        Revision = prefs?.Revision ?? 0,
        Folders = Ordered(folders).Select(ToDto).ToList(),
        Settings = prefs is null
            ? new ChatListSettingsDto()
            : new ChatListSettingsDto
            {
                ArchiveNewFromStrangers = prefs.ArchiveNewFromStrangers,
                KeepArchived = prefs.KeepArchived,
                HideArchive = prefs.HideArchive,
            },
    };

    private static ChatFolderDto ToDto(ChatFolder folder) => new()
    {
        Id = folder.Id,
        Name = folder.Name,
        Icon = folder.Icon,
        IncludeContacts = folder.IncludeContacts,
        IncludeNonContacts = folder.IncludeNonContacts,
        IncludeGroups = folder.IncludeGroups,
        IncludeChannels = folder.IncludeChannels,
        ExcludeMuted = folder.ExcludeMuted,
        ExcludeRead = folder.ExcludeRead,
        ExcludeArchived = folder.ExcludeArchived,
        IncludedChatIds = folder.IncludedChatIds.ToList(),
        ExcludedChatIds = folder.ExcludedChatIds.ToList(),
        PinnedChatIds = folder.PinnedChatIds.ToList(),
    };
}
