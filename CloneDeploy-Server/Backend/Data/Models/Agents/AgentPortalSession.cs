using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class AgentPortalSession
{
    public string TokenHash { get; set; } = "";
    public Guid DeviceGuid { get; set; }
    public string LocalUser { get; set; } = "";
    public int DirectorySourceId { get; set; }
    public Guid ObjectGuid { get; set; }
    public Guid DomainGuid { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
public sealed class AgentPortalSessionConfiguration : IEntityTypeConfiguration<AgentPortalSession>
{
    public void Configure(EntityTypeBuilder<AgentPortalSession> b)
    {
        b.HasKey(x => x.TokenHash);
        b.Property(x => x.TokenHash).HasMaxLength(43);
        b.Property(x => x.LocalUser).HasMaxLength(100);
        b.HasIndex(x => new { x.DeviceGuid, x.LocalUser }).IsUnique();
        b.HasIndex(x => x.ExpiresAtUtc);
    }
}
