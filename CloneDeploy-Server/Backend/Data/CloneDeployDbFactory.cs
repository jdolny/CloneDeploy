using CloneDeploy_Server.Backend.Common;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Data;

public class CloneDeployDbFactory(IDbContextFactory<CloneDeployDbContext> factory) : ICloneDeployDbFactory
{
    public async Task<CloneDeployDbContext> CreateContextAsync()
    {
        var context = await factory.CreateDbContextAsync();
        return context;
    }
}