using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Provides the application's navigation search results.
/// </summary>
public sealed class NavigationSearchService(NavigationSearchQueries queries, ILogger<NavigationSearchService> logger)
{
    public Task<ServiceResult<List<NavigationSearchResultDto>>> SearchAsync(string? text, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => queries.SearchAsync(text, cancellationToken));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "NavigationSearch.Failed", null, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "NavigationSearch.Failed", null, operationName);
}
