using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TimeStore.Core.Database;

/// <summary>
/// Entity type configuration for the Data entity.
/// </summary>
public class DataEntityTypeConfiguration : IEntityTypeConfiguration<Data>
{
    /// <summary>
    /// Configures the Data entity type in the database context.
    /// </summary>
    /// <param name="builder"></param>
    public void Configure(EntityTypeBuilder<Data> builder)
    {
        builder.Property(x => x.Id)
            .IsRequired()
            .HasDefaultValue(Guid.NewGuid());

        builder.Property(x => x.DeviceId).IsRequired();
        builder.Property(x => x.Timestamp).IsRequired();
        builder.Property(x => x.Value).IsRequired();
    }
}