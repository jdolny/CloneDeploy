using CloneDeploy_Server.Backend.Services;

namespace CloneDeploy_Server.UI.Pages.Devices.Devices.Shared;

/// <summary>Keeps the grid's row keys stable when live activity replaces a search result.</summary>
public sealed record DeviceListRow : DeviceSearchResultDto
{
    public DeviceListRow(DeviceSearchResultDto result) : base(result) { }

    public bool Equals(DeviceListRow? other) => other is not null && Id == other.Id;
    public override int GetHashCode() => Id.GetHashCode();
}
