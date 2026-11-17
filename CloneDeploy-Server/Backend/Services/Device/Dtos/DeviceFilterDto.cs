using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceFilterDto
{
    public string? SearchText { get; init; }
    public bool? IsActive { get; init; }
    public bool? IsUnmanaged { get; init; }
    public string? CategoryType { get; init; } = "Any";
    // Zero returns all matching devices.
    public int Limit { get; init; } = 100;
    public List<int> TagIds { get; init; } = [];
}
