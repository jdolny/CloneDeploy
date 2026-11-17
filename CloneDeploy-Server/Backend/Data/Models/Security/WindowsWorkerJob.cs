using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class WindowsWorkerJob
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public string Kind { get; set; } = "";
    public string State { get; set; } = "Queued";
    public string RequestEncrypted { get; set; } = "";
    public string InputsJson { get; set; } = "[]";
    public string OutputsJson { get; set; } = "[]";
    public string? Result { get; set; }
    public string? Error { get; set; }
    public Guid Lease { get; set; }
    public DateTime LeaseUntilUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? Progress { get; set; }
}
public sealed class WindowsWorkerJobConfiguration : IEntityTypeConfiguration<WindowsWorkerJob>
{
    public void Configure(EntityTypeBuilder<WindowsWorkerJob> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.SiteId, x.State });
    }
}
