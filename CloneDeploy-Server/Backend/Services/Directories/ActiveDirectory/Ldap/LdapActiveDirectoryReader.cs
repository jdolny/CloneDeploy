using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

// Allows parsing tests to use real DirectoryAttribute values without a live DC.
internal sealed record DirectoryLdapEntry(string Dn, IReadOnlyDictionary<string, DirectoryAttribute> Attributes);

/// <summary>
/// Reads users, computers, groups, and locations from Active Directory before any changes are saved.
/// </summary>
/// <remarks>
/// Reads an Active Directory domain, its selected objects, and group memberships over LDAP.
/// Builds a complete snapshot before the caller saves any changes.
/// </remarks>
public sealed class LdapActiveDirectoryReader : IActiveDirectoryReader
{
    // Search the whole verified domain, not the configured inventory import OUs.
    public static Task<string?> FindComputerAsync(ActiveDirectorySettings settings, string username,
        string password, string domainName, string computerName, CancellationToken ct)
    {
        settings.Validate();
        if (ComputerNameValidation.Error(computerName) is { } error) throw new InvalidOperationException(error);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("An AD bind username and non-empty password are required.");
        return Task.Run(() =>
        {
            using var connection = new LdapConnection(new LdapDirectoryIdentifier(settings.Host, settings.Port),
                new NetworkCredential(username, password), AuthType.Basic)
            {
                Timeout = TimeSpan.FromSeconds(Math.Min(30, settings.TimeoutSeconds)), AutoBind = false
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.SecureSocketLayer = true;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            ct.ThrowIfCancellationRequested();
            connection.Bind();
            var domainDn = Text(ReadEntry(connection, "", ["defaultNamingContext"], ct), "defaultNamingContext");
            var expectedDn = string.Join(',', domainName.Split('.').Select(x => "DC=" + x));
            if (!string.Equals(domainDn, expectedDn, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The LDAP connection does not serve the deployment's target domain.");
            var domain = Map(ReadEntry(connection, domainDn!, Attributes, ct), settings.DirectorySourceId);
            if (domain.Kind != DirectoryObjectKind.Domain ||
                settings.ExpectedDomainGuid is { } expected && domain.ObjectGuid != expected)
                throw new InvalidOperationException("The LDAP connection's domain identity changed.");
            var account = (computerName + "$").Replace("\\", "\\5c").Replace("*", "\\2a")
                .Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
            var response = Search(connection, new SearchRequest(domainDn,
                $"(&(objectCategory=computer)(sAMAccountName={account}))", SearchScope.Subtree, "distinguishedName")
                { SizeLimit = 2 }, ct);
            if (response.Entries.Count > 1)
                throw new InvalidOperationException("Multiple AD computer accounts match the deployment name.");
            return response.Entries.Count == 0 ? null : response.Entries[0].DistinguishedName;
        }, ct);
    }

    private static readonly string[] Attributes =
    [
        "objectGUID", "objectSid", "objectClass", "name", "sAMAccountName", "userPrincipalName",
        "mail", "dNSHostName", "userAccountControl"
    ];

    /// <summary>
    /// Validates the settings and runs the blocking LDAP work on a background thread.
    /// </summary>
    public Task<DirectorySnapshotDto> ReadAsync(ActiveDirectorySettings settings, string username,
        string password, CancellationToken cancellationToken)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("An AD bind username and non-empty password are required.");
        // Native bind/search calls are synchronous. Keep them off the Blazor circuit thread.
        // Cancellation is checked between calls; in-flight operations have a timeout.
        return Task.Run(() => Read(settings, username, password, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Connects securely, verifies the domain, and reads the selected directory data.
    /// </summary>
    private static DirectorySnapshotDto Read(ActiveDirectorySettings settings, string username, string password,
        CancellationToken ct)
    {
        using var connection = new LdapConnection(new LdapDirectoryIdentifier(settings.Host, settings.Port),
            new NetworkCredential(username, password), AuthType.Basic)
        {
            Timeout = TimeSpan.FromSeconds(Math.Min(30, settings.TimeoutSeconds)),
            AutoBind = false
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        // Use platform certificate and hostname validation; never install a bypass callback.
        ct.ThrowIfCancellationRequested();
        connection.Bind();
        ct.ThrowIfCancellationRequested();

        var rootDse = ReadEntry(connection, "", ["defaultNamingContext"], ct);
        var domainDn = Text(rootDse, "defaultNamingContext");
        if (string.IsNullOrWhiteSpace(domainDn))
            throw new InvalidOperationException("AD did not provide a domain naming context.");
        var domain = Map(ReadEntry(connection, domainDn, Attributes, ct), settings.DirectorySourceId);
        if (domain.Kind != DirectoryObjectKind.Domain)
            throw new InvalidOperationException("The connection must point to an AD domain.");
        if (settings.ExpectedDomainGuid.HasValue && settings.ExpectedDomainGuid.Value != domain.ObjectGuid)
            throw new InvalidOperationException("This connection points to a different AD domain. Create another connection instead.");

        return CollectSnapshot(settings, domain,
            dn => ReadScope(connection, dn, settings.DirectorySourceId, settings.PageSize, ct),
            dn => Map(ReadEntry(connection, dn, Attributes, ct), settings.DirectorySourceId),
            dn => ReadMembers(connection, dn, ct), ct);
    }

    /// <summary>
    /// Reads objects under one search base a page at a time.
    /// </summary>
    private static IEnumerable<DirectoryObject> ReadScope(LdapConnection connection, string baseDn,
        int directorySourceId, int pageSize, CancellationToken ct)
    {
        byte[] cookie = [];
        var seenCookies = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var request = new SearchRequest(baseDn,
                "(|(objectClass=domainDNS)(objectClass=container)(objectClass=organizationalUnit)(objectClass=group)(objectClass=user))",
                SearchScope.Subtree, Attributes);
            request.Controls.Add(new PageResultRequestControl(pageSize) { Cookie = cookie, IsCritical = true });
            var response = Search(connection, request, ct);
            // References to other naming contexts are not followed.
            foreach (SearchResultEntry entry in response.Entries)
            {
                ct.ThrowIfCancellationRequested();
                yield return Map(Adapt(entry), directorySourceId);
            }
            var page = response.Controls.OfType<PageResultResponseControl>().SingleOrDefault();
            if (page is null)
            {
                throw new InvalidOperationException("AD did not return a paging control; refusing a potentially partial sync.");
            }

            // The cookie tells AD where the next page starts. An empty cookie means we are done.
            cookie = page.Cookie;
            if (cookie.Length > 0 && !seenCookies.Add(Convert.ToBase64String(cookie)))
                throw new InvalidOperationException("AD returned a repeated paging cookie.");
        } while (cookie.Length > 0);
    }

    /// <summary>
    /// Combines the selected scopes, adds missing parents, and records group members.
    /// </summary>
    internal static DirectorySnapshotDto CollectSnapshot(
        ActiveDirectorySettings settings,
        DirectoryObject domain,
        Func<string, IEnumerable<DirectoryObject>> readScope,
        Func<string, DirectoryObject> readAncestor,
        Func<string, string[]> readMembers,
        CancellationToken ct)
    {
        var domainDn = domain.DistinguishedName;
        var bases = settings.SearchBases();

        if (bases.Length == 0)
            bases = [domainDn];

        if (bases.Any(dn => !DistinguishedNameUtility.IsWithin(dn, domainDn)))
            throw new InvalidOperationException(
                "Every search base must belong to the connected AD domain.");

        domain.IsScopeAncestor = true;

        var entries = new Dictionary<Guid, DirectoryObject>
        {
            [domain.ObjectGuid] = domain
        };

        void Add(DirectoryObject item)
        {
            ct.ThrowIfCancellationRequested();

            if (item.DirectorySourceId != settings.DirectorySourceId ||
                !DistinguishedNameUtility.IsWithin(item.DistinguishedName, domainDn))
            {
                throw new InvalidOperationException(
                    "AD returned an object outside this directory.");
            }

            if (entries.TryGetValue(item.ObjectGuid, out var existing))
            {
                if (existing.Kind != item.Kind ||
                    !DistinguishedNameUtility.Equals(
                        existing.DistinguishedName,
                        item.DistinguishedName))
                {
                    throw new InvalidOperationException(
                        "AD changed while reading overlapping scopes. Retry the sync.");
                }

                // An object read inside a selected scope is more than just a parent placeholder.
                item.IsScopeAncestor = item.IsScopeAncestor && existing.IsScopeAncestor;
            }

            entries[item.ObjectGuid] = item;

            if (entries.Count > settings.MaxObjects)
            {
                throw new InvalidOperationException(
                    "AD sync exceeded MaxObjects; the previous snapshot was retained.");
            }
        }

        foreach (var baseDn in bases)
        {
            ct.ThrowIfCancellationRequested();

            var seen = new HashSet<Guid>();
            var foundBase = false;

            foreach (var item in readScope(baseDn))
            {
                if (!DistinguishedNameUtility.IsWithin(
                        item.DistinguishedName, baseDn))
                {
                    throw new InvalidOperationException(
                        "AD returned an object outside the requested search base.");
                }

                if (!seen.Add(item.ObjectGuid))
                {
                    throw new InvalidOperationException(
                        "AD changed during paging (duplicate object). Retry the sync.");
                }

                if (DistinguishedNameUtility.Equals(
                        item.DistinguishedName, baseDn))
                {
                    if (item.Kind is not (
                        DirectoryObjectKind.Domain or
                        DirectoryObjectKind.Container or
                        DirectoryObjectKind.OrganizationalUnit))
                    {
                        throw new InvalidOperationException(
                            "Search bases must be domains, containers, or OUs.");
                    }

                    foundBase = true;
                }

                item.IsScopeAncestor = false;
                Add(item);
            }

            if (!foundBase)
            {
                throw new InvalidOperationException(
                    "A search base was not readable. The previous snapshot was retained.");
            }
        }

        // Fetch only the ancestor containers needed to preserve real OU paths,
        // not their contents.
        var knownDns = entries.Values
            .Select(x => x.DistinguishedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var baseDn in bases)
        {
            var parent = DistinguishedNameUtility.Parent(baseDn);

            while (parent.Length > 0 &&
                   DistinguishedNameUtility.IsWithin(parent, domainDn))
            {
                if (!knownDns.Contains(parent))
                {
                    ct.ThrowIfCancellationRequested();

                    var ancestor = readAncestor(parent);

                    if (!DistinguishedNameUtility.Equals(
                            ancestor.DistinguishedName, parent) ||
                        ancestor.Kind is not (
                            DirectoryObjectKind.Domain or
                            DirectoryObjectKind.Container or
                            DirectoryObjectKind.OrganizationalUnit))
                    {
                        throw new InvalidOperationException(
                            "AD returned an unexpected ancestor.");
                    }

                    ancestor.IsScopeAncestor = true;
                    Add(ancestor);
                    knownDns.Add(parent);
                }

                parent = DistinguishedNameUtility.Parent(parent);
            }
        }

        var byDn = entries.Values.ToDictionary(
            x => x.DistinguishedName,
            x => x.ObjectGuid,
            StringComparer.OrdinalIgnoreCase);

        var memberships = new List<DirectoryGroupMembership>();

        foreach (var group in entries.Values
                     .Where(x => x.Kind == DirectoryObjectKind.Group))
        {
            ct.ThrowIfCancellationRequested();

            var seenMembers =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var memberDn in readMembers(group.DistinguishedName))
            {
                ct.ThrowIfCancellationRequested();

                if (!seenMembers.Add(memberDn))
                    continue;

                Guid? resolvedMemberGuid = null;
                if (byDn.TryGetValue(memberDn, out var memberGuid))
                {
                    resolvedMemberGuid = memberGuid;
                }

                // Keep the member's name even when it falls outside the selected scopes.
                memberships.Add(new DirectoryGroupMembership
                {
                    DirectorySourceId = settings.DirectorySourceId,
                    GroupGuid = group.ObjectGuid,
                    MemberGuid = resolvedMemberGuid,
                    MemberDistinguishedName = memberDn
                });
            }
        }

        ct.ThrowIfCancellationRequested();

        ResolveParents(entries.Values, domain.ObjectGuid);

        ct.ThrowIfCancellationRequested();

        return new DirectorySnapshotDto(
            domain.ObjectGuid,
            entries.Values.ToList())
        {
            Memberships = memberships
        };
    }

    /// <summary>
    /// Sends one LDAP search and checks for cancellation or an incomplete result.
    /// </summary>
    private static SearchResponse Search(LdapConnection connection, SearchRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        request.TimeLimit = connection.Timeout;
        SearchResponse response;
        try
        {
            response = (SearchResponse)connection.SendRequest(request, connection.Timeout);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        ct.ThrowIfCancellationRequested();
        if (response.ResultCode != ResultCode.Success)
            throw new InvalidOperationException("AD returned an incomplete search result.");
        return response;
    }

    /// <summary>
    /// Reads exactly one directory object and its requested fields.
    /// </summary>
    private static DirectoryLdapEntry ReadEntry(LdapConnection connection, string dn, string[] attributes, CancellationToken ct)
    {
        var response = Search(connection, new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, attributes), ct);
        if (response.Entries.Count != 1)
            throw new InvalidOperationException("The requested AD entry was not readable.");
        return Adapt(response.Entries[0]);
    }

    /// <summary>
    /// Copies an LDAP result into a simpler object that can also be used in tests.
    /// </summary>
    private static DirectoryLdapEntry Adapt(SearchResultEntry entry)
    {
        var attributes = new Dictionary<string, DirectoryAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in entry.Attributes.AttributeNames)
        {
            attributes.Add(name, entry.Attributes[name]);
        }

        return new DirectoryLdapEntry(entry.DistinguishedName, attributes);
    }

    /// <summary>
    /// Links each object to the nearest parent included in the snapshot.
    /// </summary>
    private static void ResolveParents(IEnumerable<DirectoryObject> objects, Guid rootGuid)
    {
        var items = objects.ToList();

        var byDn = items.ToDictionary(x => x.DistinguishedName, x => x.ObjectGuid, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            item.ParentGuid = null;

            if (item.ObjectGuid == rootGuid)
                continue;

            var parentDn = DistinguishedNameUtility.Parent(item.DistinguishedName);

            while (!string.IsNullOrEmpty(parentDn))
            {
                if (byDn.TryGetValue(parentDn, out var parentGuid))
                {
                    item.ParentGuid = parentGuid;
                    break;
                }

                parentDn = DistinguishedNameUtility.Parent(parentDn);
            }

            if (item.ParentGuid is null)
            {
                throw new InvalidOperationException(
                    "Directory hierarchy was incomplete; the previous snapshot was retained.");
            }
        }
    }

    /// <summary>
    /// Reads all members of a group, requesting more batches when needed.
    /// </summary>
    private static string[] ReadMembers(LdapConnection connection, string dn, CancellationToken ct)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = 0;
        while (true)
        {
            var entry = ReadEntry(connection, dn, [$"member;range={start}-*"], ct);
            var next = AppendMemberRange(entry, start, members);
            if (next is null)
            {
                return members.ToArray();
            }

            start = next.Value;
        }
    }

    /// <summary>
    /// Adds one batch of group members and returns the next starting position, or null when finished.
    /// </summary>
    internal static int? AppendMemberRange(DirectoryLdapEntry entry, int start, HashSet<string> members)
    {
        var attribute = entry.Attributes.Values.SingleOrDefault(x =>
            x.Name.StartsWith("member;range=", StringComparison.OrdinalIgnoreCase)) ?? Attribute(entry, "member");
        if (attribute is null)
        {
            if (start == 0) return null;
            throw new InvalidOperationException("AD group membership range was incomplete.");
        }
        members.UnionWith(Strings(attribute));
        if (attribute.Name.Equals("member", StringComparison.OrdinalIgnoreCase))
        {
            if (start != 0) throw new InvalidOperationException("AD group membership changed during sync. Retry the sync.");
            return null;
        }
        var range = attribute.Name["member;range=".Length..].Split('-');
        if (range.Length != 2 || !int.TryParse(range[0], out var actualStart) || actualStart != start)
            throw new InvalidOperationException("AD returned an unexpected member range.");
        if (range[1] == "*") return null;
        if (!int.TryParse(range[1], out var end) || end < start || end == int.MaxValue)
            throw new InvalidOperationException("AD returned an invalid member range.");
        return end + 1;
    }

    /// <summary>
    /// Converts LDAP fields into the directory object used by the application.
    /// </summary>
    internal static DirectoryObject Map(DirectoryLdapEntry entry, int directorySourceId)
    {
        var classAttribute = Attribute(entry, "objectClass");
        if (classAttribute is null)
        {
            throw new InvalidOperationException("AD objectClass was not readable.");
        }

        var classes = new HashSet<string>(Strings(classAttribute), StringComparer.OrdinalIgnoreCase);
        var kind = DirectoryObjectKind.User;

        // Objects can have several classes. Check the more specific types first.
        if (classes.Contains("computer"))
        {
            kind = DirectoryObjectKind.Computer;
        }
        else if (classes.Contains("group"))
        {
            kind = DirectoryObjectKind.Group;
        }
        else if (classes.Contains("organizationalUnit"))
        {
            kind = DirectoryObjectKind.OrganizationalUnit;
        }
        else if (classes.Contains("domainDNS"))
        {
            kind = DirectoryObjectKind.Domain;
        }
        else if (classes.Contains("container"))
        {
            kind = DirectoryObjectKind.Container;
        }

        if (!int.TryParse(Text(entry, "userAccountControl"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var control))
        {
            control = 0;
        }

        // AD stores several account settings in one number. The flag with value 2 means disabled.
        const int disabledAccountFlag = 2;
        return new DirectoryObject
        {
            DirectorySourceId = directorySourceId,
            ObjectGuid = ObjectGuid(entry),
            DistinguishedName = entry.Dn,
            Name = Text(entry, "name") ?? entry.Dn,
            Kind = kind,
            AccountName = Text(entry, "sAMAccountName"),
            UserPrincipalName = Text(entry, "userPrincipalName"),
            ObjectSid = ReadSid(entry),
            Email = Text(entry, "mail"),
            DnsHostName = Text(entry, "dNSHostName"),
            Disabled = (control & disabledAccountFlag) != 0
        };
    }

    /// <summary>
    /// Looks up a field by name, returning null when it is missing.
    /// </summary>
    private static DirectoryAttribute? Attribute(DirectoryLdapEntry entry, string name)
    {
        return entry.Attributes.GetValueOrDefault(name);
    }

    // Decode directly so importing SIDs does not require Windows SecurityIdentifier APIs.
    /// <summary>
    /// Converts the binary security identifier into SID text, such as S-1-5-21-123.
    /// </summary>
    private static string? ReadSid(DirectoryLdapEntry entry)
    {
        var attribute = Attribute(entry, "objectSid");
        if (attribute is null)
        {
            // OUs and containers do not have a SID.
            return null;
        }

        if (attribute.Count != 1 || attribute.GetValues(typeof(byte[]))[0] is not byte[] bytes ||
            bytes.Length < 8 || bytes[0] != 1 || bytes[1] > 15 || bytes.Length != 8 + bytes[1] * 4)
            throw new InvalidOperationException("AD objectSid was invalid; the previous snapshot was retained.");
        // A SID starts with a version and a count, followed by a six-byte authority number.
        ulong authority = 0;
        for (var i = 2; i < 8; i++)
        {
            authority = (authority << 8) | bytes[i];
        }

        string authorityText;
        if (authority <= uint.MaxValue)
        {
            authorityText = authority.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            authorityText = "0x" + authority.ToString("x12", CultureInfo.InvariantCulture);
        }

        var sid = "S-1-" + authorityText;

        // Each remaining four-byte number becomes another section of the SID text.
        for (var i = 0; i < bytes[1]; i++)
        {
            var offset = 8 + i * 4;
            var section = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset, 4));
            sid += "-" + section.ToString(CultureInfo.InvariantCulture);
        }

        return sid;
    }
    /// <summary>
    /// Reads all text values from an LDAP field.
    /// </summary>
    private static string[] Strings(DirectoryAttribute attribute)
    {
        return attribute.GetValues(typeof(string)).Cast<string>().ToArray();
    }

    /// <summary>
    /// Reads the first text value of a field, or null when no value is available.
    /// </summary>
    private static string? Text(DirectoryLdapEntry entry, string name)
    {
        var attribute = Attribute(entry, name);
        if (attribute is null)
        {
            return null;
        }

        return Strings(attribute).FirstOrDefault();
    }
    /// <summary>
    /// Reads the unique object ID and checks that it contains exactly 16 bytes.
    /// </summary>
    private static Guid ObjectGuid(DirectoryLdapEntry entry)
    {
        var attribute = Attribute(entry, "objectGUID");
        if (attribute?.Count != 1 || attribute.GetValues(typeof(byte[]))[0] is not byte[] { Length: 16 } bytes)
            throw new InvalidOperationException("AD objectGUID was not readable.");
        return new Guid(bytes);
    }
}
