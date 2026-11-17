using CloneDeploy_Agent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class HelpTicket
{
    public int Id { get; set; }
    public Guid CommandId { get; set; }
    public string[] RequesterKeys { get; set; } = [];
    public string RequesterName { get; set; } = "";
    public string Subject { get; set; } = "";
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public int? AssignedToId { get; set; }
    public int? CategoryId { get; set; }
    public int? QueueId { get; set; }
    public HelpQueue? Queue { get; set; }
    public string? RoutingSummary { get; set; }
    public HelpTicketStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public long LastStaffMessageId { get; set; }
    public long ReadThroughMessageId { get; set; }
}
public sealed class HelpTicketMessage
{
    public long Id { get; set; }
    public int TicketId { get; set; }
    public Guid CommandId { get; set; }
    public string Author { get; set; } = "";
    public int? StaffUserId { get; set; }
    public bool FromStaff { get; set; }
    public string Body { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public byte[]? Screenshot { get; set; }
}
public sealed class HelpTicketConfiguration : IEntityTypeConfiguration<HelpTicket>
{
    public void Configure(EntityTypeBuilder<HelpTicket> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.CommandId).IsUnique();
        b.HasIndex(x => x.UpdatedAtUtc);
        b.HasIndex(x => x.RequesterKeys).HasMethod("gin");
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.RequesterName).HasMaxLength(256);
        b.Property(x => x.DeviceName).HasMaxLength(256);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AssignedToId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<HelpCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Queue).WithMany().HasForeignKey(x => x.QueueId).OnDelete(DeleteBehavior.Restrict);
        // Keep ticket context and history when an originating device or directory object is deleted.
    }
}
public sealed class HelpTicketMessageConfiguration : IEntityTypeConfiguration<HelpTicketMessage>
{
    public void Configure(EntityTypeBuilder<HelpTicketMessage> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.CommandId).IsUnique();
        b.HasIndex(x => new { x.TicketId, x.Id });
        b.HasOne<HelpTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Body).HasMaxLength(4000);
        b.Property(x => x.Author).HasMaxLength(256);
    }
}
