using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Backend.Data.Models;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Web UI entry point for Notifications operations.</summary>
public sealed class EmailNotificationUiService(EmailNotificationService notifications,
    ILogger<EmailNotificationUiService> logger)
{
    public Task<ServiceResult<SmtpSettingsForm>> ReadServerAsync() =>
        ExecuteAsync(() => notifications.ReadServerAsync());

    public Task<ServiceResult<bool>> SaveServerAsync(SmtpSettingsForm form) =>
        ExecuteAsync(() => notifications.SaveServerAsync(form));

    public Task<ServiceResult<bool>> TestServerAsync(string address) =>
        ExecuteAsync(() => notifications.TestServerAsync(address));

    public Task<ServiceResult<UserEmailSubscription>> ReadSubscriptionAsync(int userId) =>
        ExecuteAsync(() => notifications.ReadSubscriptionAsync(userId));

    public Task<ServiceResult<bool>> SaveSubscriptionAsync(UserEmailSubscription input) =>
        ExecuteAsync(() => notifications.SaveSubscriptionAsync(input));

    public Task<ServiceResult<bool>> SaveUserOptionsAsync(UserEmailSubscription input, AppUserOptions options) =>
        ExecuteAsync(() => notifications.SaveUserOptionsAsync(input, options));

    public Task<ServiceResult<List<EmailDigestDelivery>>> HistoryAsync(int userId) =>
        ExecuteAsync(() => notifications.HistoryAsync(userId));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);
}
