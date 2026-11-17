namespace CloneDeploy_Server.Backend.Services;

public sealed record DirectoryPolicyLinkDto(int PolicyId, string Name, int Order, bool IncludeDescendants);

