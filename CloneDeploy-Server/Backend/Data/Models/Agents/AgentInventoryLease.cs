using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class AgentInventoryLease
{
    public Guid Id { get; set; }
    public int DeviceId { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class AgentInventoryLeaseConfiguration : IEntityTypeConfiguration<AgentInventoryLease>
{
    public void Configure(EntityTypeBuilder<AgentInventoryLease> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.DeviceId).IsUnique();
        builder.HasIndex(x => x.ExpiresAtUtc);
    }
}
