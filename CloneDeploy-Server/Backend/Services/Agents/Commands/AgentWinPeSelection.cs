using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.WinPe;

namespace CloneDeploy_Server.Backend.Services;

public static class AgentWinPeSelection
{
    public static string RequireArchitecture(string? architecture) => architecture is "x64" or "arm64" ? architecture
        : throw new InvalidOperationException("Refresh inventory with an updated agent before imaging. Native x64 or ARM64 architecture is required.");

    public static WinPeDefinition Resolve(IEnumerable<WinPeDefinition> definitions, string architecture, string? manufacturer, string? model)
    {
        RequireArchitecture(architecture);
        if (architecture == "x64") return WinPeModelMatching.ResolveForAgent(definitions, manufacturer, model);
        // Project the ARM default without modifying tracked definitions or the x64 default.
        var compatible = definitions.Where(d => d.PublishedBuild is { StoredAtUtc: not null } build && HasArmFiles(build.StoredArtifactsJson))
            .Select(d => new WinPeDefinition
            {
                Id = d.Id, Name = d.Name, IsBase = d.IsBase, IsDefault = d.IsArm64Default,
                ModelRulesJson = d.ModelRulesJson, PublishedBuildId = d.PublishedBuildId, PublishedBuild = d.PublishedBuild
            });
        return WinPeModelMatching.ResolveForAgent(compatible, manufacturer, model);
    }

    private static bool HasArmFiles(string? json)
    {
        try
        {
            var files = JsonSerializer.Deserialize<BuildArtifactDto[]>(json ?? "[]") ?? [];
            return new[] { "boot-arm64.wim", "boot-arm64.sdi" }.All(name => files.Any(f => f.Name == name));
        }
        catch (JsonException) { return false; }
    }
}
