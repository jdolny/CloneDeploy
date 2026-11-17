using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceGroup
    {
        public int Id { get; set; }
        
        public required string Name { get; set; }
        
        public string? Description { get; set; }
        
        public DeviceGroupType Type { get; set; }
        public string? QueryDefinitionJson { get; set; }
        public DateTime? MembershipEvaluatedAtUtc { get; set; }

        public int? ImageId { get; set; }
        public int? DeploymentProfileId { get; set; }

        public bool IsHidden { get; set; }
        
        //navigation properties
        public Image? Image { get; set; }
    }

    public sealed class DeviceGroupConfiguration : IEntityTypeConfiguration<DeviceGroup>
    {
        public void Configure(EntityTypeBuilder<DeviceGroup> builder)
        {
            builder.HasOne<DeploymentProfile>().WithMany().HasForeignKey(x => x.DeploymentProfileId).OnDelete(DeleteBehavior.Restrict);
            builder.HasIndex(x => x.Type).IsUnique().HasFilter("\"Type\" = 2");
            // Policy targets require positive IDs; reserve 1 for the built-in group.
            builder.Property(x => x.Id).HasIdentityOptions(startValue: 2);
            builder.HasData(new DeviceGroup
            {
                Id = 1,
                Name = "All devices",
                Description = "All enrolled, non-archived devices. Membership is automatic.",
                Type = CloneDeploy_Server.Backend.Enum.DeviceGroupType.AllDevices
            });
        }
    }
}
