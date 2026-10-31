using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence.Configurations;

internal sealed class ChatConfiguration : IEntityTypeConfiguration<Chat>
{
    public void Configure(EntityTypeBuilder<Chat> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(Limits.GroupTitleMaxLength);
        b.HasIndex(x => x.Title).HasMethod("gin").HasOperators("gin_trgm_ops");
        b.Property(x => x.Description).HasMaxLength(Limits.GroupDescriptionMaxLength);
        b.Property(x => x.PrivateKey).HasMaxLength(80);
        b.HasIndex(x => x.PrivateKey).IsUnique();
        b.Property(x => x.InviteToken).HasMaxLength(64);
        b.HasIndex(x => x.InviteToken).IsUnique();
        b.HasOne<Media>().WithMany().HasForeignKey(x => x.AvatarMediaId).OnDelete(DeleteBehavior.SetNull);
        b.OwnsOne(x => x.Permissions);
        b.Navigation(x => x.Permissions).IsRequired();
        b.HasMany(x => x.Members).WithOne(x => x.Chat).HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ChatBanConfiguration : IEntityTypeConfiguration<ChatBan>
{
    public void Configure(EntityTypeBuilder<ChatBan> b)
    {
        b.HasKey(x => new { x.ChatId, x.UserId });
        b.HasOne<Chat>().WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ChatMemberConfiguration : IEntityTypeConfiguration<ChatMember>
{
    public void Configure(EntityTypeBuilder<ChatMember> b)
    {
        b.HasKey(x => new { x.ChatId, x.UserId });
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserId);
    }
}
