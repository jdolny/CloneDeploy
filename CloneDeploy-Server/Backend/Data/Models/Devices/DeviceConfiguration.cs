using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.Property(x => x.AgentPlatform).HasMaxLength(16).HasDefaultValue(CloneDeploy_Agent.Contracts.AgentPlatform.Windows);
        builder.HasOne(x => x.DeploymentProfile).WithMany().HasForeignKey(x => x.DeploymentProfileId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable("Devices", table => table.HasCheckConstraint("CK_Devices_DirectoryLink",
            "(\"DirectorySourceId\" IS NULL) = (\"DirectoryObjectGuid\" IS NULL)"));
        builder.HasOne(x => x.DirectoryObject).WithOne()
            .HasForeignKey<Device>(x => new { x.DirectorySourceId, x.DirectoryObjectGuid })
            .OnDelete(DeleteBehavior.SetNull);
    }
}
