namespace CloneDeploy_Agent.Contracts;

/// <summary>Saved generator settings contain credential IDs, never passwords.</summary>
public sealed class ImagePrepGeneratorOptions
{
    public Guid MarkerId { get; set; } = Guid.NewGuid();
    public string Architecture { get; set; } = "amd64";
    public string? Language { get; set; } = "en-US";
    public string? InputLocale { get; set; } = "en-US";
    public string? SystemLocale { get; set; } = "en-US";
    public string? UserLocale { get; set; } = "en-US";
    public string? TimeZone { get; set; }
    public string? RegisteredOwner { get; set; }
    public string? RegisteredOrganization { get; set; }
    public string? ProductKey { get; set; }
    public bool HideEula { get; set; }
    public bool HideOemRegistration { get; set; } = true;
    public bool HideOnlineAccountScreens { get; set; }
    public bool HideWirelessSetup { get; set; }
    public int? ProtectYourPc { get; set; }
    public List<ImagePrepLocalAccount> LocalAccounts { get; set; } = [];
    public int? AdministratorCredentialId { get; set; }
    public int? AutoLogonCredentialId { get; set; }
    public int AutoLogonCount { get; set; } = 1;
    public string? JoinDomain { get; set; }
    public string? OrganizationalUnit { get; set; }
    public int? DomainCredentialId { get; set; }
    public IEnumerable<int> CredentialIds() => LocalAccounts.Select(x => x.CredentialId)
        .Concat(new[] { AdministratorCredentialId, AutoLogonCredentialId, DomainCredentialId }.OfType<int>()).Distinct();
    public string Marker(int id) => $"CDSecret-{MarkerId:N}-{id}";
}

public sealed class ImagePrepLocalAccount
{
    public int CredentialId { get; set; }
    public bool Administrator { get; set; } = true;
}

public static class ImagePrepCredentialFiles
{
    public const string TicketRelativePath = @"Windows\Setup\Scripts\CloneDeploy-SysprepCredentials.ticket";
    public const string MarkerPrefix = "CDSecret-";
}
