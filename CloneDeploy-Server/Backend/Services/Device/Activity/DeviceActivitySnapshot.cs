using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceActivitySnapshot(int DeviceId, bool Connected, string Text, DateTime? ChangedAtUtc)
{
    public string? DeviceName { get; init; }
    public string? ClientVersion { get; init; }
    public DateTime? LastCheckinAtUtc { get; init; }
    public string? LastKnownIp { get; init; }
    public static DeviceActivitySnapshot Create(int id, bool connected, string? connectionId,
        DeviceActivityReport? report, ActiveImagingTask? imaging, string? queuedName, DeviceTaskTiming? timing)
    {
        var fresh = connected && report?.ConnectionId == connectionId;
        var executing = fresh && report?.Kind is AgentActivityKind.RunningTask or AgentActivityKind.RunningPolicy;
        if (imaging is { FinishedAtUtc: null } && imaging.Status < ImagingStatus.Succeeded &&
            (imaging.Status >= ImagingStatus.CheckedIn || !executing))
        {
            var label = imaging.Status switch
            {
                ImagingStatus.Imaging => imaging.Type is "Capture" or "Upload" ? "Capturing image" : "Imaging",
                ImagingStatus.InImagingQueue => "Waiting in imaging queue",
                ImagingStatus.WaitingForLogin => "Waiting for imaging login",
                _ => "Waiting for imaging"
            };
            return new(id, connected, WithName(label, imaging.ImageName), imaging.StartedAtUtc);
        }
        if (report is not null && report.Kind != AgentActivityKind.Idle)
        {
            var label = report.Kind switch
            {
                AgentActivityKind.CheckingIn => "Checking in",
                AgentActivityKind.EvaluatingPolicies => "Checking policies",
                AgentActivityKind.CollectingInventory => "Collecting inventory",
                AgentActivityKind.RunningTask => "Running task",
                AgentActivityKind.RunningPolicy => "Running policy",
                _ => "Waiting for next check-in"
            };
            return new(id, connected, (fresh ? "" : "Last reported: ") + WithName(label, report.Name), report.ChangedAtUtc);
        }
        if (queuedName is not null)
            return new(id, connected, WithName(timing == DeviceTaskTiming.NextCheckin ? "Waiting for check-in" : "Task queued", queuedName), null);
        return new(id, connected, fresh ? "Waiting for next check-in" : report is not null
            ? "Last reported: Waiting for next check-in" : "Activity unavailable", report?.ChangedAtUtc);
    }

    private static string WithName(string state, string? name) => string.IsNullOrWhiteSpace(name) ? state : $"{state} · {name}";
}
