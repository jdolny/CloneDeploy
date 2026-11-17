using Npgsql;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Lets users manage labels used to organize application records.
/// </summary>
public sealed class TagService(TagAdministration tags, ILogger<TagService> logger)
{
    public Task<ServiceResult<List<Tag>>> GetAsync(string? searchText = null) => ExecuteAsync(() => tags.GetAsync(searchText));
    public Task<ServiceResult<Tag>> CreateAsync(Tag tag) => ExecuteAsync(() => tags.CreateAsync(tag));
    public Task<ServiceResult<Tag>> UpdateAsync(Tag tag) => ExecuteAsync(() => tags.UpdateAsync(tag));
    public Task<ServiceResult> DeleteAsync(int id) => ExecuteAsync(() => tags.DeleteAsync(id));
    public Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids) => ExecuteAsync(() => tags.DeleteManyAsync(ids));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Tag.Failed", MapError, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Tag.Failed", MapError, operationName);

    private static ServiceError? MapError(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => new("Tag.Conflict", []),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } => new("Tag.InUse", []),
        _ => null
    };
}
