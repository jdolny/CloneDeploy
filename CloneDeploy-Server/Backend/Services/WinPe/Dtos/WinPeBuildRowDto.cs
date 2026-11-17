using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.WinPe;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WinPeBuildRowDto(WinPeBuild Build, BuildStatusDto Status);
