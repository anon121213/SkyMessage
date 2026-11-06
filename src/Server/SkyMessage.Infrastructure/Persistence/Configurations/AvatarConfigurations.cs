using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence.Configurations;

internal sealed class AvatarPhotoConfiguration : IEntityTypeConfiguration<AvatarPhoto>
{
    public void Configure(EntityTypeBuilder<AvatarPhoto> b)
    {
        b.ToTable("avatar_photos", t => t.HasCheckConstraint("ck_avatar_photos_one_owner", "num_nonnulls(user_id, chat_id) = 1"));
        b.HasKey(x => x.Id);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Chat>().WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        // A photo removed by media cleanup leaves the history with it.
        b.HasOne<Media>().WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.MediaId }).IsUnique().HasFilter("user_id IS NOT NULL");
        b.HasIndex(x => new { x.ChatId, x.MediaId }).IsUnique().HasFilter("chat_id IS NOT NULL");
        b.HasIndex(x => x.MediaId);
    }
}
