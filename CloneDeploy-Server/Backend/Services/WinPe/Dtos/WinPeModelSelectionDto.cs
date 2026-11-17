using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.WinPe;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WinPeModelSelectionDto(int DefinitionId, string Name, Guid BuildId, int Version);
