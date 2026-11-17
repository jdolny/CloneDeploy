using CloneDeploy_Agent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class HelpTicketStatusEvent
{
    public long Id { get; set; }
    public int TicketId { get; set; }
    public HelpTicketStatus Status { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

public sealed class HelpTicketStatusEventConfiguration : IEntityTypeConfiguration<HelpTicketStatusEvent>
{
    public void Configure(EntityTypeBuilder<HelpTicketStatusEvent> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.TicketId, x.OccurredAtUtc });
        b.HasIndex(x => new { x.Status, x.OccurredAtUtc });
        b.HasOne<HelpTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}
