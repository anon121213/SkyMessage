using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne(x => x.Chat).WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ReplyTo).WithMany().HasForeignKey(x => x.ReplyToMessageId).OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => new { x.ChatId, x.Seq }).IsUnique();
        // Keyset pagination for GET .../messages/changes: (updated_at, id) > cursor.
        b.HasIndex(x => new { x.ChatId, x.UpdatedAt, x.Id });
        b.HasIndex(x => new { x.SenderId, x.ClientMessageId }).IsUnique();

        b.Property(x => x.Text).HasMaxLength(Limits.MessageTextMaxLength);
        b.HasIndex(x => x.Text).HasMethod("gin").HasOperators("gin_trgm_ops");
        b.Property(x => x.ForwardSenderName).HasMaxLength(Limits.DisplayNameMaxLength);

        b.OwnsMany(x => x.Entities, e => e.ToJson());
        b.OwnsOne(x => x.SystemEvent, e =>
        {
            e.ToJson();
            // SystemEvent.MessageId (the pinned message) must not become the ownership key, which EF would
            // otherwise bind by convention ("{Owner}Id") and overwrite with the service message's own id.
            e.WithOwner().HasForeignKey("OwnerMessageId");
        });
        b.OwnsOne(x => x.CallLog, e => e.ToJson());

        b.HasMany(x => x.Attachments).WithOne(x => x.Message).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Reactions).WithOne(x => x.Message).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);

        b.Ignore(x => x.IsDeleted);
    }
}

internal sealed class MessageAttachmentConfiguration : IEntityTypeConfiguration<MessageAttachment>
{
    public void Configure(EntityTypeBuilder<MessageAttachment> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.MediaId);
        b.HasIndex(x => new { x.MessageId, x.Order });
    }
}

internal sealed class MessageReactionConfiguration : IEntityTypeConfiguration<MessageReaction>
{
    public void Configure(EntityTypeBuilder<MessageReaction> b)
    {
        b.HasKey(x => new { x.MessageId, x.UserId });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Emoji).HasMaxLength(Limits.ReactionMaxLength).IsRequired();
    }
}

internal sealed class MessageMentionConfiguration : IEntityTypeConfiguration<MessageMention>
{
    public void Configure(EntityTypeBuilder<MessageMention> b)
    {
        b.HasKey(x => new { x.MessageId, x.UserId });
        b.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.ChatId, x.Seq });
    }
}

internal sealed class MessageHiddenConfiguration : IEntityTypeConfiguration<MessageHidden>
{
    public void Configure(EntityTypeBuilder<MessageHidden> b)
    {
        b.ToTable("hidden_messages");
        b.HasKey(x => new { x.MessageId, x.UserId });
        b.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}

internal sealed class PinnedMessageConfiguration : IEntityTypeConfiguration<PinnedMessage>
{
    public void Configure(EntityTypeBuilder<PinnedMessage> b)
    {
        b.HasKey(x => new { x.ChatId, x.MessageId });
        b.HasOne<Chat>().WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}
