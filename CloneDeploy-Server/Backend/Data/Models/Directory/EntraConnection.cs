using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

// One tenant per installation. SSO does not depend on a DirectorySource.
public sealed class EntraConnection
{
    public int Id { get; set; } = 1;
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public bool SsoEnabled { get; set; }
    public string? CertificateEncrypted { get; set; }
    public string? PendingCertificateEncrypted { get; set; }
}

public sealed class EntraConnectionConfiguration : IEntityTypeConfiguration<EntraConnection>
{
    public void Configure(EntityTypeBuilder<EntraConnection> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.ToTable(t => t.HasCheckConstraint("CK_EntraConnection_Singleton", "\"Id\" = 1"));
    }
}
