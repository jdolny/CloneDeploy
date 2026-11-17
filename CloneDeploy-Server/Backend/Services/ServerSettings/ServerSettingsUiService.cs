using CloneDeploy_Server.WinPe;
using System.Security.Claims;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Agent.Contracts;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Web UI entry point for ServerSettings operations.</summary>
public sealed class ServerSettingsUiService(ServerSettingsService settings,
    ILogger<ServerSettingsUiService> logger)
{
    public Task<ServiceResult<ServerFeatures>> ReadServerFeaturesAsync() =>
        ExecuteAsync(() => settings.ReadServerFeaturesAsync());

    public Task<ServiceResult<ServerFeatures>> SaveServerFeaturesAsync(ServerFeatures value, ServerFeatures expected) =>
        ExecuteAsync(() => settings.SaveServerFeaturesAsync(value, expected));

    public Task<ServiceResult> CheckPortalDirectoryAsync() =>
        ExecuteAsync(() => settings.CheckPortalDirectoryAsync());

    public Task<ServiceResult<AgentFeatureConfiguration>> ReadFeaturesAsync() =>
        ExecuteAsync(() => settings.ReadFeaturesAsync());

    public Task<ServiceResult<AgentFeatureConfiguration>> SaveFeaturesAsync(AgentFeatures features, string expectedVersion, bool acknowledgeWinGet) =>
        ExecuteAsync(() => settings.SaveFeaturesAsync(features, expectedVersion, acknowledgeWinGet));

    public Task<ServiceResult<AgentBranding>> ReadBrandingAsync() =>
        ExecuteAsync(() => settings.ReadBrandingAsync());

    public Task<ServiceResult<AgentBranding>> SaveBrandingAsync(AgentBranding branding, string expectedVersion) =>
        ExecuteAsync(() => settings.SaveBrandingAsync(branding, expectedVersion));

    public Task<ServiceResult<DeploymentPerformanceOptions>> ReadDeploymentPerformanceAsync() =>
        ExecuteAsync(() => settings.ReadDeploymentPerformanceAsync());

    public Task<ServiceResult<DeploymentPerformanceOptions>> SaveDeploymentPerformanceAsync(DeploymentPerformanceOptions value, DeploymentPerformanceOptions expected) =>
        ExecuteAsync(() => settings.SaveDeploymentPerformanceAsync(value, expected));

    public Task<ServiceResult<string>> SaveGeneralAsync(string url, string? bootOverride, string? expectedUrl, string? expectedBootOverride, string? mainServerAddress, string? expectedMainServerAddress, ServerFeatures? features = null, ServerFeatures? expectedFeatures = null) =>
        ExecuteAsync(() => settings.SaveGeneralAsync(url, bootOverride, expectedUrl, expectedBootOverride, mainServerAddress, expectedMainServerAddress, features, expectedFeatures));

    public Task<ServiceResult<string?>> ReadMainServerAddressAsync() =>
        ExecuteAsync(() => settings.ReadMainServerAddressAsync());

    public Task<ServiceResult<int>> ReadDeploymentLimitAsync() =>
        ExecuteAsync(() => settings.ReadDeploymentLimitAsync());

    public Task<ServiceResult<int>> SaveDeploymentLimitAsync(int limit, int expected) =>
        ExecuteAsync(() => settings.SaveDeploymentLimitAsync(limit, expected));

    public Task<ServiceResult<string>> ReadMulticastSenderAddressAsync() =>
        ExecuteAsync(() => settings.ReadMulticastSenderAddressAsync());

    public Task<ServiceResult<string>> SaveMulticastSenderAddressAsync(string? address, string expected) =>
        ExecuteAsync(() => settings.SaveMulticastSenderAddressAsync(address, expected));

    public Task<ServiceResult<string?>> ReadAsync() =>
        ExecuteAsync(() => settings.ReadAsync());

    public Task<ServiceResult<string?>> ReadBootOverrideAsync() =>
        ExecuteAsync(() => settings.ReadBootOverrideAsync());

    public Task<ServiceResult<string?>> ReadBootUrlAsync() =>
        ExecuteAsync(() => settings.ReadBootUrlAsync());

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    public static string? DeriveBootUrl(string? publicUrl) =>
        ServerUrlSettingsStore.DeriveBootUrl(publicUrl);

    public static string? NormalizeBootUrl(string? value) =>
        ServerUrlSettingsStore.NormalizeBootUrl(value);

    public static string WinGetAcknowledgment => AgentFeatureSettings.Acknowledgment;

    public const int MaximumIconBytes = PortalIcon.MaximumBytes;
    public static bool IsValidIcon(byte[] data) => PortalIcon.IsValid(data);
}
