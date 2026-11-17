using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum DevicePowerAction { Wake = 1, Shutdown = 2 }

public sealed class DeviceGroupPowerSchedule
{
    public int Id { get; set; }
    public int DeviceGroupId { get; set; }
    public int ScheduleId { get; set; }
    public DevicePowerAction Action { get; set; }
    public bool Enabled { get; set; } = true;
    public int ConfiguredByUserId { get; set; }
    public DateTime LastEvaluatedAtUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public string? LastResult { get; set; }
    public Schedule Schedule { get; set; } = null!;
}

public sealed class DeviceGroupPowerScheduleConfiguration : IEntityTypeConfiguration<DeviceGroupPowerSchedule>
{
    public void Configure(EntityTypeBuilder<DeviceGroupPowerSchedule> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.DeviceGroupId, x.ScheduleId, x.Action }).IsUnique();
        b.HasOne<DeviceGroup>().WithMany().HasForeignKey(x => x.DeviceGroupId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Schedule).WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ConfiguredByUserId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.LastResult).HasMaxLength(1024);
    }
}

