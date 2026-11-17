using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;

namespace CloneDeploy_Server.Backend.Services;

public enum WinPeSettingsSection { General, Configuration, ModelMatching, Drivers }

/// <summary>
/// Lets users manage Windows boot environments, build them, and prepare the required tools.
/// </summary>
public sealed class WinPeService(WinPeAdministration administration, WinPeSetupService setup, ILogger<WinPeService> logger)
{
    public Task<ServiceResult<WinPeCatalogDto>> GetAsync() => ExecuteAsync(() => administration.GetAsync());

    public Task<ServiceResult<int>> SaveAsync(WinPeDefinition definition, IEnumerable<int> driverIds) =>
        ExecuteAsync(() => administration.SaveAsync(definition, driverIds));

    public Task<ServiceResult<int>> SaveSectionAsync(WinPeDefinition definition, IEnumerable<int> driverIds, WinPeSettingsSection section) =>
        ExecuteAsync(() => administration.SaveSectionAsync(definition, driverIds, section));

    public Task<ServiceResult> DeleteAsync(int definitionId) => ExecuteAsync(() => administration.DeleteAsync(definitionId));

    public Task<ServiceResult<List<InventoriedModelDto>>> GetModelsAsync() => ExecuteAsync(() => administration.GetModelsAsync());

    public Task<ServiceResult<WinPeModelSelectionDto>> PreviewModelAsync(string? manufacturer, string? model) =>
        ExecuteAsync(() => administration.PreviewModelAsync(manufacturer, model));

    public Task<ServiceResult<Guid>> QueueAsync(int definitionId) => ExecuteAsync(() => administration.QueueAsync(definitionId));

    public Task<ServiceResult> CancelAsync(Guid buildId) => ExecuteAsync(() => administration.CancelAsync(buildId));

    public Task<ServiceResult> RetryStorageAsync(Guid buildId) => ExecuteAsync(() => administration.RetryStorageAsync(buildId));

    public Task<ServiceResult<SetupCatalogDto>> GetSetupAsync() => ExecuteAsync(() => setup.GetAsync());

    public Task<ServiceResult<Guid>> QueueSetupAsync(bool acceptLicense) =>
        ExecuteAsync(() => setup.QueueAsync(acceptLicense));

    public static ServiceResult<List<WinPeModelRuleDto>> ReadModelRules(string json)
    {
        try { return ServiceResult<List<WinPeModelRuleDto>>.Ok(WinPeModelMatching.Read(json)); }
        catch (InvalidOperationException exception)
        { return ServiceResult<List<WinPeModelRuleDto>>.Fail(ErrorKeys.WinPe.Validation, exception.Message); }
    }

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "WinPe.Failed", null, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "WinPe.Failed", null, operationName);
}
