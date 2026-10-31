using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Common.Interfaces;

/// <summary>EF Core unit of work over PostgreSQL. Implemented by Infrastructure's AppDbContext (scoped).</summary>
public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<Session> Sessions { get; }
    DbSet<Contact> Contacts { get; }
    DbSet<Block> Blocks { get; }

    DbSet<Chat> Chats { get; }
    DbSet<ChatMember> ChatMembers { get; }
    DbSet<ChatBan> ChatBans { get; }
    DbSet<Message> Messages { get; }
    DbSet<MessageAttachment> MessageAttachments { get; }
    DbSet<MessageReaction> MessageReactions { get; }
    DbSet<MessageMention> MessageMentions { get; }
    DbSet<MessageHidden> HiddenMessages { get; }
    DbSet<PinnedMessage> PinnedMessages { get; }

    DbSet<Media> Media { get; }
    DbSet<MediaThumbnail> MediaThumbnails { get; }
    DbSet<MediaChunk> MediaChunks { get; }

    DbSet<Call> Calls { get; }
    DbSet<CallParticipant> CallParticipants { get; }

    DbSet<ServerProperty> ServerProperties { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
