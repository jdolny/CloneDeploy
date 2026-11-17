using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public class ScheduledTaskExecution
{
    public long Id { get; set; }
    public ScheduledTaskType TaskId { get; set; }
    
    // One-off requests from a directory/group page can target a single item, instead of syncing all
    public int? TargetId { get; set; }
    public string? TargetName { get; set; }
    
    // Null means a manual run. Scheduled occurrences have a unique task/time pair.
    public DateTime? ScheduledForUtc { get; set; }
    public DateTime QueuedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public ScheduledTaskStatus Status { get; set; }
    public string? Summary { get; set; }
    
    //nav properties
    public ScheduledTask ScheduledTask { get; set; } = null!;
}

public class ScheduledTaskHistoryConfiguration : IEntityTypeConfiguration<ScheduledTaskExecution>
{
    public void Configure(EntityTypeBuilder<ScheduledTaskExecution> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.ScheduledTask).WithMany(x => x.History).HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Summary).HasMaxLength(2000);
        builder.Property(x => x.TargetName).HasMaxLength(300);
        builder.HasIndex(x => new { x.TaskId, x.ScheduledForUtc }).IsUnique();
        builder.HasIndex(x => new { x.Status, x.QueuedAtUtc, x.Id });
    }
}
