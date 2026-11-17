using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Reads live device status, services and processes, and performs requested administrative changes.</summary>
public sealed class AgentDeviceTools
{
    private const int CompletedRequestLimit = 100;
    private const int MaximumPageOffset = 100_000;
    private const int SystemProcessId = 4;
    private const int MaximumErrorLength = 512;
    private const int MaximumServiceNameLength = 256;
    private const int MaximumDisplayTextLength = 256;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ServiceTransitionTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<Guid, AgentDeviceToolReply> completed = new();
    private readonly Func<AgentDeviceToolRequest, AgentDeviceToolReply> power;

    public AgentDeviceTools() : this(AgentPowerControl.Schedule) { }
    internal AgentDeviceTools(Func<AgentDeviceToolRequest, AgentDeviceToolReply> power) => this.power = power;

    public async Task<AgentDeviceToolReply> ExecuteAsync(AgentDeviceToolRequest request, Guid deviceGuid)
    {
        if (request.DeviceGuid != deviceGuid || request.RequestId == Guid.Empty
                                             ||
                                             request.ExpiresAtUtc <= DateTimeOffset.UtcNow
                                             || request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            return AgentDeviceToolReply.Result(
                request.RequestId,
                false,
                "The request is expired or belongs to another device.");

        if (!await gate.WaitAsync(0))
            return AgentDeviceToolReply.Result(
                request.RequestId,
                false,
                "Another device tool operation is running. Try again shortly.");

        try
        {
            if (completed.TryGetValue(request.RequestId, out var previous))
                return previous;

            var reply = await Task.Run(() => Execute(request));

            if (request.Operation is not (AgentDeviceOperation.Services or AgentDeviceOperation.Processes or AgentDeviceOperation.SystemStatus))
            {
                if (completed.Count >= CompletedRequestLimit)
                    completed.Remove(completed.Keys.First());

                completed[request.RequestId] = reply;
            }

            return reply;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
                                       or ManagementException or System.ServiceProcess.TimeoutException or UnauthorizedAccessException)
        {
            return AgentDeviceToolReply.Result(
                request.RequestId,
                false,
                ex.Message.Length > MaximumErrorLength ? ex.Message[..MaximumErrorLength] : ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private AgentDeviceToolReply Execute(AgentDeviceToolRequest request)
    {
        if (request.Operation == AgentDeviceOperation.RenameComputer)
        {
            if (!AgentExecutionGate.Gate.Wait(0))
                return AgentDeviceToolReply.Result(request.RequestId, false, "The agent is busy. Retry the rename when its current operation finishes.");
            try { return AgentComputerRename.Execute(request); }
            finally { AgentExecutionGate.Gate.Release(); }
        }
        if (request.Offset < 0 || request.Offset > MaximumPageOffset || !Enum.IsDefined(request.Operation))
            return AgentDeviceToolReply.Result(request.RequestId, false, "Invalid device tool request.");

        if (request.Operation is AgentDeviceOperation.Reboot or AgentDeviceOperation.Shutdown)
            return power(request);
        if (request.Operation == AgentDeviceOperation.SystemStatus)
        {
            string[] users = [];
            string? usersError = null;
            try { users = WindowsLoggedInUsers.Read().Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (System.ComponentModel.Win32Exception) { usersError = "Windows could not enumerate logged-on users."; }
            return AgentDeviceToolReply.Result(request.RequestId, true, "") with
            {
                SystemStatus = new(DateTimeOffset.UtcNow, Environment.TickCount64 / 1000, users, usersError)
            };
        }

        if (request.Operation == AgentDeviceOperation.Services)
        {
            using var search = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode FROM Win32_Service");
            search.Options.Timeout = QueryTimeout;
            using var results = search.Get();
            var rows = new List<AgentServiceRow>();

            foreach (ManagementObject item in results)
                using (item)
                    rows.Add(new(
                        Text(item["Name"]),
                        Text(item["DisplayName"]),
                        Text(item["State"]),
                        Text(item["StartMode"])));

            var page = rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Skip(request.Offset).Take(AgentDeviceToolReply.PageSize + 1).ToArray();

            return new(
                request.RequestId,
                true,
                "",
                page.Take(AgentDeviceToolReply.PageSize).ToArray(),
                [],
                page.Length > AgentDeviceToolReply.PageSize);
        }

        if (request.Operation == AgentDeviceOperation.Processes)
        {
            var rows = new List<AgentProcessRow>();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var name = process.ProcessName;
                        long? started = null, memory = null;
                        double? cpu = null;

                        try
                        {
                            started = process.StartTime.ToUniversalTime().Ticks;
                            memory = process.WorkingSet64;
                            cpu = process.TotalProcessorTime.TotalSeconds;
                        }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                        {
                        }

                        rows.Add(new(process.Id, Text(name), started, cpu, memory));
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                    }
                }
            }

            var page = rows.OrderBy(x => x.Id).Skip(request.Offset).Take(AgentDeviceToolReply.PageSize + 1).ToArray();

            return new(
                request.RequestId,
                true,
                "",
                [],
                page.Take(AgentDeviceToolReply.PageSize).ToArray(),
                page.Length > AgentDeviceToolReply.PageSize);
        }

        if (request.Operation == AgentDeviceOperation.EndProcess)
        {
            if (request.ProcessId <= SystemProcessId || request.ProcessId == Environment.ProcessId
                || request.ProcessStartedUtcTicks <= 0)
                return AgentDeviceToolReply.Result(
                    request.RequestId,
                    false,
                    "This process cannot be ended from the connection panel.");

            using var process = Process.GetProcessById(request.ProcessId);

            if (process.StartTime.ToUniversalTime().Ticks != request.ProcessStartedUtcTicks)
                return AgentDeviceToolReply.Result(
                    request.RequestId,
                    false,
                    "The process has changed. Refresh the list before trying again.");

            process.Kill();

            return AgentDeviceToolReply.Result(request.RequestId, true, "Process termination requested.");
        }

        if (string.IsNullOrWhiteSpace(request.ServiceName)
            || request.ServiceName.Length > MaximumServiceNameLength
            ||
            request.ServiceName.Equals(AgentOptions.WindowsServiceName, StringComparison.OrdinalIgnoreCase))
            return AgentDeviceToolReply.Result(
                request.RequestId,
                false,
                "This service cannot be controlled from the connection panel.");

        using var service = new ServiceController(request.ServiceName);

        if (request.Operation is AgentDeviceOperation.StopService or AgentDeviceOperation.RestartService
            && service.Status != ServiceControllerStatus.Stopped)
        {
            service.Stop(stopDependentServices: false);
            service.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTransitionTimeout);
        }

        if (request.Operation is AgentDeviceOperation.StartService or AgentDeviceOperation.RestartService
            && service.Status != ServiceControllerStatus.Running)
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, ServiceTransitionTimeout);
        }

        return AgentDeviceToolReply.Result(request.RequestId, true, $"{request.ServiceName}: {service.Status}.");
    }

    private static string Text(object? value)
    {
        var text = value?.ToString() ?? "";

        return text.Length > MaximumDisplayTextLength ? text[..MaximumDisplayTextLength] : text;
    }
}
