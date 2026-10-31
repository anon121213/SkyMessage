using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence.Configurations;

internal sealed class CallConfiguration : IEntityTypeConfiguration<Call>
{
    public void Configure(EntityTypeBuilder<Call> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne(x => x.Chat).WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.RoomName).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.RoomName).IsUnique();
        b.HasIndex(x => new { x.ChatId, x.Status });
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasMany(x => x.Participants).WithOne(x => x.Call).HasForeignKey(x => x.CallId).OnDelete(DeleteBehavior.Cascade);
        b.Ignore(x => x.IsFinished);
    }
}

internal sealed class CallParticipantConfiguration : IEntityTypeConfiguration<CallParticipant>
{
    public void Configure(EntityTypeBuilder<CallParticipant> b)
    {
        b.HasKey(x => new { x.CallId, x.UserId });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserId);
    }
}
