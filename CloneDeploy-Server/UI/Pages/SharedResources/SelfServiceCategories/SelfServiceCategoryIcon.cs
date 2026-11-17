using MudBlazor;

namespace CloneDeploy_Server.UI.Pages.SharedResources.SelfServiceCategories;

internal static class SelfServiceCategoryIcon
{
    public static string Get(string key) => key switch
    {
        "Software" => Icons.Material.Filled.Apps,
        "Printers" => Icons.Material.Filled.Print,
        "Maintenance" => Icons.Material.Filled.Build,
        "Network" => Icons.Material.Filled.Lan,
        "Security" => Icons.Material.Filled.Security,
        "Help" => Icons.Material.Filled.HelpOutline,
        "Productivity" => Icons.Material.Filled.Description,
        "Communication" => Icons.Material.Filled.Chat,
        "Files & Storage" => Icons.Material.Filled.Folder,
        "Updates" => Icons.Material.Filled.Update,
        "Accounts" => Icons.Material.Filled.Person,
        "Education" => Icons.Material.Filled.School,
        _ => Icons.Material.Filled.Handyman
    };
}
