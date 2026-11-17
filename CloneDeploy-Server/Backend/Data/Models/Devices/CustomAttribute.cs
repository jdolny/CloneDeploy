using CloneDeploy_Server.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class CustomAttribute
    {
        public int Id { get; set; }

        public string Name { get; set; } = null!;

        public string? Description { get; set; }

        public bool ClientImagingAvailable { get; set; }
    }

    public class CustomAttributeConfiguration : IEntityTypeConfiguration<CustomAttribute>
    {
        public void Configure(EntityTypeBuilder<CustomAttribute> builder)
        {
            builder.Property(t => t.Name)
                .HasColumnType("citext");

            builder.HasIndex(t => t.Name)
                .IsUnique();
        }
    }
}
