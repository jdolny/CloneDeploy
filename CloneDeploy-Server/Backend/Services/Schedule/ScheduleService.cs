using Npgsql;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

// Single UI entry point. Background scheduling uses ScheduleCalculator directly.
/// <summary>
/// Lets users manage schedules and check when they will next run.
/// </summary>
public sealed class ScheduleService(ScheduleAdministration schedules, ScheduleCalculator calculator, ILogger<ScheduleService> logger)
{
    public Task<ServiceResult<List<Schedule>>> GetAsync(string? searchText = null) => ExecuteAsync(() => schedules.GetAsync(searchText));
    public Task<ServiceResult<List<Schedule>>> GetAllAsync() => ExecuteAsync(() => schedules.GetAsync());
    public Task<ServiceResult<Schedule>> CreateAsync(Schedule input) => ExecuteAsync(() => schedules.CreateAsync(input));
    public Task<ServiceResult<Schedule>> UpdateAsync(Schedule input) => ExecuteAsync(() => schedules.UpdateAsync(input));
    public Task<ServiceResult> DeleteAsync(int id) => ExecuteAsync(() => schedules.DeleteAsync(id));
    public Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids) => ExecuteAsync(() => schedules.DeleteManyAsync(ids));
    public bool IsValid(Schedule schedule) => calculator.IsValid(schedule);
    public DateTime? GetNextScheduledTime(Schedule schedule, DateTime afterUtc) => calculator.GetNextScheduledTime(schedule, afterUtc);

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Schedule.Failed", MapError, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Schedule.Failed", MapError, operationName);

    private static ServiceError? MapError(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => new("Schedule.Conflict", []),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } => new("Schedule.InUse", []),
        _ => null
    };
}
