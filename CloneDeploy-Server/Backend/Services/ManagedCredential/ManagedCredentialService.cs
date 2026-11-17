using Npgsql;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;

namespace CloneDeploy_Server.Backend.Services;

// Single UI entry point for managed credential administration.
/// <summary>
/// Lets users manage saved credentials for use by the application.
/// </summary>
public sealed class ManagedCredentialService(ManagedCredentialAdministration credentials, ILogger<ManagedCredentialService> logger, SecurityActivity? security = null)
{
    public Task<ServiceResult<List<ManagedCredentialDto>>> GetAsync(string? searchText = null) =>
        ExecuteAsync(() => credentials.GetAsync(searchText));

    public Task<ServiceResult<ManagedCredentialDto>> CreateAsync(ManagedCredentialDto input, string password) =>
        ExecuteAsync(() => credentials.CreateAsync(input, password));

    public Task<ServiceResult<ManagedCredentialDto>> UpdateAsync(ManagedCredentialDto input, string? password) =>
        ExecuteAsync(() => credentials.UpdateAsync(input, password));

    public Task<ServiceResult> DeleteAsync(int id) => ExecuteAsync(() => credentials.DeleteAsync(id));

    public Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids) => ExecuteAsync(() => credentials.DeleteManyAsync(ids));

    private async Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "")
    {
        var result = await ServiceOperations.RunAsync(operation, logger, "ManagedCredential.Failed", MapError, operationName);
        if (result.Success && security is not null && operationName is nameof(DeleteAsync) or nameof(DeleteManyAsync))
            await security.ChangeAsync("Managed credentials deleted", "Managed credentials");
        return result;
    }

    private async Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "")
    {
        var result = await ServiceOperations.RunAsync(operation, logger, "ManagedCredential.Failed", MapError, operationName);
        if (result.Success && security is not null && operationName is nameof(CreateAsync) or nameof(UpdateAsync))
            await security.ChangeAsync(operationName == nameof(CreateAsync) ? "Managed credential created" : "Managed credential changed",
                result.Data is ManagedCredentialDto value ? $"Credential #{value.Id}: {value.Name}" : "Managed credentials");
        return result;
    }

    private static ServiceError? MapError(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => new("ManagedCredential.Validation", ["This name is already in use. Refresh and try again."]),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } => new("ManagedCredential.InUse", []),
        _ => null
    };
}
