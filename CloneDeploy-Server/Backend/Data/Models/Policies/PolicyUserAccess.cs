using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>Explicit portal availability, independent of automatic device assignments.</summary>
public sealed class PolicyUserAccess
{
    public int PolicyId { get; set; }
    // Login audience, or recipient audience for a notification-only non-login policy.
    public int? LoginAudienceId { get; set; }
    public int? SelfServiceAudienceId { get; set; }
    public bool SelfServiceEnabled { get; set; }
    public bool AnyInteractiveUser { get; set; }
    public bool RestrictSelfServiceToDeviceTargets { get; set; }
    public int? CategoryId { get; set; }
    public SelfServiceCategory? Category { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public byte[]? IconPng { get; set; }
    public string? IconHash { get; set; }
    public Guid Revision { get; set; }
}
public sealed class PolicyUserAccessConfiguration : IEntityTypeConfiguration<PolicyUserAccess>
{
    public void Configure(EntityTypeBuilder<PolicyUserAccess> builder)
    {
        builder.HasKey(x => x.PolicyId);
        builder.HasOne<UserAudience>().WithMany().HasForeignKey(x => x.LoginAudienceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAudience>().WithMany().HasForeignKey(x => x.SelfServiceAudienceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Policy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.Property(x => x.DisplayName).HasMaxLength(120);
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.Icon).HasMaxLength(24);
        builder.Property(x => x.IconHash).HasMaxLength(64);
        builder.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
    }
}
