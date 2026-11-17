namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceGroupRelationLinkDto(string Name, string Href);
public sealed record DeviceGroupRelationDto(string Kind, string Name, string Details, string? Href,
    IReadOnlyList<DeviceGroupRelationLinkDto> RelatedThrough)
{
    public bool Matches(string? search) => string.IsNullOrWhiteSpace(search) ||
        $"{Kind} {Name} {Details} {string.Join(' ', RelatedThrough.Select(x => x.Name))}"
            .Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}
