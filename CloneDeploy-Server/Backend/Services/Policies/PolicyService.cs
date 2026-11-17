using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using System.Runtime.CompilerServices;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

// Single UI entry point for policy editing, targeting, and publication.
/// <summary>
/// Lets users manage policies, their actions, and the devices they apply to.
/// </summary>
public sealed class PolicyService(
    PolicyAdministration policies,
    PolicyTargetService targets,
    PolicyActivationService activation,
    ILogger<PolicyService> logger,
    PolicyAudienceService audiences,
    UserAudienceService userAudiences,
    SelfServiceCategoryService categories)
{
    public Task<ServiceResult<List<Policy>>> GetAsync(string? search = null, bool includeArchived = false) => ExecuteAsync(() => policies.GetAsync(search, includeArchived));

    public Task<ServiceResult<Policy>> CreateAsync(string name, string? description) => ExecuteAsync(() => policies.CreateAsync(name, description));

    public Task<ServiceResult<PolicyDetailsDto>> GetDetailsAsync(int id) => ExecuteAsync(() => policies.GetDetailsAsync(id));

    public Task<ServiceResult<List<PolicyRelationDto>>> GetRelationsAsync(int id) => ExecuteAsync(() => policies.GetRelationsAsync(id));

    public Task<ServiceResult<PolicyDetailsDto>> GetSectionAsync(int id, string section) => ExecuteAsync(() => policies.GetSectionAsync(id, section));

    public Task<ServiceResult> SaveAsync(PolicyDetailsDto details, string section) => ExecuteAsync(() => policies.SaveAsync(details, section));

    public Task<ServiceResult> ArchiveManyAsync(IEnumerable<Guid> guids) => ExecuteAsync(() => policies.ArchiveManyAsync(guids));

    public Task<ServiceResult> RestoreAsync(Guid guid) => ExecuteAsync(() => policies.RestoreAsync(guid));

    public Task<ServiceResult> DeleteManyAsync(IEnumerable<Guid> guids) => ExecuteAsync(() => policies.DeleteManyAsync(guids));

    public Task<ServiceResult<PolicyTargetStateDto>> GetPolicyTargetsAsync(int policyId) => ExecuteAsync(() => targets.GetPolicyTargetsAsync(policyId));

    public Task<ServiceResult> SavePolicyTargetsAsync(PolicyTargetStateDto state) => ExecuteAsync(() => targets.SavePolicyTargetsAsync(state));

    public Task<ServiceResult> SavePolicyTargetsAsync(PolicyTargetStateDto state, bool saveCondition) => ExecuteAsync(() => targets.SavePolicyTargetsAsync(state, saveCondition));

    public Task<ServiceResult<List<PolicyTargetDeviceDto>>> SearchDevicesAsync(string? search) => ExecuteAsync(() => targets.SearchDevicesAsync(search));

    public Task<ServiceResult<PolicyTargetPreviewDto>> PreviewAsync(PolicyTargetStateDto state) => ExecuteAsync(() => targets.PreviewAsync(state));

    public Task<ServiceResult<TargetPolicyStateDto>> GetTargetPoliciesAsync(PolicyTargetKeyDto key) => ExecuteAsync(() => targets.GetTargetPoliciesAsync(key));

    public Task<ServiceResult> SaveTargetPoliciesAsync(TargetPolicyStateDto state) => ExecuteAsync(() => targets.SaveTargetPoliciesAsync(state));

    public Task<ServiceResult<List<int>>> GetActiveIdsAsync() => ExecuteAsync(() => activation.GetActiveIdsAsync());

    public Task<ServiceResult<PolicyActivationPreviewDto>> PreviewActivationAsync(int policyId) => ExecuteAsync(() => activation.PreviewActivationAsync(policyId));

    public Task<ServiceResult> ActivateAsync(int policyId, bool reRunExisting = false) => ExecuteAsync(() => activation.ActivateAsync(policyId, reRunExisting));

    public Task<ServiceResult> ActivateAsync(int policyId, bool reRunExisting, string? expectedHash) => ExecuteAsync(() => activation.ActivateAsync(policyId, reRunExisting, expectedHash));

    public Task<ServiceResult> ReRunAsync(int policyId, string? expectedHash = null) => ExecuteAsync(() => activation.ReRunAsync(policyId, expectedHash));

    public Task<ServiceResult> DeactivateAsync(int policyId) => ExecuteAsync(() => activation.DeactivateAsync(policyId));

    public Task<ServiceResult<List<CompiledPolicySnapshotDto>>> GetEffectivePoliciesAsync(int deviceId) => ExecuteAsync(() => activation.GetEffectivePoliciesAsync(deviceId));

    public Task<ServiceResult<List<EffectivePolicyDetailsDto>>> GetEffectivePolicyDetailsAsync(int deviceId) => ExecuteAsync(() => activation.GetEffectivePolicyDetailsAsync(deviceId));

    public static IEnumerable<PolicyFrequency> Frequencies(PolicyTrigger trigger) => PolicyAdministration.Frequencies(trigger);
    public static string? ValidateTrigger(Policy policy) => PolicyAdministration.ValidateTrigger(policy);

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Policy.Failed", MapError, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Policy.Failed", MapError, operationName);

    private static ServiceError? MapError(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation } or { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation } } => new("Policy.Validation", ["The policy changed in another session. Reload and try again."]),
        _ => null
    };

    public Task<ServiceResult<PolicyAudienceState>> GetAudienceAsync(int policyId) =>
        ExecuteAsync(() => audiences.GetAsync(policyId));

    public Task<ServiceResult> SaveAudienceAsync(PolicyAudienceState state) =>
        ExecuteAsync(() => audiences.SaveAsync(state));

    public Task<ServiceResult<List<UserAudience>>> ListUserAudienceAsync() =>
        ExecuteAsync(() => userAudiences.ListAsync());

    public Task<ServiceResult<UserAudience>> GetUserAudienceAsync(int id) =>
        ExecuteAsync(() => userAudiences.GetAsync(id));

    public Task<ServiceResult<List<PolicyAudienceOption>>> UserAudienceOptionsAsync() =>
        ExecuteAsync(() => userAudiences.OptionsAsync());

    public Task<ServiceResult> SaveUserAudienceAsync(UserAudience input, bool delete = false) =>
        ExecuteAsync(() => userAudiences.SaveAsync(input, delete));

    public Task<ServiceResult<List<SelfServiceCategory>>> ListCategoryAsync() =>
        ExecuteAsync(() => categories.ListAsync());

    public Task<ServiceResult> SaveCategoryAsync(SelfServiceCategory input, bool delete = false) =>
        ExecuteAsync(() => categories.SaveAsync(input, delete));

    public Task<ServiceResult> MoveCategoryAsync(int id, Guid revision, int direction) =>
        ExecuteAsync(() => categories.MoveAsync(id, revision, direction));

    public const int MaximumIconBytes = PortalIcon.MaximumBytes;
    public static bool IsValidIcon(byte[] data) => PortalIcon.IsValid(data);
}
