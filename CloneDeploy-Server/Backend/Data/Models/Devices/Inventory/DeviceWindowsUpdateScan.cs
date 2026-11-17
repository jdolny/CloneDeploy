namespace CloneDeploy_Server.Backend.Data.Models;

public class DeviceWindowsUpdateScan
{
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "jsonb")]
    public string? ConfigurationJson { get; set; }
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public Device? Device { get; set; }
    public DateTime CollectedAtUtc { get; set; }
    public bool IsOnline { get; set; }
    public bool Succeeded { get; set; }
    public int? ResultCode { get; set; }
    public int? ErrorCode { get; set; }
    public string? SourceName { get; set; }
    public string? SourceId { get; set; }
    public DateTime? LastSuccessfulSearchAtUtc { get; set; }
    public DateTime? LastSuccessfulOnlineScanAtUtc { get; set; }
    public bool? RebootRequired { get; set; }
}
