using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Common;

internal static class ServiceHttpResults
{
    public static IActionResult ToHttp<T>(this ControllerBase controller, ServiceResult<T> result) =>
        result.Success ? result.Data is null ? controller.NoContent() : controller.Ok(result.Data) : controller.ServiceError(result);

    public static IActionResult ToHttp(this ControllerBase controller, ServiceResult result) =>
        result.Success ? controller.NoContent() : controller.ServiceError(result);

    public static IActionResult ServiceError(this ControllerBase controller, ServiceResult result)
    {
        var detail = result.Error?.Args.FirstOrDefault() as string;
        switch (result.Error?.Code)
        {
            case ServiceErrorCodes.Invalid: return detail is null ? controller.BadRequest() : controller.BadRequest(detail);
            case ServiceErrorCodes.NotFound: return controller.NotFound();
            case ServiceErrorCodes.Forbidden: return controller.Forbid();
            case ServiceErrorCodes.Unauthorized: return controller.Unauthorized();
            case ServiceErrorCodes.Conflict: return detail is null ? controller.Conflict() : controller.Conflict(detail);
            case ServiceErrorCodes.Busy:
                controller.Response.Headers.RetryAfter = Random.Shared.Next(ControllerRetryLimits.MinDelaySeconds, ControllerRetryLimits.MaxDelaySeconds + 1).ToString();
                return controller.StatusCode(StatusCodes.Status429TooManyRequests);
            case ServiceErrorCodes.Unavailable: return controller.StatusCode(StatusCodes.Status503ServiceUnavailable, detail ?? "Storage is unavailable.");
            default: return controller.Problem(statusCode: StatusCodes.Status500InternalServerError, detail: "The operation could not be completed.");
        }
    }
}
