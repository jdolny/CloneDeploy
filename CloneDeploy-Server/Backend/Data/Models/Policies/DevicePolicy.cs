using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class DevicePolicy
{
    public int DeviceId { get; set; }
    public int PolicyId { get; set; }
    public Device Device { get; set; } = null!;
    public Policy Policy { get; set; } = null!;
}

public sealed class DevicePolicyConfiguration : IEntityTypeConfiguration<DevicePolicy>
{
    public void Configure(EntityTypeBuilder<DevicePolicy> builder)
    {
        builder.HasKey(x => new { x.DeviceId, x.PolicyId });
        builder.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
    }
}
