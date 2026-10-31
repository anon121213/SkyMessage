using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Persistence.Configurations;

internal sealed class ServerPropertyConfiguration : IEntityTypeConfiguration<ServerProperty>
{
    public void Configure(EntityTypeBuilder<ServerProperty> b)
    {
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Value).HasMaxLength(4000).IsRequired();
    }
}
