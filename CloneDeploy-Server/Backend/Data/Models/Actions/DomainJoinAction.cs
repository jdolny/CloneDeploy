using Microsoft.EntityFrameworkCore;
using CloneDeploy_Agent.Contracts;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>Joins a workgroup computer to a domain using a managed credential and optional target OU.</summary>
public sealed class DomainJoinAction
{
    public int Id { get; set; }
    public Guid Guid { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string DomainName { get; set; } = "";
    public string? OrganizationalUnit { get; set; }
    public DomainJoinOuMode OuMode { get; set; }
    public int? OuNamingRuleSetId { get; set; }
    public int? ManagedCredentialId { get; set; }
    public ActionReference? ActionReference { get; set; }
    public ManagedCredential? ManagedCredential { get; set; }
}

public sealed class DomainJoinActionConfiguration : IEntityTypeConfiguration<DomainJoinAction>
{
    public void Configure(EntityTypeBuilder<DomainJoinAction> builder)
    {
        builder.HasOne(x => x.ActionReference).WithOne().HasPrincipalKey<ActionReference>(x => x.ActionGuid)
            .HasForeignKey<DomainJoinAction>(x => x.Guid).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.ManagedCredential).WithMany().HasForeignKey(x => x.ManagedCredentialId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.DomainName).HasMaxLength(253);
        builder.Property(x => x.OrganizationalUnit).HasMaxLength(2000);
        builder.HasOne<OuNamingRuleSet>().WithMany().HasForeignKey(x => x.OuNamingRuleSetId).OnDelete(DeleteBehavior.Restrict);
    }
}
