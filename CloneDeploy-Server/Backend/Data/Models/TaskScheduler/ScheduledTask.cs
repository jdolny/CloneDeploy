using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum ScheduledTaskType { DirectorySync = 1, DynamicGroupRecalculation = 2, ExternalFileRefresh = 3, WinGetManifestImport = 4, WindowsUpdateAutoApproval = 5, EmailDigests = 6, GroupPowerSchedules = 7 }
public enum ScheduledTaskStatus { Queued, Running, Succeeded, Failed, Interrupted, Canceled }

// The application defines the available tasks. Administrators only configure when they run.
public class ScheduledTask
{
    public ScheduledTaskType Id { get; set; }
    public bool IsEnabled { get; set; }
    public int? ScheduleId { get; set; }
    public DateTime? LastEvaluatedAtUtc { get; set; }
    
    //nav properties
    public Schedule? Schedule { get; set; }
    public ICollection<ScheduledTaskExecution> History { get; set; } = [];
}



public class ScheduledTaskConfiguration : IEntityTypeConfiguration<ScheduledTask>
{
    public void Configure(EntityTypeBuilder<ScheduledTask> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasOne(x => x.Schedule).WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasData(new ScheduledTask { Id = ScheduledTaskType.DirectorySync },
            new ScheduledTask { Id = ScheduledTaskType.DynamicGroupRecalculation });
    }
}


