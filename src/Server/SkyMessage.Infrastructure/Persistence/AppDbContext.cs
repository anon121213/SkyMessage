using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Block> Blocks => Set<Block>();

    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatMember> ChatMembers => Set<ChatMember>();
    public DbSet<ChatBan> ChatBans => Set<ChatBan>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<MessageMention> MessageMentions => Set<MessageMention>();
    public DbSet<MessageHidden> HiddenMessages => Set<MessageHidden>();
    public DbSet<PinnedMessage> PinnedMessages => Set<PinnedMessage>();

    public DbSet<Media> Media => Set<Media>();
    public DbSet<MediaThumbnail> MediaThumbnails => Set<MediaThumbnail>();
    public DbSet<MediaChunk> MediaChunks => Set<MediaChunk>();

    public DbSet<Call> Calls => Set<Call>();
    public DbSet<CallParticipant> CallParticipants => Set<CallParticipant>();

    public DbSet<ServerProperty> ServerProperties => Set<ServerProperty>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Trigram indexes power ILIKE '%q%' search over names and message text.
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
