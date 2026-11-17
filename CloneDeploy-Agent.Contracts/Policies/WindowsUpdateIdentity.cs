namespace CloneDeploy_Agent.Contracts;

/// <summary>Identifies one exact revision of a Windows update.</summary>
public sealed record WindowsUpdateIdentity(Guid UpdateId, int RevisionNumber);
