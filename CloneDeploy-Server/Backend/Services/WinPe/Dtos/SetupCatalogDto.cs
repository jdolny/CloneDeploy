using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.WinPe;

namespace CloneDeploy_Server.Backend.Services;

public sealed record SetupCatalogDto(List<SetupJobRowDto> Jobs, SetupHealthDto? Builder);
