using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class Certificate
    {
        public int Id { get; set; }
        
        public required string Store { get; set; }
        
        public required string Subject { get; set; }
        
        public string? FriendlyName { get; set; }
        
        public required string Thumbprint { get; set; }
        
        public required string Serial { get; set; }
        
        public required string Issuer { get; set; }
        
        public DateTime NotBeforeUtc { get; set; }
        
        public DateTime NotAfterUtc { get; set; }
    }

    public sealed class CertificateConfiguration : IEntityTypeConfiguration<Certificate>
    {
        public void Configure(EntityTypeBuilder<Certificate> builder)
        {
            builder.HasIndex(x => new { x.Thumbprint, x.Store }).IsUnique();
        }
    }
}
