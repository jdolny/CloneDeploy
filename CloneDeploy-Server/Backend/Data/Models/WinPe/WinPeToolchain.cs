using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class WinPeToolchain
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string AdkUrl { get; set; } = "";
    public string AdkSha256 { get; set; } = "";
    public string WinPeUrl { get; set; } = "";
    public string WinPeSha256 { get; set; } = "";
    public static WinPeToolchain From(ToolchainSpecDto spec) => new() { Name = spec.Name, Version = spec.Version, AdkUrl = spec.AdkUrl, AdkSha256 = spec.AdkSha256, WinPeUrl = spec.WinPeUrl, WinPeSha256 = spec.WinPeSha256 };
}
public sealed class WinPeSetupJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int ToolchainId { get; set; }
    public string RequestJson { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
public sealed class WinPeToolchainConfiguration : IEntityTypeConfiguration<WinPeToolchain>
{
    public void Configure(EntityTypeBuilder<WinPeToolchain> b)
    {
        b.Property(x => x.Name).HasColumnType("citext").HasMaxLength(200);
        b.HasIndex(x => x.Name).IsUnique();
    }
}
public sealed class WinPeSetupJobConfiguration : IEntityTypeConfiguration<WinPeSetupJob>
{
    public void Configure(EntityTypeBuilder<WinPeSetupJob> b) => b.HasOne<WinPeToolchain>().WithMany().HasForeignKey(x => x.ToolchainId).OnDelete(DeleteBehavior.Restrict);
}
