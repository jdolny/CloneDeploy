namespace CloneDeploy_Server.Backend.Services;

public sealed record ActionRelationDto(string Kind, string Name, string Details, string? Href)
{
    public bool Matches(string? search) => string.IsNullOrWhiteSpace(search) ||
        $"{Kind} {Name} {Details}".Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}
