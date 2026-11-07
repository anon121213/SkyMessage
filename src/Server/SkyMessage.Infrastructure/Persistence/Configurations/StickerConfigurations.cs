using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence.Configurations;

internal sealed class StickerPackConfiguration : IEntityTypeConfiguration<StickerPack>
{
    public void Configure(EntityTypeBuilder<StickerPack> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Title).HasMaxLength(Limits.StickerPackTitleMaxLength).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        // User rows are never deleted (accounts become tombstones), so a pack outlives its creator's account.
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CreatorId, x.CreatedAt });
    }
}

internal sealed class StickerConfiguration : IEntityTypeConfiguration<Sticker>
{
    public void Configure(EntityTypeBuilder<Sticker> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Emoji).HasDefaultValueSql("'{}'::text[]");
        b.HasOne<StickerPack>().WithMany().HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Cascade);
        // The media outlives the sticker (sent messages show it); media cleanup keeps it while a sticker uses it.
        b.HasOne<Media>().WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PackId, x.Position });
        b.HasIndex(x => x.MediaId);
    }
}

internal sealed class InstalledStickerPackConfiguration : IEntityTypeConfiguration<InstalledStickerPack>
{
    public void Configure(EntityTypeBuilder<InstalledStickerPack> b)
    {
        b.HasKey(x => new { x.UserId, x.PackId });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<StickerPack>().WithMany().HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.PackId);
    }
}

internal sealed class FavoriteStickerConfiguration : IEntityTypeConfiguration<FavoriteSticker>
{
    public void Configure(EntityTypeBuilder<FavoriteSticker> b)
    {
        b.HasKey(x => new { x.UserId, x.StickerId });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Sticker>().WithMany().HasForeignKey(x => x.StickerId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.StickerId);
    }
}

internal sealed class RecentStickerConfiguration : IEntityTypeConfiguration<RecentSticker>
{
    public void Configure(EntityTypeBuilder<RecentSticker> b)
    {
        b.HasKey(x => new { x.UserId, x.StickerId });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Sticker>().WithMany().HasForeignKey(x => x.StickerId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.StickerId);
    }
}

internal sealed class SavedGifConfiguration : IEntityTypeConfiguration<SavedGif>
{
    public void Configure(EntityTypeBuilder<SavedGif> b)
    {
        b.HasKey(x => new { x.UserId, x.MediaId });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        // Media cleanup keeps a saved GIF's media; the cascade only matters for media removed some other way.
        b.HasOne<Media>().WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.MediaId);
    }
}

internal sealed class StickerPreferencesConfiguration : IEntityTypeConfiguration<StickerPreferences>
{
    public void Configure(EntityTypeBuilder<StickerPreferences> b)
    {
        b.HasKey(x => x.UserId);
        // Rows are created by the sticker service's INSERT … ON CONFLICT DO NOTHING (race-free), which writes every column.
        b.HasOne<User>().WithOne().HasForeignKey<StickerPreferences>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
