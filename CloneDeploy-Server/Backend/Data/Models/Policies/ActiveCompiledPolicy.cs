using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    /// <summary>
    /// Associates one compiled policy with one policy. The compiled policy is stored as a JSON string.
    /// </summary>
    public class ActiveCompiledPolicy
    {
        public int Id { get; set; }
        
        public int PolicyId { get; set; }

        public required string Json { get; set; }
        
        //navigation properties
        public Policy? Policy { get; set; }
    }

    public sealed class ActiveCompiledPolicyConfiguration : IEntityTypeConfiguration<ActiveCompiledPolicy>
    {
        public void Configure(EntityTypeBuilder<ActiveCompiledPolicy> builder)
        {
            builder.HasIndex(x => x.PolicyId).IsUnique();
        }
    }
}
