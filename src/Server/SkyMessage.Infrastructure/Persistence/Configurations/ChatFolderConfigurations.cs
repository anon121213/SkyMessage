using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence.Configurations;

internal sealed class ChatFolderConfiguration : IEntityTypeConfiguration<ChatFolder>
{
    public void Configure(EntityTypeBuilder<ChatFolder> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(Limits.ChatFolderNameMaxLength);
        b.Property(x => x.Icon).HasMaxLength(Limits.ChatFolderIconMaxLength);
        b.Property(x => x.IncludedChatIds).HasDefaultValueSql("'{}'::uuid[]");
        b.Property(x => x.ExcludedChatIds).HasDefaultValueSql("'{}'::uuid[]");
        b.Property(x => x.PinnedChatIds).HasDefaultValueSql("'{}'::uuid[]");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.Position });
    }
}

internal sealed class ChatListPreferencesConfiguration : IEntityTypeConfiguration<ChatListPreferences>
{
    public void Configure(EntityTypeBuilder<ChatListPreferences> b)
    {
        b.HasKey(x => x.UserId);
        // Rows are created by ChatFolderService's INSERT … ON CONFLICT DO NOTHING (race-free), which writes every column.
        b.HasOne<User>().WithOne().HasForeignKey<ChatListPreferences>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
