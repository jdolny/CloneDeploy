using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class AgentPolicyRun
{
    public Guid Id { get; set; }
    public int DeviceId { get; set; }
    public int PolicyId { get; set; }
    public required string Hash { get; set; }
    public required string SnapshotJson { get; set; }
    public string? UserName { get; set; }
    public string? UserSid { get; set; }
    public bool SelfService { get; set; }
    public string? ManagedWinGetActionsJson { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int? Result { get; set; }
    public string? Detail { get; set; }
}
public sealed class AgentPolicyRunConfiguration : IEntityTypeConfiguration<AgentPolicyRun>
{
    public void Configure(EntityTypeBuilder<AgentPolicyRun> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Policy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.DeviceId, x.PolicyId, x.StartedAtUtc });
        builder.Property(x => x.Detail).HasMaxLength(16000);
    }
}

public sealed class AgentDownloadLease
{
    public Guid Id { get; set; }
    public int DeviceId { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
public sealed class AgentDownloadLeaseConfiguration : IEntityTypeConfiguration<AgentDownloadLease>
{
    public void Configure(EntityTypeBuilder<AgentDownloadLease> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.ExpiresAtUtc);
        builder.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}
