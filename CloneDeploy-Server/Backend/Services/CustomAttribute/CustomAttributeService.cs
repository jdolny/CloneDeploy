using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Lets users manage custom fields for storing additional device information.
/// </summary>
public sealed class CustomAttributeService(CustomAttributeAdministration administration, ILogger<CustomAttributeService> logger)
{
    public Task<ServiceResult<List<CustomAttribute>>> GetAsync(string? searchText = null) =>
        ExecuteAsync(async () => ServiceResult<List<CustomAttribute>>.Ok(await administration.GetAsync(searchText)));

    public Task<ServiceResult<CustomAttribute>> CreateAsync(CustomAttribute attribute) =>
        ExecuteAsync(() => administration.CreateAsync(attribute));

    public Task<ServiceResult<CustomAttribute>> UpdateAsync(CustomAttribute attribute) =>
        ExecuteAsync(() => administration.UpdateAsync(attribute));

    public Task<ServiceResult> DeleteAsync(int id) => ExecuteAsync(() => administration.DeleteAsync(id));

    public Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids) => ExecuteAsync(() => administration.DeleteManyAsync(ids));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, ErrorKeys.CustomAttribute.Failed, MapError, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, ErrorKeys.CustomAttribute.Failed, MapError, operationName);

    private static ServiceError? MapError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => new("SharedResource.Forbidden", []),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
            new(ErrorKeys.CustomAttribute.Conflict, []),
        _ => null
    };
}
