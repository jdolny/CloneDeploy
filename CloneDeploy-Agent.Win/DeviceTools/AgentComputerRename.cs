using System.ComponentModel;
using System.Runtime.InteropServices;
using CloneDeploy_Agent.Contracts;
using Microsoft.Win32;

namespace CloneDeploy_Agent;

public static class AgentComputerRename
{
    private const int PhysicalDnsHostname = 5;
    private const uint RenameDomainAccount = 2;
    public static AgentDeviceToolReply Execute(AgentDeviceToolRequest request)
    {
        if (ComputerNameValidation.Error(request.ComputerName) is { } error)
            return AgentDeviceToolReply.Result(request.RequestId, false, error);
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName");
        var pendingName = key?.GetValue("ComputerName") as string;
        return Execute(request, new WindowsDomainJoinMachine().GetDomain(), Environment.MachineName, pendingName,
            (name, credential) => credential is null
                ? (SetComputerNameEx(PhysicalDnsHostname, name) ? 0 : Marshal.GetLastWin32Error())
                : NetRenameMachineInDomain(null, name, credential.Username, credential.Password, RenameDomainAccount));
    }

    public static AgentDeviceToolReply Execute(AgentDeviceToolRequest request, string? domain, string currentName,
        string? pendingName, Func<string, AgentPolicyCredential?, int> rename)
    {
        if (ComputerNameValidation.Error(request.ComputerName) is { } error)
            return AgentDeviceToolReply.Result(request.RequestId, false, error);
        if (string.Equals(pendingName, request.ComputerName, StringComparison.OrdinalIgnoreCase))
            return AgentDeviceToolReply.Result(request.RequestId, true,
                string.Equals(currentName, request.ComputerName, StringComparison.OrdinalIgnoreCase)
                    ? "Windows already has this computer name." : "This computer name is already set. Restart Windows to apply it.");
        if (domain is not null && (string.IsNullOrWhiteSpace(request.DomainCredential?.Username) || string.IsNullOrEmpty(request.DomainCredential.Password)))
            return AgentDeviceToolReply.Result(request.RequestId, false, "Select a managed credential authorized to rename this domain computer.");

        var code = rename(request.ComputerName!, domain is null ? null : request.DomainCredential);
        return AgentDeviceToolReply.Result(request.RequestId, code == 0, code == 0
            ? "Windows computer name changed. Restart required; no automatic restart was requested."
            : $"Windows rename failed ({code}): {new Win32Exception(code).Message}");
    }

    [DllImport("kernel32.dll", EntryPoint = "SetComputerNameExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetComputerNameEx(int type, string name);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetRenameMachineInDomain(string? server, string name, string? account, string? password, uint options);
}
