using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum SiteRegistrationState { Created, Pending, Approved, Revoked }

public sealed class SiteServiceRegistration
{
    public Guid Id { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    public SiteRegistrationState State { get; set; }
    public bool IsDeleted { get; set; }
    public int Revision { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime TokenExpiresAtUtc { get; set; }
    [MaxLength(64)] public string? TokenHash { get; set; }
    [MaxLength(64)] public string BackendAddress { get; set; } = "";
    [MaxLength(2048)] public string BackendUrl { get; set; } = "";
    [MaxLength(2048)] public string PublicUrl { get; set; } = "";
    [MaxLength(64)] public string ListenAddress { get; set; } = "";
    public int WindowsWorkerRoles { get; set; }
    public bool IsWindowsWorker { get; set; }
    public bool ProxyEnabled { get; set; }
    public string BootSettingsJson { get; set; } = "{}";
    public string? BootStatusJson { get; set; }
    [MaxLength(8192)] public string? IdentityCsr { get; set; }
    [MaxLength(8192)] public string? HttpsCsr { get; set; }
    [MaxLength(64)] public string? KeyFingerprint { get; set; }
    [MaxLength(255)] public string? Hostname { get; set; }
    [MaxLength(20)] public string? HostOs { get; set; }
    [MaxLength(100)] public string? Version { get; set; }
    [MaxLength(2048)] public string? Addresses { get; set; }
    public int IssuerId { get; set; }
    public AppCertificate Issuer { get; set; } = null!;
    public byte[]? IdentityCertificate { get; set; }
    public byte[]? HttpsCertificate { get; set; }
    [MaxLength(64)] public string? CertificateSha256 { get; set; }
    [MaxLength(64)] public string? PreviousCertificateSha256 { get; set; }
    public DateTime? PreviousCertificateUntilUtc { get; set; }
    public DateTime? CertificateExpiresAtUtc { get; set; }
    public DateTime? LastContactAtUtc { get; set; }
    public int AppliedRevision { get; set; }
    public bool ProxyRunning { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
}

public sealed class SiteServiceRegistrationConfiguration : IEntityTypeConfiguration<SiteServiceRegistration>
{
    public void Configure(EntityTypeBuilder<SiteServiceRegistration> builder)
    {
        builder.HasQueryFilter(x => !x.IsDeleted);
        builder.HasIndex(x => x.CertificateSha256).IsUnique();
        builder.HasIndex(x => x.PreviousCertificateSha256);
        builder.HasIndex(x => x.IsWindowsWorker).IsUnique().HasFilter("\"IsWindowsWorker\" = TRUE AND \"State\" <> 3");
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasOne(x => x.Issuer).WithMany().HasForeignKey(x => x.IssuerId).OnDelete(DeleteBehavior.Restrict);
    }
}
