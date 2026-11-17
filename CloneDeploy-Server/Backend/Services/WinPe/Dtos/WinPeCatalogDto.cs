using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.WinPe;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WinPeCatalogDto(List<WinPeDefinition> Definitions, List<WinPeBuildRowDto> Builds, List<DriverPack> Drivers, BuilderHealthDto? Builder);
