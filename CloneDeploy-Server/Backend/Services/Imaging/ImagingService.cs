using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Text.Json.Nodes;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Agent.Contracts;
using CloneDeploy.SiteServices;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

// Single UI entry point for images, deployment profiles, versions, and imaging deployments.
/// <summary>
/// Lets users manage images, image versions, deployment profiles, and imaging deployments.
/// </summary>
public sealed class ImagingService(
    ImageAdministration images,
    DeploymentProfileAdministration profiles,
    DeploymentProfileService settings,
    ImageVersionService versions,
    ImagingDeploymentService deployments,
    ILogger<ImagingService> logger,
    ImagingInspectionService inspection,
    ImagePrepProfileService imagePrep,
    OuNamingRuleService ouNaming,
    ImageReplicationAdministration replication,
    ImagingTaskService tasks)
{
    public Task<ServiceResult<List<Image>>> GetImagesAsync(string? search = null, bool includeDisabled = false) =>
        ExecuteAsync(() => images.GetImagesAsync(search, includeDisabled));

    public Task<ServiceResult<Image>> GetImageAsync(int id) =>
        ExecuteAsync(() => images.GetImageAsync(id));

    public Task<ServiceResult<List<Tag>>> GetTagsAsync() =>
        ExecuteAsync(() => images.GetTagsAsync());

    public Task<ServiceResult<Image>> CreateImageAsync(string name, string? description, ImageFormat format = ImageFormat.Wim) =>
        ExecuteAsync(() => images.CreateImageAsync(name, description, format));

    public Task<ServiceResult> SaveImageGeneralAsync(Image input) =>
        ExecuteAsync(() => images.SaveImageGeneralAsync(input));

    public Task<ServiceResult> SaveImageConfigurationAsync(Image input) =>
        ExecuteAsync(() => images.SaveImageConfigurationAsync(input));

    public Task<ServiceResult> DeleteImageAsync(int id) =>
        ExecuteAsync(() => images.DeleteImageAsync(id));

    public Task<ServiceResult<ImagingDeployment>> PrepareVersionAsync(int deviceId, int imageId, int profileId, Guid versionId) =>
        ExecuteAsync(() => deployments.PrepareVersionAsync(deviceId, imageId, profileId, versionId));

    public Task<ServiceResult> ApproveVersionAsync(int imageId, Guid versionId) =>
        ExecuteAsync(() => versions.ApproveAsync(imageId, versionId));

    public Task<ServiceResult> DeleteVersionAsync(int imageId, Guid versionId) =>
        ExecuteAsync(() => versions.DeleteAsync(imageId, versionId));

    public Task<ServiceResult> DeleteImagesAsync(IEnumerable<int> ids) =>
        ExecuteAsync(() => images.DeleteImagesAsync(ids));

    public Task<ServiceResult<List<AuditLog>>> GetAuditAsync(int imageId, int? profileId = null) =>
        ExecuteAsync(() => images.GetAuditAsync(imageId, profileId));

    public Task<ServiceResult<List<DeploymentProfile>>> GetProfilesAsync(int imageId, string? search = null, bool includeDisabled = true) =>
        ExecuteAsync(() => profiles.GetProfilesAsync(imageId, search, includeDisabled));

    public Task<ServiceResult<List<DeploymentProfile>>> GetAllProfilesAsync(string? search = null, bool includeDisabled = false) =>
        ExecuteAsync(() => profiles.GetAllProfilesAsync(search, includeDisabled));

    public Task<ServiceResult<DeploymentProfile>> GetProfileByIdAsync(int id) =>
        ExecuteAsync(() => profiles.GetProfileByIdAsync(id));

    public Task<ServiceResult<List<AuditLog>>> GetProfileAuditAsync(int id) =>
        ExecuteAsync(() => profiles.GetProfileAuditAsync(id));

    public Task<ServiceResult<DeploymentProfile>> GetProfileAsync(int imageId, int profileId) =>
        ExecuteAsync(() => profiles.GetProfileAsync(imageId, profileId));

    public Task<ServiceResult<DeploymentProfile>> CreateProfileAsync(int imageId, string name, string? description) =>
        ExecuteAsync(() => profiles.CreateProfileAsync(imageId, name, description));

    public Task<ServiceResult<DeploymentProfile>> CreateProfileAsync(string name, string? description) =>
        ExecuteAsync(() => profiles.CreateProfileAsync(name, description));

    public Task<ServiceResult> SaveProfileGeneralAsync(DeploymentProfile input) =>
        ExecuteAsync(() => profiles.SaveProfileGeneralAsync(input));

    public Task<ServiceResult> SaveProfileConfigurationAsync(DeploymentProfile input) =>
        ExecuteAsync(() => profiles.SaveProfileConfigurationAsync(input));

    public Task<ServiceResult> DeleteProfileAsync(int id) =>
        ExecuteAsync(() => profiles.DeleteProfileAsync(id));

    public Task<ServiceResult<List<DeploymentDriverRule>>> GetDriverRulesAsync(int id) =>
        ExecuteAsync(() => settings.GetDriverRulesAsync(id));

    public Task<ServiceResult> SaveDriversAsync(DeploymentProfile input, IReadOnlyList<DeploymentDriverRule> rules) =>
        ExecuteAsync(() => settings.SaveDriversAsync(input, rules));

    public Task<ServiceResult<List<Policy>>> GetAvailablePoliciesAsync() =>
        ExecuteAsync(() => settings.GetAvailablePoliciesAsync());

    public Task<ServiceResult<List<DeploymentPolicy>>> GetPoliciesAsync(int id) =>
        ExecuteAsync(() => settings.GetPoliciesAsync(id));

    public Task<ServiceResult> SavePoliciesAsync(int id, IReadOnlyList<int> ids) =>
        ExecuteAsync(() => settings.SavePoliciesAsync(id, ids));

    public Task<ServiceResult<Device>> GetDeviceAsync(int id) =>
        ExecuteAsync(() => settings.GetDeviceAsync(id));

    public Task<ServiceResult<EffectiveImagingSettings>> GetEffectiveSettingsAsync(int id) =>
        ExecuteAsync(() => settings.GetEffectiveSettingsAsync(id));

    public Task<ServiceResult> SaveGroupDefaultsAsync(int id, int? imageId, int? profileId) =>
        ExecuteAsync(() => settings.SaveGroupDefaultsAsync(id, imageId, profileId));

    public Task<ServiceResult<List<Device>>> GetDevicesAsync(int? imageId = null, int? profileId = null) =>
        ExecuteAsync(() => settings.GetDevicesAsync(imageId, profileId));

    public Task<ServiceResult> SaveDeviceDefaultsAsync(int id, int? imageId, int? profileId) =>
        ExecuteAsync(() => settings.SaveDeviceDefaultsAsync(id, imageId, profileId));

    public Task<ServiceResult<List<ImageVersion>>> GetVersionsAsync(int imageId) =>
        ExecuteAsync(() => versions.GetVersionsAsync(imageId));

    public Task<ServiceResult<ImageVersion>> RegisterCompletedUploadAsync(int imageId, Guid uploadId, int storageProviderId, long sizeBytes) =>
        ExecuteAsync(() => versions.RegisterCompletedUploadAsync(imageId, uploadId, storageProviderId, sizeBytes));

    public Task<ServiceResult> SetActiveAsync(int imageId, Guid versionId) =>
        ExecuteAsync(() => versions.SetActiveAsync(imageId, versionId));

    public Task<ServiceResult> SaveDetailsAsync(int imageId, Guid versionId, string name, string? description) =>
        ExecuteAsync(() => versions.SaveDetailsAsync(imageId, versionId, name, description));

    public Task<ServiceResult<ImagingDeployment>> PrepareAssignedAsync(int deviceId, int imageId, int profileId) =>
        ExecuteAsync(() => deployments.PrepareAssignedAsync(deviceId, imageId, profileId));

    public Task<ServiceResult<ImagingDeployment>> PrepareAsync(int deviceId, int? imageId = null, int? profileId = null) =>
        ExecuteAsync(() => deployments.PrepareAsync(deviceId, imageId, profileId));

    public Task<ServiceResult> CompleteImagingAsync(Guid id, int deviceId) =>
        ExecuteAsync(() => deployments.CompleteImagingAsync(id, deviceId));

    public Task<ServiceResult> CancelAsync(Guid id, int deviceId) =>
        ExecuteAsync(() => deployments.CancelAsync(id, deviceId));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Image.Failed", MapError, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Image.Failed", MapError, operationName);

    private static ServiceError? MapError(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => new("Image.Validation", ["Another operation changed these versions. Refresh and try again."]),
        _ => null
    };

    public Task<ServiceResult<List<ImagingRelation>>> GetInspectionRelationsAsync(ImagingItemKind kind, int id) =>
        ExecuteAsync(() => inspection.GetRelationsAsync(kind, id));

    public Task<ServiceResult<List<AuditLog>>> GetInspectionAuditAsync(ImagingItemKind kind, int id) =>
        ExecuteAsync(() => inspection.GetAuditAsync(kind, id));

    public Task<ServiceResult<List<ImagingHistoryEntry>>> GetInspectionHistoryAsync(ImagingItemKind kind, int id) =>
        ExecuteAsync(() => inspection.GetHistoryAsync(kind, id));

    public Task<ServiceResult<List<ImagePrepProfile>>> GetImagePrepProfilesAsync() =>
        ExecuteAsync(() => imagePrep.GetProfilesAsync());

    public Task<ServiceResult<List<ManagedCredentialDto>>> GetImagePrepCredentialsAsync() =>
        ExecuteAsync(() => imagePrep.GetCredentialsAsync());

    public Task<ServiceResult> SaveImagePrepAsync(ImagePrepProfile input) =>
        ExecuteAsync(() => imagePrep.SaveAsync(input));

    public Task<ServiceResult> DeleteImagePrepAsync(int id) =>
        ExecuteAsync(() => imagePrep.DeleteAsync(id));

    public Task<ServiceResult<List<OuNamingRuleSet>>> ListOuNamingAsync() =>
        ExecuteAsync(() => ouNaming.ListAsync());

    public Task<ServiceResult<OuNamingCatalog>> OuNamingCatalogAsync() =>
        ExecuteAsync(() => ouNaming.CatalogAsync());

    public Task<ServiceResult<OuNamingResult>> PreviewOuNamingAsync(OuNamingRuleSet draft, string computerName) =>
        ExecuteAsync(() => ouNaming.PreviewAsync(draft, computerName));

    public Task<ServiceResult> SaveOuNamingAsync(OuNamingRuleSet input) =>
        ExecuteAsync(() => ouNaming.SaveAsync(input));

    public Task<ServiceResult> DeleteOuNamingAsync(int id, int revision) =>
        ExecuteAsync(() => ouNaming.DeleteAsync(id, revision));

    public Task<ServiceResult<ImageReplicationSettings>> GetReplicationAsync(int imageId) =>
        ExecuteAsync(() => replication.GetAsync(imageId));

    public Task<ServiceResult> SaveReplicationAsync(int imageId, IEnumerable<int> destinations) =>
        ExecuteAsync(() => replication.SaveAsync(imageId, destinations));

    public Task<ServiceResult<List<ImagingTaskRowDto>>> GetTaskAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => tasks.GetAsync(ct));

    public Task<ServiceResult<List<ImagingTaskRowDto>>> GetTaskAsync(bool allTasks, CancellationToken ct = default) =>
        ExecuteAsync(() => tasks.GetAsync(allTasks, ct));

    public Task<ServiceResult> CancelTaskAsync(Guid uploadId, CancellationToken ct = default) =>
        ExecuteAsync(() => tasks.CancelAsync(uploadId, ct));

    public Task<ServiceResult> CancelTaskDeploymentAsync(Guid id, int deviceId) =>
        ExecuteAsync(() => tasks.CancelDeploymentAsync(id, deviceId));

    public Task<ServiceResult> TaskRecoverPolicyAsync(Guid id, int deviceId, Guid stepId, string hash, bool skip) =>
        ExecuteAsync(() => tasks.RecoverPolicyAsync(id, deviceId, stepId, hash, skip));

    public Task<ServiceResult<List<DomainJoinRuleChoice>>> GetOuRuleChoicesAsync() =>
        ExecuteAsync(() => settings.GetOuRuleChoicesAsync());

    public Task<List<InventoriedModelDto>> GetAutomaticRuleModelsAsync() =>
        deployments.GetAutomaticRuleModelsAsync();

    public Task<List<AutomaticDeploymentRule>> GetAutomaticRulesAsync() =>
        deployments.GetAutomaticRulesAsync();

    public Task SaveAutomaticRuleGeneralAsync(AutomaticDeploymentRule input) =>
        deployments.SaveAutomaticRuleGeneralAsync(input);

    public Task SaveAutomaticRuleMatchingAsync(AutomaticDeploymentRule input) =>
        deployments.SaveAutomaticRuleMatchingAsync(input);

    public Task SaveAutomaticRuleDeploymentAsync(AutomaticDeploymentRule input) =>
        deployments.SaveAutomaticRuleDeploymentAsync(input);

    public Task DeleteAutomaticRuleAsync(int id) =>
        deployments.DeleteAutomaticRuleAsync(id);

    public Task<ServiceResult<ImagingDeployment>> PrepareGroupMemberAsync(int groupId, int deviceId, int imageId, int profileId) =>
        ExecuteAsync(() => deployments.PrepareGroupMemberAsync(groupId, deviceId, imageId, profileId));

    public Task<List<MulticastProvider>> MulticastProvidersAsync(int imageId) =>
        deployments.MulticastProvidersAsync(imageId);

    public Task<List<MulticastRow>> MulticastListAsync() =>
        deployments.MulticastListAsync();

    public Task<List<MulticastRow>> MulticastListAsync(bool allTasks) =>
        deployments.MulticastListAsync(allTasks);

    public Task<Guid> CreateMulticastAsync(MulticastCreate request, DeviceGroupService groups) =>
        deployments.CreateMulticastAsync(request, groups);

    public Task CancelMulticastAsync(Guid id) =>
        deployments.CancelMulticastAsync(id);

    public Task RemoveMulticastHistoryAsync(Guid id) =>
        deployments.RemoveMulticastHistoryAsync(id);

    public static string GenerateImagePrepAnswer(ImagePrepGeneratorOptions o, IReadOnlyDictionary<int, string> usernames) =>
        ImagePrepAnswerGenerator.Generate(o, usernames);


    public static PolicyTargetDirectoryNodeDto? FindNamingOu(OuNamingRuleSet set, Guid? id, OuNamingCatalog catalog) =>
        OuNamingResolver.FindOu(set, id, catalog);
}
