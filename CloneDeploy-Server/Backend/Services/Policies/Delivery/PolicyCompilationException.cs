namespace CloneDeploy_Server.Backend.Services;

// Corrupt persisted snapshots must stop delivery; never treat them as an empty policy list.
/// <summary>
/// Describes a problem that prevents a policy from being prepared for devices.
/// </summary>
internal sealed class PolicyCompilationException(string message) : Exception(message);