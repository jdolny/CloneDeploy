using System.Net;
using System.Text.RegularExpressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Agent.Contracts;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Web UI entry point for HelpDesk operations.</summary>
public sealed class HelpDeskUiService(
    HelpDeskService tickets,
    HelpRoutingService routing,
    HelpDeskReportService reports,
    ILogger<HelpDeskUiService> logger)
{
    public Task<HelpDeskService.Access> AccessAsync(ClaimsPrincipal user) =>
        tickets.AccessAsync(user);

    public Task<HelpDeskService.Overview> OverviewAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        tickets.OverviewAsync(user, ct);

    public Task<HelpResponse> ListRoutedAsync(ClaimsPrincipal user, string? search, HelpTicketStatus? status, int? assignedTo, int offset, bool openOnly, int? queueId, int? categoryId, bool myQueues, CancellationToken ct = default) =>
        tickets.ListRoutedAsync(user, search, status, assignedTo, offset, openOnly, queueId, categoryId, myQueues, ct);

    public Task<HelpResponse> ListFilteredAsync(ClaimsPrincipal user, string? search, HelpTicketListFilter filter,
        int? assignedTo, int offset, bool myQueues, CancellationToken ct = default) =>
        tickets.ListFilteredAsync(user, search, filter, assignedTo, offset, false, myQueues, ct);

    public Task<(int Total, HelpTicketSummary[] Tickets)> DirectoryHistoryAsync(ClaimsPrincipal user, int source, Guid guid, int page, int size, CancellationToken ct) =>
        tickets.DirectoryHistoryAsync(user, source, guid, page, size, ct);

    public Task<(int Total, HelpTicketSummary[] Tickets)> DeviceHistoryAsync(ClaimsPrincipal user, int deviceId, int page, int size, CancellationToken ct) =>
        tickets.DeviceHistoryAsync(user, deviceId, page, size, ct);

    public Task<HelpResponse> ReadAsync(ClaimsPrincipal user, int id, long before = 0, CancellationToken ct = default) =>
        tickets.ReadAsync(user, id, before, ct);

    public Task<byte[]?> ScreenshotAsync(ClaimsPrincipal user, int id, long messageId, CancellationToken ct = default) =>
        tickets.ScreenshotAsync(user, id, messageId, ct);

    public Task<HelpRoutingSettings> RoutingOptionsAsync(ClaimsPrincipal user) =>
        tickets.RoutingOptionsAsync(user);

    public Task<HelpStaff[]> StaffAsync(ClaimsPrincipal user) =>
        tickets.StaffAsync(user);

    public Task UpdateTicketAsync(ClaimsPrincipal user, int id, HelpTicketStatus status, int? assignedTo, string? reply, Guid commandId, DateTime? expectedUpdatedAtUtc, HelpDeskService.RoutingChange? routing) =>
        tickets.UpdateTicketAsync(user, id, status, assignedTo, reply, commandId, expectedUpdatedAtUtc, routing);

    public Task<HelpRoutingSettings> ReadRoutingAsync(ClaimsPrincipal user) =>
        routing.ReadAsync(user);

    public Task SaveRoutingCategoryAsync(ClaimsPrincipal user, int id, string name) =>
        routing.SaveCategoryAsync(user, id, name);

    public Task SaveRoutingQueueAsync(ClaimsPrincipal user, int id, string name, int[] memberIds) =>
        routing.SaveQueueAsync(user, id, name, memberIds);

    public Task SaveRoutingQueueAsync(ClaimsPrincipal user, int id, string name, int[] memberIds, bool? emailEnabled) =>
        routing.SaveQueueAsync(user, id, name, memberIds, emailEnabled);

    public Task SaveRoutingRuleAsync(ClaimsPrincipal user, HelpRoutingRule input) =>
        routing.SaveRuleAsync(user, input);

    public Task DeleteRoutingAsync(ClaimsPrincipal user, string kind, int id) =>
        routing.DeleteAsync(user, kind, id);

    public Task<HelpRoutingResult> PreviewRoutingAsync(ClaimsPrincipal user, HelpRoutingInput input) =>
        routing.PreviewAsync(user, input);

    public Task<HelpDirectoryChoice[]> SearchRoutingDirectoryAsync(ClaimsPrincipal user, DirectoryObjectKind kind, string? search, CancellationToken ct) =>
        routing.SearchDirectoryAsync(user, kind, search, ct);

    public Task<HelpDirectoryChoice?> RoutingDirectoryChoiceAsync(ClaimsPrincipal user, string key) =>
        routing.DirectoryChoiceAsync(user, key);

    public Task<HelpRequesterReportRow[]> ReportRequestersAsync(ClaimsPrincipal user, HelpReportFilter filter, CancellationToken ct = default) =>
        reports.RequestersAsync(user, filter, ct);

    public Task<HelpReportOptions> ReportOptionsAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        reports.OptionsAsync(user, ct);

    public Task<HelpReportSummary> ReportSummaryAsync(ClaimsPrincipal user, HelpReportFilter f, CancellationToken ct = default) =>
        reports.SummaryAsync(user, f, ct);

    public Task<HelpReportPage> ReportPageAsync(ClaimsPrincipal user, HelpReportFilter f, int page, int size, CancellationToken ct = default) =>
        reports.PageAsync(user, f, page, size, ct);

    public Task<MemoryStream> ExportReportAsync(ClaimsPrincipal user, HelpReportFilter f, CancellationToken ct = default) =>
        reports.ExportAsync(user, f, ct);

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    public static HelpRuleCondition[] RoutingConditions(HelpRoutingRule rule) =>
        HelpRoutingEngine.Conditions(rule);

    public static bool IsDirectoryCondition(HelpRuleField field) =>
        HelpRoutingEngine.IsDirectory(field);

    public static bool IsOuCondition(HelpRuleField field) =>
        HelpRoutingEngine.IsOu(field);

    public static string RoutingRegexMatch(string sample, string pattern) =>
        HelpRoutingEngine.RegexMatch(sample, pattern);

    public static string StatusLabel(HelpTicketStatus status) =>
        UiText.Label(HelpDeskService.StatusLabel(status));
}
