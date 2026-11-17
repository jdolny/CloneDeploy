using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>
/// Relationship of directory object member to directory group including inherited groups
/// Basically shows every group that a directory object is a member of, both direct and inherited
/// Member could be a group or computer or user
/// Calculated after each directory sync
/// </summary>
public class DirectoryEffectiveGroupMembership
{
    public int DirectorySourceId { get; set; }
    public DirectorySource DirectorySource { get; set; } = null!;
    public Guid GroupGuid { get; set; }
    public Guid MemberGuid { get; set; }
}

public class DirectoryEffectiveGroupMembershipConfiguration : IEntityTypeConfiguration<DirectoryEffectiveGroupMembership>
{
    public void Configure(EntityTypeBuilder<DirectoryEffectiveGroupMembership> builder)
    {
        builder.HasOne(x => x.DirectorySource).WithMany()
            .HasForeignKey(x => x.DirectorySourceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasKey(x => new { x.DirectorySourceId, x.GroupGuid, x.MemberGuid });

        builder.HasIndex(x => new { x.DirectorySourceId, x.MemberGuid });
        builder.HasOne<DirectoryObject>().WithMany()
            .HasForeignKey(x => new { x.DirectorySourceId, x.GroupGuid }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DirectoryObject>().WithMany()
            .HasForeignKey(x => new { x.DirectorySourceId, x.MemberGuid }).OnDelete(DeleteBehavior.Cascade);
    }
}
