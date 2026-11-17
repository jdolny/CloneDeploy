using System.Runtime.CompilerServices;

namespace CloneDeploy_Server.Backend.Common;

internal static class ServiceOperations
{
    public static async Task<ServiceResult<T>> RunAsync<T>(Func<Task<ServiceResult<T>>> operation, ILogger logger,
        string errorCode = ServiceErrorCodes.Failed, Func<Exception, ServiceError?>? mapError = null,
        [CallerMemberName] string operationName = "")
    {
        try { return await operation(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = mapError?.Invoke(exception);
            if (error is not null) return ServiceResult<T>.Fail(error.Code, error.Args);
            logger.LogError(exception, "Service operation {Operation} failed", operationName);
            return ServiceResult<T>.Fail(errorCode);
        }
    }

    public static async Task<ServiceResult> RunAsync(Func<Task<ServiceResult>> operation, ILogger logger,
        string errorCode = ServiceErrorCodes.Failed, Func<Exception, ServiceError?>? mapError = null,
        [CallerMemberName] string operationName = "")
    {
        try { return await operation(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = mapError?.Invoke(exception);
            if (error is not null) return ServiceResult.Fail(error.Code, error.Args);
            logger.LogError(exception, "Service operation {Operation} failed", operationName);
            return ServiceResult.Fail(errorCode);
        }
    }
}
