using CloneDeploy_Agent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class DeviceActivityReport
{
    public int DeviceId { get; set; }
    public required string ConnectionId { get; set; }
    public long Sequence { get; set; }
    public AgentActivityKind Kind { get; set; }
    public Guid? RunId { get; set; }
    public string? Name { get; set; }
    public DateTime ChangedAtUtc { get; set; }
}

public sealed class DeviceActivityReportConfiguration : IEntityTypeConfiguration<DeviceActivityReport>
{
    public void Configure(EntityTypeBuilder<DeviceActivityReport> builder)
    {
        builder.ToTable("DeviceActivityReports");
        builder.HasKey(x => x.DeviceId);
        builder.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(x => x.ConnectionId).HasMaxLength(128);
        builder.Property(x => x.Name).HasMaxLength(512);
    }
}
