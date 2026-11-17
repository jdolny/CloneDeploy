using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace CloneDeploy_Server.Backend.Data.Models;
/// <summary>Installs Windows updates using the device's configured update source.</summary>
public sealed class WindowsUpdateAction
{
    public int Id { get; set; }
    public Guid Guid { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public WindowsUpdateMode Mode { get; set; } = WindowsUpdateMode.InstallAll;
    public int Timeout { get; set; } = 120;
    public ActionReference? ActionReference { get; set; }
}
public sealed class WindowsUpdateActionConfiguration : IEntityTypeConfiguration<WindowsUpdateAction>
{
    public void Configure(EntityTypeBuilder<WindowsUpdateAction> builder) =>
        builder.HasOne(x => x.ActionReference).WithOne().HasPrincipalKey<ActionReference>(x => x.ActionGuid)
            .HasForeignKey<WindowsUpdateAction>(x => x.Guid).OnDelete(DeleteBehavior.Cascade);
}
