using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class AgentCertificate
{
    public Guid Id { get; set; }
    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public int IssuerId { get; set; }
    public AppCertificate Issuer { get; set; } = null!;
    public required byte[] CertificateDer { get; set; }
    public required string Sha256Fingerprint { get; set; }
    public DateTime NotBeforeUtc { get; set; }
    public DateTime NotAfterUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public Guid? RenewedFromId { get; set; }
    public DateTime? RenewalConfirmedAtUtc { get; set; }
    public DateTime? RetireAfterUtc { get; set; }
}

public sealed class AgentCertificateConfiguration : IEntityTypeConfiguration<AgentCertificate>
{
    public void Configure(EntityTypeBuilder<AgentCertificate> builder)
    {
        builder.Property(x => x.Sha256Fingerprint).HasMaxLength(64);
        builder.HasIndex(x => x.Sha256Fingerprint).IsUnique();
        builder.HasIndex(x => x.RenewedFromId).IsUnique();
        builder.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Issuer).WithMany().HasForeignKey(x => x.IssuerId).OnDelete(DeleteBehavior.Restrict);
    }
}
