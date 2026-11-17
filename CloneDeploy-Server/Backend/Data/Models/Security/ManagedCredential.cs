using CloneDeploy_Server.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class ManagedCredential
    {
        public int Id { get; set; }

        public string Name { get; set; } = null!;

        public string Username { get; set; } = null!;

        public string PasswordEncrypted { get; set; } = null!;

        public Guid Guid { get; set; } = Guid.NewGuid();

        //navigation properties
        public ICollection<StorageProvider> StorageProviders { get; set; } = [];
    }
    public class ManagedCredentialConfiguration : IEntityTypeConfiguration<ManagedCredential>
    {
        public void Configure(EntityTypeBuilder<ManagedCredential> builder)
        {
            builder.Property(t => t.Name)
                .HasColumnType("citext");

            builder.HasIndex(t => t.Name)
                .IsUnique();
        }
    }
}
