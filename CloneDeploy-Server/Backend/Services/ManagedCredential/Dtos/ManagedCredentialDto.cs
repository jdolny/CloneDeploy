namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the saved credential details shown when viewing or editing a credential.
/// </summary>
public sealed class ManagedCredentialDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public Guid Guid { get; set; }
}