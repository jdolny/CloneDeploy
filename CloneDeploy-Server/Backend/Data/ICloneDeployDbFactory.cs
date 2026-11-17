using CloneDeploy_Server.Backend.Common;

namespace CloneDeploy_Server.Backend.Data;

public interface ICloneDeployDbFactory
{
    Task<CloneDeployDbContext> CreateContextAsync();
}