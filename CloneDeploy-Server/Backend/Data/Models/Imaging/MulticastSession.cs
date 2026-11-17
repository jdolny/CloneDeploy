using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum MulticastState { Preparing, Waiting, Transmitting, Completed, Failed, Cancelled }

public sealed class MulticastSession
{
    public Guid Id { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    public Guid ImageVersionId { get; set; }
    public int DeploymentProfileId { get; set; }
    public int StorageProviderId { get; set; }
    public Guid? SiteServiceId { get; set; }
    public int? GroupId { get; set; }
    public int CreatedBy { get; set; }
    public int ExpectedReceivers { get; set; }
    [MaxLength(45)] public string SenderAddress { get; set; } = "";
    [MaxLength(45)] public string GroupAddress { get; set; } = "";
    public int Port { get; set; }
    public int MegabitsPerSecond { get; set; }
    public int Ttl { get; set; } = 1;
    public MulticastState State { get; set; }
    public bool StartRequested { get; set; }
    public Guid? SenderLease { get; set; }
    public DateTime? SenderLeaseExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    [MaxLength(2000)] public string? Error { get; set; }
}

public sealed class MulticastSessionConfiguration : IEntityTypeConfiguration<MulticastSession>
{
    public void Configure(EntityTypeBuilder<MulticastSession> b)
    {
        // Historical version identity survives explicit deletion of image content.
        b.HasIndex(x => x.ImageVersionId);
        b.HasOne<DeploymentProfile>().WithMany().HasForeignKey(x => x.DeploymentProfileId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.State, x.CreatedAtUtc });
        b.HasIndex(x => x.Port).IsUnique().HasFilter("\"State\" < 3");
    }
}
