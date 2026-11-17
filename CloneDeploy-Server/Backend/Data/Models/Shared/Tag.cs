using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class Tag
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        
        public ICollection<ActionTag> ActionTags { get; set; } = [];
        public ICollection<DeviceGroupTag> DeviceGroupTags { get; set; } = [];
        public ICollection<PolicyTag> PolicyTags { get; set; } = [];
        public ICollection<DeviceTag> DeviceTags { get; set; } = [];
    }
    
    public class TagConfiguration : IEntityTypeConfiguration<Tag>
    {
        public void Configure(EntityTypeBuilder<Tag> builder)
        {
            builder.Property(t => t.Name)
                .HasColumnType("citext");
            
            builder.HasIndex(t => t.Name)
                .IsUnique();
        }
    }
}
