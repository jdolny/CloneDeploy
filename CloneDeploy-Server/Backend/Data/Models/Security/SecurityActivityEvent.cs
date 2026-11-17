using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

// Deliberately no payload, credentials, tokens, or foreign key to an account that may be deleted.
public sealed class SecurityActivityEvent
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Kind { get; set; } = "";
    public string Category { get; set; } = "";
    public string Account { get; set; } = "";
    public string SourceIp { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class SecurityActivityEventConfiguration : IEntityTypeConfiguration<SecurityActivityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityActivityEvent> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.OccurredAtUtc);
        b.HasIndex(x => new { x.Account, x.SourceIp, x.OccurredAtUtc });
        b.Property(x => x.Kind).HasMaxLength(32);
        b.Property(x => x.Category).HasMaxLength(100);
        b.Property(x => x.Account).HasMaxLength(256);
        b.Property(x => x.SourceIp).HasMaxLength(45);
        b.Property(x => x.Target).HasMaxLength(256);
    }
}
