using System.Diagnostics;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

internal static class AgentPowerControl
{
    private const int ShutdownCommandTimeoutMilliseconds = 5000;

    internal static AgentDeviceToolReply Schedule(AgentDeviceToolRequest request) => Schedule(request, Run);

    internal static AgentDeviceToolReply Schedule(AgentDeviceToolRequest request, Func<ProcessStartInfo, int> run)
    {
        if (request.Operation is not (AgentDeviceOperation.Reboot or AgentDeviceOperation.Shutdown))
            return AgentDeviceToolReply.Result(request.RequestId, false, "Invalid power operation.");
        if (!AgentExecutionGate.Gate.Wait(0))
            return AgentDeviceToolReply.Result(request.RequestId, false, "The agent is busy. Retry when its current operation finishes.");
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { request.Operation == AgentDeviceOperation.Reboot ? "/r" : "/s", "/t", "30", "/d", "p:4:1", "/c", "Requested by a CloneDeploy administrator" })
                start.ArgumentList.Add(argument);
            var code = run(start);
            var action = request.Operation == AgentDeviceOperation.Reboot ? "Restart" : "Shutdown";
            return AgentDeviceToolReply.Result(request.RequestId, code == 0, code == 0
                ? $"{action} scheduled in 30 seconds. Unsaved work may be lost."
                : $"Windows rejected the {action.ToLowerInvariant()} request (code {code}).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return AgentDeviceToolReply.Result(request.RequestId, false, "The power request was not confirmed. Check the device before trying again.");
        }
        finally { AgentExecutionGate.Gate.Release(); }
    }

    private static int Run(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows power command.");
        if (!process.WaitForExit(ShutdownCommandTimeoutMilliseconds))
            throw new InvalidOperationException("Windows did not confirm the power request. It may still take effect.");
        return process.ExitCode;
    }
}
