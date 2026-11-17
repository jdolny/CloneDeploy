using CloneDeploy_Agent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class ImagePrepProfile
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public string? AnswerFile { get; set; }
    public bool GenerateAnswerFile { get; set; }
    public string? GeneratorJson { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public ImagePrepGeneratorOptions? GeneratorOptions
    {
        get => string.IsNullOrWhiteSpace(GeneratorJson) ? null : System.Text.Json.JsonSerializer.Deserialize<ImagePrepGeneratorOptions>(GeneratorJson);
        set => GeneratorJson = value is null ? null : System.Text.Json.JsonSerializer.Serialize(value);
    }
    public string? SetupComplete { get; set; }
    public string? PreSysprepScript { get; set; }
    public bool DisableHibernate { get; set; } = true;
    public bool DisableBitLocker { get; set; } = true;
    public bool ResetAgent { get; set; } = true;
    public bool UninstallAgent { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public ImagePrepAgentMode AgentMode
    {
        get => UninstallAgent ? ImagePrepAgentMode.Uninstall : ResetAgent ? ImagePrepAgentMode.Reset : ImagePrepAgentMode.Keep;
        set { ResetAgent = value == ImagePrepAgentMode.Reset; UninstallAgent = value == ImagePrepAgentMode.Uninstall; }
    }
    public bool RunSysprep { get; set; } = true;
    public bool RebootAndCapture { get; set; } = true;

    public AgentImagePrepPlan ToPlan() => new(
        Id, Name, AnswerFile, SetupComplete, PreSysprepScript,
        DisableHibernate, DisableBitLocker, ResetAgent, RunSysprep) { RebootAndCapture = RebootAndCapture, UninstallAgent = UninstallAgent };
}

public sealed class ImagePrepProfileConfiguration : IEntityTypeConfiguration<ImagePrepProfile>
{
    public void Configure(EntityTypeBuilder<ImagePrepProfile> builder)
    {
        builder.Property(x => x.Name).HasColumnType("citext").HasMaxLength(200);
        builder.HasIndex(x => x.Name).IsUnique();
    }
}
