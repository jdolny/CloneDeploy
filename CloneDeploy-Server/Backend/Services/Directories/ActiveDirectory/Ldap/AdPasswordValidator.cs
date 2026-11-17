using System.DirectoryServices.Protocols;
using System.Net;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public interface IAdPasswordValidator
{
    Task<bool> ValidateAsync(DirectorySource source, DirectoryObject identity, string? domain, string password, CancellationToken ct = default);
}

// Bind as the selected directory object, then verify immutable identity and domain.
// Passwords remain transient; platform LDAPS certificate validation is always enabled.
public sealed class AdPasswordValidator : IAdPasswordValidator
{
    public Task<bool> ValidateAsync(DirectorySource source, DirectoryObject identity, string? domain, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(identity.DistinguishedName)) return Task.FromResult(false);
        return Task.Run(() => Validate(source, identity, domain, password, ct), ct);
    }

    private static bool Validate(DirectorySource source, DirectoryObject identity, string? domain, string password, CancellationToken ct)
    {
        var settings = source.LdapSettings ?? throw new InvalidOperationException("LDAP settings are missing.");
        using var connection = new LdapConnection(new LdapDirectoryIdentifier(settings.Host, settings.Port),
            new NetworkCredential(identity.DistinguishedName, password), AuthType.Basic)
        { AutoBind = false, Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 30)) };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        ct.ThrowIfCancellationRequested();
        try { connection.Bind(); }
        catch (LdapException exception) when (exception.ErrorCode == 49) { return false; }
        var root = Read("", "defaultNamingContext", "configurationNamingContext");
        var domainDn = Text(root, "defaultNamingContext");
        if (string.IsNullOrWhiteSpace(domainDn)) return false;
        var domainEntry = Read(domainDn, "objectGUID");
        var expectedDomain = source.SyncState.DomainObjectGuid ?? source.SyncState.RootObjectGuid;
        if (GuidValue(domainEntry) != expectedDomain) return false;
        var user = Read(identity.DistinguishedName, "objectGUID", "userAccountControl");
        if (GuidValue(user) != identity.ObjectGuid || !int.TryParse(Text(user, "userAccountControl"), out var flags) || (flags & 2) != 0) return false;
        if (domain is not null)
        {
            var config = Text(root, "configurationNamingContext");
            if (string.IsNullOrWhiteSpace(config)) return false;
            var escapedDn = domainDn.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
            ct.ThrowIfCancellationRequested();
            var response = (SearchResponse)connection.SendRequest(new SearchRequest("CN=Partitions," + config,
                "(&(objectClass=crossRef)(nCName=" + escapedDn + "))", SearchScope.OneLevel, "nETBIOSName", "dnsRoot"));
            if (response.Entries.Count != 1 || !(string.Equals(domain, Text(response.Entries[0], "nETBIOSName"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(domain, Text(response.Entries[0], "dnsRoot"), StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;

        SearchResultEntry Read(string dn, params string[] attributes)
        {
            ct.ThrowIfCancellationRequested();
            var response = (SearchResponse)connection.SendRequest(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, attributes));
            if (response.Entries.Count != 1) throw new InvalidOperationException("AD identity verification failed.");
            return response.Entries[0];
        }
    }

    private static string? Text(SearchResultEntry entry, string name) => entry.Attributes[name]?.GetValues(typeof(string)).Cast<string>().FirstOrDefault();
    private static Guid? GuidValue(SearchResultEntry entry) => entry.Attributes["objectGUID"]?[0] is byte[] { Length: 16 } bytes ? new Guid(bytes) : null;
}
