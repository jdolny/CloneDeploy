using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{ 
    /// <summary>
    /// Associates one Tag with one DeviceAction.
    /// </summary>
    public class ActionTag
    {
        public int Id { get; set; }
        
        public Guid ActionGuid { get; set; }
        
        public int TagId { get; set; }
        
        //navigation properties
        
        public ActionReference? ActionReference { get; set; }
        public Tag? Tag { get; set; }
    }
    
    public class ActionTagConfiguration : IEntityTypeConfiguration<ActionTag>
    {
        public void Configure(EntityTypeBuilder<ActionTag> builder)
        {
            builder
                .HasOne(x => x.ActionReference)
                .WithMany(x => x.Tags)
                .HasForeignKey(x => x.ActionGuid)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}