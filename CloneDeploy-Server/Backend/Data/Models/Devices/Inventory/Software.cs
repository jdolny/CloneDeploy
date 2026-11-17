using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class Software
    {
        public int Id { get; set; }

        public string? Name { get; set; }

        public string? Version { get; set; }

        public int Major { get; set; }

        public int Minor { get; set; }

        public int Build { get; set; }

        public int Revision { get; set; }

    }

    public sealed class SoftwareConfiguration : IEntityTypeConfiguration<Software>
    {
        public void Configure(EntityTypeBuilder<Software> builder)
        {
            builder.HasIndex(x => new { x.Name, x.Version }).IsUnique().AreNullsDistinct(false);
        }
    }
}
