using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class LdapDirectorySettings
{
    public int DirectorySourceId { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 636;
    public string[] BaseDns { get; set; } = [];
    public int ManagedCredentialId { get; set; }
    public int PageSize { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxObjects { get; set; } = 100_000;
}

public sealed class LdapDirectorySettingsConfiguration : IEntityTypeConfiguration<LdapDirectorySettings>
{
    public void Configure(EntityTypeBuilder<LdapDirectorySettings> builder)
    {
        builder.HasKey(x => x.DirectorySourceId);
        builder.HasOne<ManagedCredential>().WithMany().HasForeignKey(x => x.ManagedCredentialId).OnDelete(DeleteBehavior.Restrict);
    }
}