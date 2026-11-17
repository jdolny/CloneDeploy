using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class HelpEmailState
{
    public int TicketId { get; set; }
    public DateTime? FirstPendingUtc { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime? LastSentUtc { get; set; }
    public bool Immediate { get; set; }
    public int Updates { get; set; }
    public string Reason { get; set; } = "";
}
public sealed class HelpEmailDelivery
{
    public long Id { get; set; }
    public int TicketId { get; set; }
    public Guid BatchId { get; set; }
    public string Address { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public EmailDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
public sealed class HelpEmailConfiguration : IEntityTypeConfiguration<HelpEmailState>, IEntityTypeConfiguration<HelpEmailDelivery>
{
    public void Configure(EntityTypeBuilder<HelpEmailState> b)
    {
        b.HasKey(x => x.TicketId); b.Property(x => x.TicketId).ValueGeneratedNever();
        b.HasOne<HelpTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.DueAtUtc); b.Property(x => x.Reason).HasMaxLength(80);
    }
    public void Configure(EntityTypeBuilder<HelpEmailDelivery> b)
    {
        b.HasKey(x => x.Id); b.HasOne<HelpTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.BatchId, x.Address }).IsUnique();
        b.HasIndex(x => new { x.Status, x.NextAttemptUtc });
        b.Property(x => x.Address).HasMaxLength(254); b.Property(x => x.Subject).HasMaxLength(300);
    }
}
