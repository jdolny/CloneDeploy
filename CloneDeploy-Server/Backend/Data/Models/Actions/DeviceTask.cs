using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum DeviceTaskTiming { Now, NextCheckin }
public enum DeviceTaskSource { Administrator, SelfService }
public enum DeviceTaskStatus { Queued, Offered, Running, Succeeded, Failed, Cancelled, Unknown }

// A deployment freezes one action payload and the target selection for its device tasks.
public sealed class ActionDeployment
{
    public Guid Id { get; set; }
    public Guid ActionGuid { get; set; }
    public required string ActionName { get; set; }
    public required string ActionJson { get; set; }
    public required string ContentHash { get; set; }
    public required string TargetsJson { get; set; }
    public DeviceTaskTiming Timing { get; set; }
    public DeviceTaskSource Source { get; set; }
    public int RequestedByUserId { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public ICollection<DeviceTask> Tasks { get; set; } = [];
}

public sealed class DeviceTask
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    // Retain identity and names for history, even if the original device/action is deleted.
    public int DeviceId { get; set; }
    public Guid DeviceGuid { get; set; }
    public required string DeviceName { get; set; }
    public DeviceTaskStatus Status { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? ManagedWinGetActionsJson { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public DateTime? LastNotifiedAtUtc { get; set; }
    public string? Result { get; set; }
    public ActionDeployment Deployment { get; set; } = null!;
}

public sealed class DeviceTaskConfiguration : IEntityTypeConfiguration<DeviceTask>
{
    public void Configure(EntityTypeBuilder<DeviceTask> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.Deployment).WithMany(x => x.Tasks).HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.DeploymentId, x.DeviceId }).IsUnique();
        builder.HasIndex(x => new { x.Status, x.LeaseExpiresAtUtc });
        builder.HasIndex(x => new { x.DeviceGuid, x.Status });
        builder.HasIndex(x => new { x.DeviceId, x.Status });
        builder.Property(x => x.Result).HasMaxLength(4000);
    }
}
public sealed class ActionDeploymentConfiguration : IEntityTypeConfiguration<ActionDeployment>
{
    public void Configure(EntityTypeBuilder<ActionDeployment> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.CreatedAtUtc);
        builder.HasIndex(x => x.ActionGuid);
    }
}

// Short-lived admission slots for the future authenticated check-in handler.
public sealed class DeviceCheckinLease
{
    public Guid Id { get; set; }
    public Guid DeviceGuid { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
public sealed class DeviceCheckinLeaseConfiguration : IEntityTypeConfiguration<DeviceCheckinLease>
{
    public void Configure(EntityTypeBuilder<DeviceCheckinLease> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.DeviceGuid).IsUnique();
        builder.HasIndex(x => x.ExpiresAtUtc);
    }
}
