using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class AutomaticDeploymentRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? ManufacturerRegex { get; set; }
    public string? ModelRegex { get; set; }
    public int? ImageId { get; set; }
    public int? DeploymentProfileId { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && ImageId.HasValue && DeploymentProfileId.HasValue &&
        (!string.IsNullOrWhiteSpace(Manufacturer) && !string.IsNullOrWhiteSpace(Model) ||
         !string.IsNullOrWhiteSpace(ManufacturerRegex) || !string.IsNullOrWhiteSpace(ModelRegex));
    public Image? Image { get; set; }
    public DeploymentProfile? DeploymentProfile { get; set; }
}

public sealed class AutomaticDeploymentRuleConfiguration : IEntityTypeConfiguration<AutomaticDeploymentRule>
{
    public void Configure(EntityTypeBuilder<AutomaticDeploymentRule> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.Property(x => x.Manufacturer).HasMaxLength(500);
        builder.Property(x => x.Model).HasMaxLength(500);
        builder.Property(x => x.ManufacturerRegex).HasMaxLength(500);
        builder.Property(x => x.ModelRegex).HasMaxLength(500);
        builder.HasOne(x => x.Image).WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.DeploymentProfile).WithMany().HasForeignKey(x => x.DeploymentProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
