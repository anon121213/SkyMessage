using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.HasKey(x => x.Id);
        // Wider than Limits.UsernameMaxLength: deleted accounts get "deleted_{id:N}" (40 chars).
        b.Property(x => x.Username).HasMaxLength(User.StoredUsernameMaxLength).IsRequired();
        b.Property(x => x.UsernameNormalized).HasMaxLength(User.StoredUsernameMaxLength).IsRequired();
        b.HasIndex(x => x.UsernameNormalized).IsUnique();
        b.Property(x => x.DisplayName).HasMaxLength(Limits.DisplayNameMaxLength).IsRequired();
        b.HasIndex(x => x.DisplayName).HasMethod("gin").HasOperators("gin_trgm_ops");
        b.Property(x => x.Bio).HasMaxLength(Limits.BioMaxLength);
        b.Property(x => x.PhoneNumber).HasMaxLength(20);
        b.HasIndex(x => x.PhoneNumber).IsUnique();
        b.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        // Guards the race with media cleanup: an avatar whose media row disappears is cleared, not dangling.
        b.HasOne<Media>().WithMany().HasForeignKey(x => x.AvatarMediaId).OnDelete(DeleteBehavior.SetNull);
        b.OwnsOne(x => x.Privacy);
        b.Navigation(x => x.Privacy).IsRequired();
    }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserId);
        b.Property(x => x.RefreshTokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.RefreshTokenHash).IsUnique();
        b.Property(x => x.PreviousRefreshTokenHash).HasMaxLength(64);
        b.HasIndex(x => x.PreviousRefreshTokenHash);
        b.Property(x => x.MediaKey).IsRequired();
        b.Property(x => x.DeviceName).HasMaxLength(Limits.DeviceNameMaxLength);
        b.Property(x => x.AppVersion).HasMaxLength(32);
        b.Property(x => x.OsVersion).HasMaxLength(64);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.PushToken).HasMaxLength(1024);
        b.Ignore(x => x.IsActive);
    }
}

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> b)
    {
        b.HasKey(x => new { x.OwnerId, x.ContactUserId });
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ContactUser).WithMany().HasForeignKey(x => x.ContactUserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.ContactUserId);
        b.Property(x => x.CustomName).HasMaxLength(Limits.ContactNameMaxLength);
    }
}

internal sealed class BlockConfiguration : IEntityTypeConfiguration<Block>
{
    public void Configure(EntityTypeBuilder<Block> b)
    {
        b.HasKey(x => new { x.BlockerId, x.BlockedId });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.BlockerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Blocked).WithMany().HasForeignKey(x => x.BlockedId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.BlockedId);
    }
}
