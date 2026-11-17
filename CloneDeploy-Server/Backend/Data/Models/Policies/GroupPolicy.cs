using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class GroupPolicy
    {
        public int Id { get; set; }
        
        public int DeviceGroupId { get; set; }
        
        public int PolicyId { get; set; }
        
        public int PolicyOrder { get; set; }
        
        //navigation properties
        public DeviceGroup? DeviceGroup { get; set; }
        public Policy? Policy { get; set; }
    }

    public sealed class GroupPolicyConfiguration : IEntityTypeConfiguration<GroupPolicy>
    {
        public void Configure(EntityTypeBuilder<GroupPolicy> builder)
        {
            builder.HasOne(x => x.Policy).WithMany(x => x.GroupPolicies).HasForeignKey(x => x.PolicyId);
        }
    }
}
