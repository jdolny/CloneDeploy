using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class Policy
    {
        public int Id { get; set; }
        
        public Guid Guid { get; set; }
        
        public string? Hash { get; set; }
        
        public required string Name { get; set; }
        
        public string? Description { get; set; }

        public int ExecutionPriority { get; set; } = 100;

        // Incremented only by an explicit Re-Run request.
        public long ExecutionRevision { get; set; }
        
        public PolicyTrigger Trigger { get; set; }

        public bool SelfServiceRunAsUser { get; set; }
        
        public PolicyFrequency Frequency { get; set; }
        
        public int SubFrequency { get; set; }
        
        public DateTime StartDateUtc { get; set; }
        
        public PolicyCompletedAction CompletedAction { get; set; }
        
        public bool RunInventory { get; set; }
        
        public bool RunLoginTracker { get; set; }
        
        public bool RemoveInstallCache { get; set; } = true;
        
        public PolicyExecutionType ExecutionType { get; set; }
        
        public PolicyErrorAction ErrorAction { get; set; }
        
        public PolicyLogLevel LogLevel { get; set; }
        
        public PolicyFrequencyMissedAction MissedAction { get; set; }
        
        public PolicyAutoArchiveType AutoArchiveType { get; set; }
        
        public string? AutoArchiveSub { get; set; }
        
        public bool Archived { get; set; }
        
        public bool SkipServerResult { get; set; }

        public bool StartWindowEnabled { get; set; }
        public PolicyWindowDays StartWindowDays { get; set; } = PolicyWindowDays.EveryDay;
        public TimeSpan? StartWindowFrom { get; set; }
        public TimeSpan? StartWindowUntil { get; set; }



        public DateTime? ArchivedAtUtc { get; set; }
        
        public int? ConditionId { get; set; }
        
        public PolicyConditionFailedAction ConditionFailedAction { get; set; }
        
        public PolicyRemoteAccess RemoteAccess { get; set; }

        //navigation properties
        public ScriptAction? Condition { get; set; }
        public ICollection<PolicyAction> PolicyActions { get; set; } = [];
        public ICollection<PolicyTag> PolicyTags { get; set; } = [];
        public ICollection<GroupPolicy> GroupPolicies { get; set; } = [];
        public ICollection<DirectoryOuPolicy> DirectoryOuPolicies { get; set; } = [];
    }

    public sealed class PolicyConfiguration : IEntityTypeConfiguration<Policy>
    {
        public void Configure(EntityTypeBuilder<Policy> builder)
        {
            builder.Property(x => x.ExecutionPriority).HasDefaultValue(100);
        }
    }
}
