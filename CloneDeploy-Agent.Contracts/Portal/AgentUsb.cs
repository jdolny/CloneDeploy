namespace CloneDeploy_Agent.Contracts;

public sealed record AgentUsbVariant(Guid BuildId, string Name, int BuildNumber, long Size, string Sha256)
{
    public string Architecture { get; init; } = "x64";
    public string DisplayName => $"{Name} ({Architecture}) — build {BuildNumber} ({Size / 1048576:N0} MB)";
}
public sealed record AgentUsbDrive(int Number, string UniqueId, string SerialNumber, string Name, long Size, string Volumes)
{
    public string DisplayName => $"Disk {Number}: {Name} — {Size / 1073741824d:N1} GB — {Volumes} — {SerialNumber}";
}
// Operations: Catalog, Download, Drives, Write, Status, Cancel. Paths and URLs are never supplied by the UI.
public sealed record AgentUsbRequest(string Operation, Guid? BuildId = null, AgentUsbDrive? Drive = null, bool ConfirmErase = false)
{
    public string Architecture { get; init; } = "x64";
}
public sealed record AgentUsbStatus(string Status, string Message, int Progress = 0,
    AgentUsbVariant[]? Variants = null, AgentUsbDrive[]? Drives = null, Guid? BuildId = null)
{
    public string Architecture { get; init; } = "x64";
}
