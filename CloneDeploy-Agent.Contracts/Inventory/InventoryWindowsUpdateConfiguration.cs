namespace CloneDeploy_Agent.Contracts;

/// <summary>A read-only snapshot of update configuration and the sources reporting it.</summary>
public sealed class InventoryWindowsUpdateConfiguration
{
    public DateTime CollectedAtUtc { get; set; }
    public string? TimeZone { get; set; }
    public List<InventoryWindowsUpdateSetting> Settings { get; set; } = [];
    public List<string> UnavailableSources { get; set; } = [];
    public InventoryWindowsUpdateActivity? Activity { get; set; }
}

/// <summary>A reported setting; its source is evidence, not a claim about cross-provider precedence.</summary>
public sealed record InventoryWindowsUpdateSetting(string Name, string Value, string Source, string? GroupPolicy = null);

/// <summary>Latest observed update outcomes, without individual event history.</summary>
public sealed class InventoryWindowsUpdateActivity
{
    public DateTime CollectedAtUtc { get; set; }
    public DateTime? LastAutomaticInstallationSuccessAtUtc { get; set; }
    public DateTime? LastInstallationSuccessAtUtc { get; set; }
    public DateTime? LastDownloadSuccessAtUtc { get; set; }
    public DateTime? LastFailureAtUtc { get; set; }
    public string? LastFailureOperation { get; set; }
    public string? LastFailureErrorCode { get; set; }
    public List<string> UnavailableSources { get; set; } = [];
}