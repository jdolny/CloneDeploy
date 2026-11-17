namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

/// <summary>
/// Represents one matching rule or a group of rules for selecting devices.
/// </summary>
public sealed class DeviceQueryNodeDto
{
    public string Scope { get; set; } = "";
    public QueryMatch Match { get; set; }
    public bool Negate { get; set; }
    public string? Field { get; set; }
    public QueryOperator Operator { get; set; }
    public string Value { get; set; } = "";
    public int? DirectorySourceId { get; set; }
    public Guid? ObjectGuid { get; set; }
    public bool IncludeDescendants { get; set; } = true;
    public List<DeviceQueryNodeDto> Children { get; set; } = [];
}
