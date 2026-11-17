using System.Globalization;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceReportColumn(string Key, string Title);
public sealed record DeviceReportResultRow(int DeviceId, List<string> Values)
{
    public string? ReportedDetail { get; init; }
    public string? PolicyHash { get; init; }
    public string? PolicyName { get; init; }
    public int? PolicyId { get; init; }
    public Guid? RunId { get; init; }
}
public sealed record DeviceReportPreview(int Total, List<DeviceReportColumn> Columns, List<DeviceReportResultRow> Rows, DateTime AsOfUtc);

public static class DeviceReportColumns
{
    public static readonly string[] Defaults = ["name", "last-user", "model", "serial", "os", "last-checkin"];
    public static List<DeviceReportColumn> Create(IEnumerable<CustomAttribute> attributes) =>
    [
        new("name", "Device name"), new("description", "Description"), new("id", "Device ID"), new("guid", "Device GUID"),
        new("last-user", "Last user"), new("manufacturer", "Manufacturer"), new("model", "Model"), new("serial", "Serial number"),
        new("domain", "Domain"), new("os", "Operating system"), new("os-version", "OS version"), new("os-build", "OS build"),
        new("architecture", "OS architecture"), new("native-architecture", "Native architecture"), new("agent-version", "Agent version"), new("last-ip", "Last known IP"),
        new("last-checkin", "Last check-in (UTC)"), new("last-inventory", "Last inventory (UTC)"), new("enrolled", "Enrolled (UTC)"),
        new("enrollment", "Enrollment status"), new("directory", "Directory path"), new("tags", "Tags"),
        .. attributes.Select(x => new DeviceReportColumn($"attribute:{x.Id}", $"Custom attribute: {x.Name}"))
    ];

    public static string CsvCell(string? value)
    {
        value ??= "";
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    internal static string Text(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
