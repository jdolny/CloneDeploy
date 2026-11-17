using MudBlazor;

namespace CloneDeploy_Server.UI.Pages.Devices.Devices.Shared;

public enum DeviceRowAction
{
    Archive,
    Delete,
    Connect,
    Restore
}

public sealed record DeviceRowActionDefinition(string Key, string Title, string Icon,
    DeviceRowAction Action, bool DefaultVisible);

public static class DeviceRowActionCatalog
{
    // Add agent commands here when their handlers are available. Edit is always rendered separately.
    public static IReadOnlyList<DeviceRowActionDefinition> All =>
    [
        new("action:connect", UiText.Get(LocKeys.Device.Connect), Icons.Material.Filled.Power, DeviceRowAction.Connect, true),
        new("action:archive", UiText.Get(LocKeys.Policy.Archive), Icons.Material.Filled.Archive, DeviceRowAction.Archive, true),
        new("action:delete", UiText.Get(LocKeys.Common.Delete), Icons.Material.Filled.Delete, DeviceRowAction.Delete, false)
    ];
}
