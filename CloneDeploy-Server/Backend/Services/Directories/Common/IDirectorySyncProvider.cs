using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

// Providers own their read protocol and persistence semantics (LDAP snapshot vs Graph delta).
public interface IDirectorySyncProvider
{
    DirectoryProvider Provider { get; }
    Task<DirectorySyncSummaryDto> SyncAsync(DirectorySource source, CloneDeployDbContext db, CancellationToken ct);
}

