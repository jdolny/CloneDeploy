using System.Diagnostics.CodeAnalysis;

namespace CloneDeploy_Server.Backend.Common;

public class ServiceResult
{
    [MemberNotNullWhen(false, nameof(Error))]
    public bool Success { get; set; }
    public ServiceError? Error { get; set; }

    public static ServiceResult Ok() => new()
    {
        Success = true
    };

    public static ServiceResult Fail(string errorCode, params object[] args) => new()
    {
        Success = false,
        Error = new ServiceError(errorCode, args)
    };
}

public class ServiceResult<T> : ServiceResult
{
    public T? Data { get; set; }
    [MemberNotNullWhen(true, nameof(Data))]
    public bool HasData => Success && Data is not null;
    public static ServiceResult<T> Ok(T data) => new()
    {
        Success = true,
        Data = data
    };

    public new static ServiceResult<T> Fail(string errorCode, params object[] args) => new()
    {
        Success = false,
        Error = new ServiceError(errorCode, args)
    };
}


public record ServiceError(string Code, object[] Args);