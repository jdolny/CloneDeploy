namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds settings for connecting to Active Directory and choosing what to import.
/// </summary>
public sealed class ActiveDirectorySettings
{
    public int DirectorySourceId { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 636;
    public string[] BaseDns { get; set; } = [];
    internal Guid? ExpectedDomainGuid { get; set; }
    public int ManagedCredentialId { get; set; }
    public int PageSize { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxObjects { get; set; } = 100_000;

    public string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(Host) ||
            Host.Contains('/') ||
            Port is < 1 or > 65535 ||
            ManagedCredentialId <= 0 ||
            PageSize is < 1 or > 1000 ||
            TimeoutSeconds is < 1 or > 3600 ||
            MaxObjects < 1)
        {
            return "Configure Active Directory host, managed credential, and valid sync limits before syncing.";
        }

        return null;
    }

    // Protocol readers still abort invalid imports; UI validation uses the returned message.
    public void Validate()
    {
        var error = GetValidationError();
        if (error is not null)
            throw new InvalidOperationException(error);
    }

    public string[] SearchBases() =>
        BaseDns
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
