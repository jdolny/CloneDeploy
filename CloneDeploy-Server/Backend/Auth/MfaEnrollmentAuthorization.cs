using System.Security.Claims;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Auth;

// This only exempts enrollment; the page still requires an authenticated user.
[AttributeUsage(AttributeTargets.Class)]
public sealed class AllowMfaEnrollmentAttribute : Attribute;

public sealed class MfaEnrollmentRequirement : IAuthorizationRequirement;

public sealed class MfaEnrollmentHandler(IDbContextFactory<CloneDeployDbContext> factory)
    : AuthorizationHandler<MfaEnrollmentRequirement>
{
    public const string InvalidSession = "The account session is no longer valid.";

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MfaEnrollmentRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        if (MfaPolicy.IsEntraSession(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id);
        // A separate context avoids concurrent operations on the circuit's scoped Identity context.
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == id);
        var stamp = context.User.FindFirstValue("AspNet.Identity.SecurityStamp");
        if (user is null || string.IsNullOrEmpty(stamp) || stamp != user.SecurityStamp)
        {
            context.Fail(new AuthorizationFailureReason(this, InvalidSession));
            return;
        }

        var pageType = context.Resource switch
        {
            Microsoft.AspNetCore.Components.RouteData route => route.PageType,
            HttpContext http => http.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type,
            _ => null
        };
        if (pageType?.IsDefined(typeof(AllowMfaEnrollmentAttribute), inherit: true) == true)
        {
            context.Succeed(requirement);
            return;
        }

        if (!await MfaPolicy.RequiresWebAsync(db, user) || user.TwoFactorEnabled)
            context.Succeed(requirement);
    }
}

public sealed class MfaAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        var isPage = context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null;
        if (result.Challenged && !isPage)
        {
            // A response body also prevents status-code pages from rewriting API failures as HTML.
            await Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                detail: "Authentication required.").ExecuteAsync(context);
            return;
        }

        if (result.Forbidden)
        {
            var invalidSession = result.AuthorizationFailure?.FailureReasons
                .Any(reason => reason.Message == MfaEnrollmentHandler.InvalidSession) == true;
            var needsEnrollment = result.AuthorizationFailure?.FailedRequirements
                .OfType<MfaEnrollmentRequirement>().Any() == true;
            if (isPage)
            {
                var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                var destination = invalidSession
                    ? $"/login?error=session-expired&returnUrl={Uri.EscapeDataString(returnUrl)}"
                    : needsEnrollment
                    ? $"/account/mfa?returnUrl={Uri.EscapeDataString(returnUrl)}"
                    : "/access-denied";
                context.Response.Redirect(context.Request.PathBase + destination);
            }
            else
            {
                await Results.Problem(statusCode: invalidSession ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden,
                    detail: invalidSession ? "Authentication required." : needsEnrollment ? "MFA enrollment is required." : "Access denied.")
                    .ExecuteAsync(context);
            }
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, result);
    }
}
