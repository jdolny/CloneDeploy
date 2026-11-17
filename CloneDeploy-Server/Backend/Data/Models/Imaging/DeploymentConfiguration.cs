using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class DeploymentDriverRule
{
    public int Id { get; set; }
    public int DeploymentProfileId { get; set; }
    public int DriverPackId { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? ManufacturerRegex { get; set; }
    public string? ModelRegex { get; set; }
    public DriverPack? DriverPack { get; set; }
}

public sealed class DeploymentPolicy
{
    public int DeploymentProfileId { get; set; }
    public int PolicyId { get; set; }
    public int Position { get; set; }
    public Policy? Policy { get; set; }
}

public sealed class DeploymentDriverRuleConfiguration : IEntityTypeConfiguration<DeploymentDriverRule>
{
    public void Configure(EntityTypeBuilder<DeploymentDriverRule> b)
    {
        b.HasOne<DeploymentProfile>().WithMany().HasForeignKey(x => x.DeploymentProfileId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.DriverPack).WithMany().HasForeignKey(x => x.DriverPackId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Manufacturer).HasMaxLength(500); b.Property(x => x.Model).HasMaxLength(500);
        b.Property(x => x.ManufacturerRegex).HasMaxLength(500); b.Property(x => x.ModelRegex).HasMaxLength(500);
    }
}

public sealed class DeploymentPolicyConfiguration : IEntityTypeConfiguration<DeploymentPolicy>
{
    public void Configure(EntityTypeBuilder<DeploymentPolicy> b)
    {
        b.HasKey(x => new { x.DeploymentProfileId, x.PolicyId });
        b.HasIndex(x => new { x.DeploymentProfileId, x.Position }).IsUnique();
        b.HasOne<DeploymentProfile>().WithMany().HasForeignKey(x => x.DeploymentProfileId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
    }
}