using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>
/// Direct only membership of a directory group and it's member, does not include inheritance
/// Member could be group, user, or computer
/// </summary>
public class DirectoryGroupMembership
{
    public long Id { get; set; }
    public int DirectorySourceId { get; set; }
    public DirectorySource DirectorySource { get; set; } = null!;
    public Guid GroupGuid { get; set; }
    public Guid? MemberGuid { get; set; }
    public string MemberDistinguishedName { get; set; } = "";
}

public class DirectoryGroupMembershipConfiguration : IEntityTypeConfiguration<DirectoryGroupMembership>
{
    public void Configure(EntityTypeBuilder<DirectoryGroupMembership> builder)
    {
        builder.HasOne(x => x.DirectorySource).WithMany()
            .HasForeignKey(x => x.DirectorySourceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.DirectorySourceId, x.GroupGuid, x.MemberGuid });
        builder.HasIndex(x => new { x.DirectorySourceId, x.MemberGuid });
        builder.HasOne<DirectoryObject>().WithMany()
            .HasForeignKey(x => new { x.DirectorySourceId, x.GroupGuid }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DirectoryObject>().WithMany()
            .HasForeignKey(x => new { x.DirectorySourceId, x.MemberGuid }).OnDelete(DeleteBehavior.Cascade);
    }
}
