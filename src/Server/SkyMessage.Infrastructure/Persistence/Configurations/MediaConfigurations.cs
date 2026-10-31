using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence.Configurations;

internal sealed class MediaConfiguration : IEntityTypeConfiguration<Media>
{
    public void Configure(EntityTypeBuilder<Media> b)
    {
        b.ToTable("media");
        b.HasKey(x => x.Id);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.OwnerId);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.Property(x => x.FileName).HasMaxLength(Limits.FileNameMaxLength).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.ProcessingError).HasMaxLength(1000);
    }
}

internal sealed class MediaThumbnailConfiguration : IEntityTypeConfiguration<MediaThumbnail>
{
    public void Configure(EntityTypeBuilder<MediaThumbnail> b)
    {
        b.HasKey(x => x.MediaId);
        b.HasOne<Media>().WithOne().HasForeignKey<MediaThumbnail>(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.ContentType).HasMaxLength(64);
    }
}

internal sealed class MediaChunkConfiguration : IEntityTypeConfiguration<MediaChunk>
{
    public void Configure(EntityTypeBuilder<MediaChunk> b)
    {
        // The migration also runs: ALTER TABLE media_chunks ALTER COLUMN data SET STORAGE EXTERNAL
        // (out-of-line, no TOAST compression — media is already compressed).
        b.HasKey(x => new { x.MediaId, x.Index });
        b.HasOne<Media>().WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
    }
}
