using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>
/// The relationship of a directory object of type OU and a policy
/// </summary>
public class DirectoryOuPolicy
{
    public int DirectorySourceId { get; set; }
    public DirectorySource DirectorySource { get; set; } = null!;
    public Guid OuGuid { get; set; }
    public int PolicyId { get; set; }
    public Policy Policy { get; set; } = null!;
    public int Order { get; set; }
    public bool IncludeDescendants { get; set; } = true;
}

public class DirectoryOuPolicyConfiguration : IEntityTypeConfiguration<DirectoryOuPolicy>
{
    public void Configure(EntityTypeBuilder<DirectoryOuPolicy> builder)
    {
        builder.HasOne(x => x.DirectorySource).WithMany()
            .HasForeignKey(x => x.DirectorySourceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasKey(x => new { x.DirectorySourceId, x.OuGuid, x.PolicyId });

        builder.HasOne<DirectoryObject>().WithMany()
            .HasForeignKey(x => new { x.DirectorySourceId, x.OuGuid }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Policy).WithMany(x => x.DirectoryOuPolicies).HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
    }
}
