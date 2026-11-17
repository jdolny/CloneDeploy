using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public enum ScheduleType { Weekly, Interval }

    public class Schedule
    {
        public int Id { get; set; }

        public string Name { get; set; } = null!;
        
        public string? Description { get; set; }

        public ScheduleType Type { get; set; }
        public int? IntervalMinutes { get; set; }
        public DateTime? StartsAtUtc { get; set; }
        
        public bool Monday { get; set; }
        
        public bool Tuesday { get; set; }
        
        public bool Wednesday { get; set; }
        
        public bool Thursday { get; set; }
        
        public bool Friday { get; set; }
        
        public bool Saturday { get; set; }
        
        public bool Sunday { get; set; }
        
        public TimeSpan? Time { get; set; }
    }
    
    public class ScheduleConfiguration : IEntityTypeConfiguration<Schedule>
    {
        public void Configure(EntityTypeBuilder<Schedule> builder)
        {
            builder.Property(t => t.Name)
                .HasColumnType("citext");
            
            builder.HasIndex(t => t.Name)
                .IsUnique();
        }
    }
}
