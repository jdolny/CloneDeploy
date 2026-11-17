using CloneDeploy_Server.Backend.Enum;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    /// <summary>
    /// An ActiveImagingTask represents an imaging task that is currently in progress or queued for execution.
    /// It contains information about the task's status, device, user, and other relevant details.
    /// Associated with the Device and User entities through their respective IDs.
    /// </summary>
    public class ActiveImagingTask
    {
        public int Id { get; set; }
        public Guid? UploadId { get; set; }
        public Guid? DeploymentId { get; set; }
        public string? ImageName { get; set; }
        public int Phase { get; set; }
        public double? ProgressPercent { get; set; }
        public long BytesTransferred { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public string? StatusMessage { get; set; }
        public string? ErrorMessage { get; set; }

        public int DeviceId { get; set; }

        public int QueuePosition { get; set; }
        
        public string? Rate { get; set; }

        public ImagingStatus Status { get; set; }
        
        public string? Type { get; set; }
        
        public int? AppUserId { get; set; }
        
        public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;

        //navigation properties
        public Device? Device { get; set; }
        public AppUser? AppUser { get; set; }
    }
    public sealed class ActiveImagingTaskConfiguration : IEntityTypeConfiguration<ActiveImagingTask>
    {
        public void Configure(EntityTypeBuilder<ActiveImagingTask> builder)
        {
            builder.HasIndex(x => x.UploadId).IsUnique();
            builder.HasIndex(x => x.DeploymentId).IsUnique();
            builder.HasOne<ImagingDeployment>().WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

