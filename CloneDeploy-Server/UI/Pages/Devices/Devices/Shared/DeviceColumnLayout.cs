using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.UI.Pages.Devices.Devices.Shared;

public static class DeviceColumnLayout
{
    public static void Drop(List<AppUserGridColumn> items, IReadOnlyList<DeviceColumnDefinition> catalog,
        AppUserGridColumn item, bool visible, int index)
    {
        if (!items.Contains(item)) return;
        var isAction = catalog.First(x => x.Key == item.ColumnKey).IsAction;
        items.Remove(item);
        item.Visible = visible;
        var destination = items.Where(x => x.Visible == visible &&
            catalog.First(c => c.Key == x.ColumnKey).IsAction == isAction).ToList();
        index = Math.Clamp(index, 0, destination.Count);
        var insertion = index < destination.Count ? items.IndexOf(destination[index])
            : destination.Count > 0 ? items.IndexOf(destination[^1]) + 1 : items.Count;
        items.Insert(insertion, item);
        for (var i = 0; i < items.Count; i++) items[i].Order = i;
    }
}
