namespace CloneDeploy_Server.Backend.Services;

// Notify only: this asks an authenticated connected agent to fetch tasks; it never sends an
// executable payload or treats successful notification as successful execution.
public interface IDeviceTaskNotifier
{
    Task<bool> NotifyAsync(Guid deviceGuid, CancellationToken cancellationToken);
}