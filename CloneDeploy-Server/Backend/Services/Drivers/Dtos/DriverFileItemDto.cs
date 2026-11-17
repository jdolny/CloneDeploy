using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DriverFileItemDto(ActionFileItemDto File, DriverVendor? Vendor, string? Model, string? Os, string? Release, bool FollowLatest)
{
    public DriverPreparationStatus PreparationStatus { get; init; }
    public string? PreparationError { get; init; }
    public bool HasPreparedArchive { get; init; }
    public bool PreparedIsCurrent { get; init; }
    public int InfCount { get; init; }
    public DateTime? PreparedAtUtc { get; init; }
}

