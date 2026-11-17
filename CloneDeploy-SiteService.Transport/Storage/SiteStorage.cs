namespace CloneDeploy.SiteServices;

public sealed record SiteStorageProvider(int Id, string Path, bool Enabled)
{
    public string? LinuxMountPath { get; init; }
    public bool Smb { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    // Never include credentials in diagnostics or record formatting.
    public override string ToString() => $"Site storage provider {Id} (SMB: {Smb}, enabled: {Enabled})";
}
public sealed record SiteStorageJob(int ProviderId, string Key, long Length, string Sha256, string State)
{ public string RelativePath { get; init; } = ""; }
public sealed record SiteStorageManifest(SiteStorageProvider[] Providers, SiteStorageJob[] Jobs)
{
    public int SharedFileConcurrency { get; init; } = 2;
}
public sealed record SiteStorageReport(int ProviderId, string Key, long Length, string Sha256, string State, string? Error = null);

public sealed record SiteDownloadOffer(int ProviderId, string Key, long Length, string Sha256, Guid LeaseId, string ContentType, bool Ranges, string? FileName)
{ public int MaximumSeconds { get; init; } public bool PreserveLease { get; init; } public string RelativePath { get; init; } = ""; }

public sealed record SiteDownloadRedemption(string Ticket, string ClientAddress);
