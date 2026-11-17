using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public interface IActiveDirectoryReader
{
    Task<DirectorySnapshotDto> ReadAsync(ActiveDirectorySettings settings, string username, string password,
        CancellationToken cancellationToken);
}
