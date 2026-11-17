using CloneDeploy_Agent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

// Reusable deployment settings. The image is chosen by the device or deployment request.
public class DeploymentProfile
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public bool VisibleInWinPe { get; set; } = true;
    public int? OuNamingRuleSetId { get; set; }
    public DeploymentOuPrompt OuPrompt { get; set; }
    public bool SetComputerName { get; set; }
    public DeploymentCompletionAction AfterDeployment { get; set; } = DeploymentCompletionAction.Restart;
    public DeploymentDriverMode DriverMode { get; set; } = DeploymentDriverMode.Automatic;
    public int? DriverPackId { get; set; }
    public DriverPack? DriverPack { get; set; }
}

public sealed class ImageProfileConfiguration : IEntityTypeConfiguration<DeploymentProfile>
{
    public void Configure(EntityTypeBuilder<DeploymentProfile> builder)
    {
        builder.ToTable("ImageProfiles");
        builder.Property(x => x.AfterDeployment).HasDefaultValue(DeploymentCompletionAction.Restart).HasSentinel(DeploymentCompletionAction.Restart);
        // Keep fresh-install defaults in the model so a rebuilt initial migration
        // includes them. Reserve 1 for the seed without colliding with new profiles.
        builder.Property(x => x.Id).HasIdentityOptions(startValue: 2);
        builder.HasData(new DeploymentProfile
        {
            Id = 1,
            Name = "Default",
            Enabled = true,
            VisibleInWinPe = true,
            SetComputerName = true,
            DriverMode = DeploymentDriverMode.Automatic
        });
        builder.Property(x => x.Name).HasColumnType("citext");
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasOne<OuNamingRuleSet>().WithMany().HasForeignKey(x => x.OuNamingRuleSetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.DriverPack).WithMany().HasForeignKey(x => x.DriverPackId).OnDelete(DeleteBehavior.Restrict);
    }
}

public enum DeploymentDriverMode { Automatic, Specific, None }

public enum DeploymentOuPrompt { Never, IfNoMatch, Always }
