namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Describes a problem that prevents an agent from enrolling.
/// </summary>
public sealed class AgentEnrollmentException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

