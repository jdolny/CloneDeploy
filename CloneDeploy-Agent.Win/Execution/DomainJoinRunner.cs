using System.ComponentModel;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Runs a domain join using run-scoped credentials, without exposing passwords to a command line.</summary>
public sealed class DomainJoinRunner(IDomainJoinMachine machine)
{
    private const int MaximumDnsNameCharacters = 253;

    public Func<string> ComputerName { get; init; } = () => Environment.MachineName;
    public async Task<PolicyProcessResult> RunAsync(
        JsonElement settings,
        HttpClient client,
        string runPath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var domain = settings.Text("DomainName").Trim();

        if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Length > MaximumDnsNameCharacters)
            throw new InvalidDataException("Invalid domain DNS name.");

        var current = machine.GetDomain();

        if (current is not null)
            return string.Equals(current, domain, StringComparison.OrdinalIgnoreCase)
                ? new(0, $"Already joined to {domain}; no changes made.", "")
                : new(1, "", $"Already joined to {current}; switching domains is not supported.");

        AgentPolicyCredential credential;
        var ou = settings.Text("OrganizationalUnit").Trim();
        var detail = "";
        if (settings.Number("OuMode") == (int)DomainJoinOuMode.NamingRules)
        {
            if (!Guid.TryParse(settings.Text("DomainJoinActionGuid"), out var actionGuid)) throw new InvalidDataException("Missing domain-join action identity.");
            var name = ComputerName();
            if (ComputerNameValidation.Error(name) is { } nameError) return new(1, "", nameError);
            using var response = await client.PostAsJsonAsync($"{runPath}/domain-join/{actionGuid}", new AgentDomainJoinRequest(name), ct);
            response.EnsureSuccessStatusCode();
            var plan = await response.Content.ReadFromJsonAsync<AgentDomainJoinPlan>(ct) ?? throw new InvalidDataException("Missing domain-join plan.");
            if (!plan.Success) return new(1, "", plan.Message);
            credential = plan.Credential ?? throw new InvalidDataException("Missing domain-join credential.");
            ou = plan.OrganizationalUnit ?? "";
            detail = $" {plan.Message} OU: {plan.OrganizationalUnit ?? "domain default / existing account location"}.";
        }
        else
        {
            var credentialId = settings.Number("ManagedCredentialId");
            if (credentialId <= 0) throw new InvalidDataException("Missing domain-join credential. OU naming rules require an updated agent.");
            credential = await client.GetFromJsonAsync<AgentPolicyCredential>($"{runPath}/credentials/{credentialId}", ct)
                ?? throw new InvalidDataException("Missing run credential.");
        }
        ct.ThrowIfCancellationRequested();
        // Windows cannot cancel a join RPC safely. Wait for its outcome once started.
        var code = await Task.Run(
            () => machine.Join(domain, ou.Length == 0 ? null : ou, credential),
            CancellationToken.None);

        return code == 0
            ? new(ProcessExitCodes.RebootRequired, $"Joined {domain}.{detail} Restart required to complete domain membership.", "")
            : new(code, "", $"Domain join failed ({code}): {new Win32Exception(code).Message}");
    }
}

/// <summary>Separates machine membership changes from orchestration so tests never join the host.</summary>
public interface IDomainJoinMachine
{
    string? GetDomain();

    int Join(string domain, string? organizationalUnit, AgentPolicyCredential credential);
}

/// <summary>Queries DNS domain membership and joins through the Windows NetAPI.</summary>
public sealed class WindowsDomainJoinMachine : IDomainJoinMachine
{
    private const uint JoinDomain = 1;
    private const uint CreateAccount = 2;
    private const int BasicDomainInformation = 1;

    public string? GetDomain()
    {
        var code = DsRoleGetPrimaryDomainInformation(null, BasicDomainInformation, out var buffer);

        if (code != 0)
            throw new Win32Exception(code);

        try
        {
            var info = Marshal.PtrToStructure<DomainInformation>(buffer);

            if (info.Role >= 4)
                throw new InvalidOperationException("Domain-join actions cannot run on domain controllers.");

            if (info.Role is not (1 or 3))
                return null;

            return Marshal.PtrToStringUni(info.DnsName)
                   ?? throw new InvalidOperationException("Unable to determine the joined DNS domain.");
        }
        finally
        {
            DsRoleFreeMemory(buffer);
        }
    }

    public int Join(string domain, string? organizationalUnit, AgentPolicyCredential credential) =>
        NetJoinDomain(
            null,
            domain,
            organizationalUnit,
            credential.Username,
            credential.Password,
            JoinDomain | CreateAccount);

    [StructLayout(LayoutKind.Sequential)]
    private struct DomainInformation
    {
        public int Role;
        public uint Flags;
        public IntPtr FlatName, DnsName, ForestName;
        public Guid DomainGuid;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetJoinDomain(
        string? server,
        string domain,
        string? ou,
        string account,
        string password,
        uint options);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DsRoleGetPrimaryDomainInformation(string? server, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern void DsRoleFreeMemory(IntPtr buffer);
}
