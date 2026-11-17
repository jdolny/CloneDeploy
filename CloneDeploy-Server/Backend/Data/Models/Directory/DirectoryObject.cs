using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>
/// A synced directory object
/// </summary>
public class DirectoryObject
{
    public int DirectorySourceId { get; set; }
    public DirectorySource DirectorySource { get; set; } = null!;
    public Guid ObjectGuid { get; set; }
    public Guid? ParentGuid { get; set; }
    public DirectoryObjectKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string DistinguishedName { get; set; } = "";
    public string? AccountName { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? Email { get; set; }
    public string? DnsHostName { get; set; }
    public string? ObjectSid { get; set; }
    public bool Disabled { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public bool IsMissing { get; set; }
    public bool IsScopeAncestor { get; set; }
    
}

public class DirectoryObjectConfiguration : IEntityTypeConfiguration<DirectoryObject>
{
    public void Configure(EntityTypeBuilder<DirectoryObject> builder)
    {
        builder.HasOne(x => x.DirectorySource).WithMany()
            .HasForeignKey(x => x.DirectorySourceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasKey(x => new { x.DirectorySourceId, x.ObjectGuid });

        builder.HasIndex(x => new { x.DirectorySourceId, x.ParentGuid });
        builder.HasIndex(x => new { x.DirectorySourceId, x.Kind, x.IsMissing });
        // Parent GUID deliberately isn't an FK: missing objects and moves are retained.
    }
}




