using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public class ActionFile
{
    public Guid ActionGuid { get; set; }
    public int StoredFileId { get; set; }
    public ActionReference? ActionReference { get; set; }
    public StoredFile File { get; set; } = null!;
}

public class ActionFileConfiguration : IEntityTypeConfiguration<ActionFile>
{
    public void Configure(EntityTypeBuilder<ActionFile> builder)
    {
        builder.HasKey(x => new { x.ActionGuid, x.StoredFileId });
        builder.HasOne(x => x.ActionReference).WithMany(x => x.Files)
            .HasForeignKey(x => x.ActionGuid).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.File).WithMany()
            .HasForeignKey(x => x.StoredFileId).OnDelete(DeleteBehavior.Cascade);
    }
}
