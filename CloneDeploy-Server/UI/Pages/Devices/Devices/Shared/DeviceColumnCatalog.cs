using System.Linq.Expressions;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace CloneDeploy_Server.UI.Pages.Devices.Devices.Shared;

public sealed record DeviceColumnDefinition(string Key, string Title, bool DefaultVisible,
    RenderFragment Content, int? AttributeId = null)
{
    public bool IsAction { get; init; }
}

public static class DeviceColumnCatalog
{
    public static List<DeviceColumnDefinition> Create(IEnumerable<CustomAttribute> attributes) =>
    [
        Column("platform", "Platform", true, x => x.AgentPlatform),
        Column("name", "Name", true, x => x.Name),
        Column("status", "Connection status", true, x => x.Status),
        Column("activity", "Activity", true, x => x.Activity),
        Column("last-ip", "Last IP", true, x => x.LastIp),
        Column("last-checkin", "Last check-in", true, x => x.LastCheckinTime, FormatDate),
        Column("description", "Description", false, x => x.Description),
        Column("id", "ID", false, x => x.Id),
        Column("last-user", "Last user", false, x => x.LastLoggedInUser),
        Column("client-version", "Client version", false, x => x.ClientVersion, CloneDeploy_Agent.Contracts.AgentUpdateOffer.DisplayVersion),
        Column("manufacturer", "Manufacturer", false, x => x.Manufacturer),
        Column("model", "Model", false, x => x.Model),
        Column("domain", "Domain", false, x => x.Domain),
        Column("os-name", "OS name", false, x => x.OsName),
        Column("os-version", "OS version", false, x => x.OsVersion),
        Column("os-build", "OS build", false, x => x.OsBuild),
        Column("provision-status", "Enrollment status", false, x => DeviceEnrollmentLabels.Status(x.ProvisionStatus)),
        Column("ad-sync", "AD sync", false, x => x.IsAdSync),
        Column("ad-disabled", "AD disabled", false, x => x.AdDisabled),
        Column("provisioned", "Enrolled", false, x => x.ProvisionedTime, FormatDate),
        .. attributes.Select(AttributeColumn),
        .. DeviceRowActionCatalog.All.Select(action => new DeviceColumnDefinition(
            action.Key, action.Title, action.DefaultVisible, _ => { }) { IsAction = true })
    ];

    public static List<AppUserGridColumn> Merge(IReadOnlyList<DeviceColumnDefinition> catalog,
        IEnumerable<AppUserGridColumn> saved)
    {
        var known = catalog.Select(x => x.Key).ToHashSet();
        var result = saved.Where(x => known.Contains(x.ColumnKey)).OrderBy(x => x.Order)
            .DistinctBy(x => x.ColumnKey)
            .Select(x => new AppUserGridColumn { ColumnKey = x.ColumnKey, Visible = x.Visible }).ToList();
        var existing = result.Select(x => x.ColumnKey).ToHashSet();
        result.AddRange(catalog.Where(x => !existing.Contains(x.Key))
            .Select(x => new AppUserGridColumn { ColumnKey = x.Key, Visible = x.DefaultVisible }));
        // Add the new default icon beside Name for existing saved layouts too.
        // Once saved, its chosen visibility and position remain untouched.
        if (!existing.Contains("platform"))
        {
            var platform = result.FirstOrDefault(x => x.ColumnKey == "platform");
            if (platform is not null)
            {
                result.Remove(platform);
                var nameIndex = result.FindIndex(x => x.ColumnKey == "name");
                result.Insert(nameIndex < 0 ? 0 : nameIndex, platform);
            }
        }
        for (var i = 0; i < result.Count; i++) result[i].Order = i;
        return result;
    }

    private static string FormatDate(DateTime? value) => value?.ToLocalTime().ToString("g") ?? "—";

    private static DeviceColumnDefinition Column<T>(string key, string title, bool visible,
        Expression<Func<DeviceSearchResultDto, T>> property, Func<T, string>? format = null)
    {
        var getter = property.Compile();
        title = UiText.Label(title);
        return new(key, title, visible, builder =>
        {
            builder.OpenComponent<PropertyColumn<DeviceSearchResultDto, T>>(0);
            builder.SetKey(key);
            builder.AddAttribute(1, "Property", property);
            builder.AddAttribute(2, "Title", title);
            if (key == "platform")
            {
                builder.AddAttribute(3, "HeaderStyle", "width:48px;max-width:48px;padding:0 8px;");
                builder.AddAttribute(4, "CellStyle", "width:48px;max-width:48px;padding:0 8px;text-align:center;");
                builder.AddAttribute(5, "HeaderTemplate", (RenderFragment<HeaderContext<DeviceSearchResultDto>>)(_ => _ => { }));
                builder.AddAttribute(6, "Sortable", false);
                builder.AddAttribute(7, "Filterable", false);
                builder.AddAttribute(8, "CellTemplate", (RenderFragment<CellContext<DeviceSearchResultDto>>)
                    (context => cell =>
                    {
                        var platform = context.Item.AgentPlatform;
                        var icon = platform == CloneDeploy_Agent.Contracts.AgentPlatform.MacOS ? Icons.Custom.Brands.Apple
                            : platform == CloneDeploy_Agent.Contracts.AgentPlatform.Windows ? Icons.Custom.Brands.MicrosoftWindows
                            : Icons.Material.Outlined.Computer;
                        cell.OpenComponent<MudIcon>(0);
                        cell.AddAttribute(1, "Icon", icon);
                        cell.AddAttribute(2, "Size", Size.Small);
                        cell.AddAttribute(3, "Title", platform);
                        cell.AddAttribute(4, "aria-label", platform);
                        cell.CloseComponent();
                    }));
            }
            else if (key == "name")
                builder.AddAttribute(3, "CellTemplate", (RenderFragment<CellContext<DeviceSearchResultDto>>)
                    (context => cell =>
                    {
                        cell.OpenComponent<MudLink>(0);
                        cell.AddAttribute(1, "Href", $"/devices/manage/{context.Item.Id}");
                        cell.AddAttribute(2, "Class", "device-name-link");
                        cell.AddAttribute(3, "ChildContent", (RenderFragment)(text =>
                            text.AddContent(0, context.Item.Name ?? UiText.Get(LocKeys.Ui.ManageDevicesPage.Device0, context.Item.Id))));
                        cell.CloseComponent();
                    }));
            else if (key == "status")
                builder.AddAttribute(3, "CellTemplate", (RenderFragment<CellContext<DeviceSearchResultDto>>)
                    (context => cell =>
                    {
                        var connected = context.Item.Status == "Connected";
                        cell.OpenComponent<MudChip<string>>(0);
                        cell.AddAttribute(1, "Variant", Variant.Outlined);
                        cell.AddAttribute(2, "Color", connected ? Color.Success : Color.Error);
                        cell.AddAttribute(3, "Size", Size.Small);
                        cell.AddAttribute(4, "ChildContent", (RenderFragment)(text =>
                            text.AddContent(0, UiText.Label(connected ? "Connected" : "Disconnected"))));
                        cell.CloseComponent();
                    }));
            else if (format is not null)
                builder.AddAttribute(3, "CellTemplate", (RenderFragment<CellContext<DeviceSearchResultDto>>)
                    (context => cell => cell.AddContent(0, format(getter(context.Item)))));
            builder.CloseComponent();
        });
    }

    private static DeviceColumnDefinition AttributeColumn(CustomAttribute attribute)
    {
        var id = attribute.Id;
        var key = $"custom-attribute:{id}";
        string Value(DeviceSearchResultDto row) => row.CustomAttributeValues.GetValueOrDefault(id, "");
        // Dictionary entries have no distinct CLR property names for the grid's built-in filters.
        return new(key, attribute.Name, false, builder =>
        {
            builder.OpenComponent<TemplateColumn<DeviceSearchResultDto>>(0);
            builder.SetKey(key);
            builder.AddAttribute(1, "Title", attribute.Name);
            builder.AddAttribute(2, "Sortable", true);
            builder.AddAttribute(3, "SortBy", (Func<DeviceSearchResultDto, object>)(row => Value(row)));
            builder.AddAttribute(4, "Filterable", false);
            builder.AddAttribute(5, "CellTemplate", (RenderFragment<CellContext<DeviceSearchResultDto>>)
                (context => cell => cell.AddContent(0, Value(context.Item))));
            builder.CloseComponent();
        }, id);
    }
}
