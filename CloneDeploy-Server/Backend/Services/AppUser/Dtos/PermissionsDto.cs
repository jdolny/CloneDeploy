namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the permissions selected for a user or user group.
/// </summary>
public class PermissionsDto
{
    public HashSet<string> Selected { get; set; } = [];
}
