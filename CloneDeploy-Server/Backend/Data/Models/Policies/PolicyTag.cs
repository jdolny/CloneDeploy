using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class PolicyTag
    {
        public int Id { get; set; }
        
        public int PolicyId { get; set; }
        
        public int TagId { get; set; }
        
        //navigation properties
        public Policy? Policy { get; set; }
        public Tag? Tag { get; set; }
    }

    public sealed class PolicyTagConfiguration : IEntityTypeConfiguration<PolicyTag>
    {
        public void Configure(EntityTypeBuilder<PolicyTag> builder)
        {
            builder.HasOne(x => x.Policy).WithMany(x => x.PolicyTags).HasForeignKey(x => x.PolicyId);
        }
    }
}
