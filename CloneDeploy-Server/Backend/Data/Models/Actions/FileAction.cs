using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class FileAction
    {
        public int Id { get; set; }
        
        public Guid Guid { get; set; } = Guid.NewGuid();
        
        public required string Name { get; set; }
        
        public string? Description { get; set; }
        
        public required string Destination { get; set; }
        
        public bool DecompressAfterCopy { get; set; }

        public bool OverwriteExisting { get; set; }
        
        //navigation properties
        public ActionReference? ActionReference { get; set; }
    }
    
    public class FileActionConfiguration : IEntityTypeConfiguration<FileAction>
    {
        public void Configure(EntityTypeBuilder<FileAction> builder)
        {
            builder
                .HasOne(x => x.ActionReference)
                .WithOne()
                .HasPrincipalKey<ActionReference>(x => x.ActionGuid)
                .HasForeignKey<FileAction>(x => x.Guid)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}