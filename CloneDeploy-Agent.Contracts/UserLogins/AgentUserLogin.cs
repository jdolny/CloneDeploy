using System.ComponentModel.DataAnnotations;

namespace CloneDeploy_Agent.Contracts;

public sealed record AgentUserLogin(
    [Range(1, int.MaxValue)] int ClientDatabaseLoginId,
    [Required, StringLength(512)] string UserName,
    DateTime LoggedInAtUtc,
    DateTime? LoggedOutAtUtc);
