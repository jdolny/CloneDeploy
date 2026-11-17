using CloneDeploy_Server.Localization;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks and publishes policies for devices to run and handles rerun or deactivation requests.
/// </summary>
public sealed class PolicyActivationService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<int>>> GetActiveIdsAsync()
    {
        if (await GetAuthorizedUserAsync(false) is null)
            return ServiceResult<List<int>>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<int>>.Ok(await db.ActiveCompiledPolicies.Select(x => x.PolicyId).ToListAsync());
    }

    public async Task<ServiceResult<PolicyActivationPreviewDto>> PreviewActivationAsync(int policyId)
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView) is null &&
            await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyActivate) is null)
            return ServiceResult<PolicyActivationPreviewDto>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var policy = await db.Policies.SingleOrDefaultAsync(x => x.Id == policyId);
        if (policy is null) return ServiceResult<PolicyActivationPreviewDto>.Fail(ErrorKeys.Common.NotFound, policyId);
        var (content, error) = await PolicyCompiler.CompileAsync(db, policy);
        if (error is not null)
            return ServiceResult<PolicyActivationPreviewDto>.Fail("Policy.Validation", error);
        var hash = ComputeHash(content!);
        return ServiceResult<PolicyActivationPreviewDto>.Ok(new(hash,
            !string.IsNullOrEmpty(policy.Hash) && policy.Hash != hash,
            policy.Frequency is PolicyFrequency.OncePerComputer or PolicyFrequency.OncePerUserPerComputer)
        {
            Warnings = UserContextWarnings(content!)
        });
    }

    private static List<string> UserContextWarnings(CompiledPolicyContentDto content)
    {
        var warnings = new List<string>();
        if (!PolicyUserContext.UsesUserContext(content.Settings)) return warnings;
        var user = content.Settings.GetProperty("Trigger").GetInt32() == (int)PolicyTrigger.Login
            ? "the logged-in user" : "the requesting user";
        void Inspect(CompiledPolicyActionDto action, bool condition)
        {
            if (action.Settings.TryGetProperty("ManagedCredentialId", out var credential)
                && credential.ValueKind == JsonValueKind.Number && credential.GetInt32() > 0)
                warnings.Add($"{(condition ? "Condition" : "Action")} \"{action.Name}\" has a Run As credential configured. This policy will ignore that setting and run it as {user}, with that user's permissions.");
            if (action.Condition is not null) Inspect(action.Condition, true);
        }
        if (content.Condition is not null) Inspect(content.Condition, true);
        foreach (var action in content.Actions) Inspect(action, false);
        return warnings.Distinct().ToList();
    }

    public async Task<ServiceResult> ActivateAsync(int policyId, bool reRunExisting = false)
    {
        return await ActivateAsync(policyId, reRunExisting, null);
    }

    public async Task<ServiceResult> ActivateAsync(int policyId, bool reRunExisting, string? expectedHash)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyActivate);
        if (user is null)
            return ServiceResult.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var policy = await db.Policies.SingleOrDefaultAsync(x => x.Id == policyId);
        if (policy is null) return ServiceResult.Fail(ErrorKeys.Common.NotFound, policyId);
        if (await db.ActiveCompiledPolicies.AnyAsync(x => x.PolicyId == policyId))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyActivationService.ThePolicyIsAlreadyActiveDeactivateItBeforePublishing));
        var (content, error) = await PolicyCompiler.CompileAsync(db, policy);
        if (error is not null)
            return ServiceResult.Fail("Policy.Validation", error);
        var hash = ComputeHash(content!);
        if (expectedHash is not null && hash != expectedHash)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyActivationService.TheSavedPolicyChangedWhileConfirmingActivationPleaseTry));
        var rerun = reRunExisting && !string.IsNullOrEmpty(policy.Hash) && policy.Hash != hash &&
                    policy.Frequency is PolicyFrequency.OncePerComputer or PolicyFrequency.OncePerUserPerComputer;
        var json = JsonSerializer.Serialize(new CompiledPolicySnapshotDto(CloneDeploy_Agent.Contracts.PolicyUserContext.SchemaVersion(content!.Settings), hash, rerun, content!));
        if (JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(json)?.Hash != hash)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyActivationService.TheCompiledPolicyCouldNotBeVerified));
        if (policy.Hash != hash)
            db.PolicyHashHistories.Add(new PolicyHashHistory { PolicyId = policyId, Hash = hash, Json = json, ModifiedAtUtc = DateTime.UtcNow });
        policy.Hash = hash;
        db.ActiveCompiledPolicies.Add(new ActiveCompiledPolicy { PolicyId = policyId, Json = json });
        AddAudit(db, user, policy, AuditType.ActivatePolicy, new { hash, policy.ExecutionRevision, ReRunExisting = rerun });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> ReRunAsync(int policyId, string? expectedHash = null)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyActivate);
        if (user is null)
            return ServiceResult.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var policy = await db.Policies.SingleOrDefaultAsync(x => x.Id == policyId);
        if (policy is null) return ServiceResult.Fail(ErrorKeys.Common.NotFound, policyId);
        if (policy.Archived || policy.ArchivedAtUtc.HasValue)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyActivationService.ArchivedPoliciesCannotBeRerun));
        var active = await db.ActiveCompiledPolicies.SingleOrDefaultAsync(x => x.PolicyId == policyId);
        CompiledPolicyContentDto content;
        if (active is not null)
        {
            var snapshot = JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(active.Json);
            if (snapshot is null || snapshot.SchemaVersion is not (1 or 2 or 3))
                return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyActivationService.TheActivePolicySnapshotIsInvalidOrUnsupported));
            content = snapshot.Policy;
        }
        else
        {
            var compilation = await PolicyCompiler.CompileAsync(db, policy);
            if (compilation.Error is not null)
                return ServiceResult.Fail("Policy.Validation", compilation.Error);
            content = compilation.Value!;
        }

        if (expectedHash is not null && ComputeHash(content!) != expectedHash)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyActivationService.ThePolicyChangedWhileConfirmingReRunPleaseReload));
        // Keep the published content intact, even if shared actions have since been edited.
        policy.ExecutionRevision = checked(Math.Max(policy.ExecutionRevision, content.ExecutionRevision) + 1);
        content = content with { ExecutionRevision = policy.ExecutionRevision };
        var hash = ComputeHash(content!);
        var json = JsonSerializer.Serialize(new CompiledPolicySnapshotDto(CloneDeploy_Agent.Contracts.PolicyUserContext.SchemaVersion(content!.Settings), hash, true, content));
        policy.Hash = hash;
        if (active is null) db.ActiveCompiledPolicies.Add(new ActiveCompiledPolicy { PolicyId = policyId, Json = json });
        else active.Json = json;
        db.PolicyHashHistories.Add(new PolicyHashHistory { PolicyId = policyId, Hash = hash, Json = json, ModifiedAtUtc = DateTime.UtcNow });
        AddAudit(db, user, policy, AuditType.ActivatePolicy, new { Operation = "ReRun", hash, policy.ExecutionRevision, ReRunExisting = true });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    private static string ComputeHash(CompiledPolicyContentDto content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content)))).ToLowerInvariant();
    }

    public async Task<ServiceResult> DeactivateAsync(int policyId)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyDeactivate);
        if (user is null)
            return ServiceResult.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var policy = await db.Policies.SingleOrDefaultAsync(x => x.Id == policyId);
        if (policy is null) return ServiceResult.Fail(ErrorKeys.Common.NotFound, policyId);
        var active = await db.ActiveCompiledPolicies.Where(x => x.PolicyId == policyId).ToListAsync();
        if (active.Count == 0) return ServiceResult.Ok();
        db.ActiveCompiledPolicies.RemoveRange(active);
        AddAudit(db, user, policy, AuditType.DeactivatePolicy, new { policy.Hash });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    // Administrative inspection and agent delivery share PolicyDelivery targeting.
    // This entry point still requires administrative policy/device permissions.
    public async Task<ServiceResult<List<CompiledPolicySnapshotDto>>> GetEffectivePoliciesAsync(int deviceId)
    {
        var user = await GetAuthorizedUserAsync(false);
        if (user is null)
            return ServiceResult<List<CompiledPolicySnapshotDto>>.Fail("Policy.Forbidden");
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded)
            return ServiceResult<List<CompiledPolicySnapshotDto>>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId);
        if (device is null) return ServiceResult<List<CompiledPolicySnapshotDto>>.Fail(ErrorKeys.Common.NotFound, deviceId);
        if (device.ArchivedAtUtc.HasValue) return ServiceResult<List<CompiledPolicySnapshotDto>>.Ok([]);
        return ServiceResult<List<CompiledPolicySnapshotDto>>.Ok(await PolicyDelivery.ReadAsync(db, deviceId));
    }

    public async Task<ServiceResult<List<EffectivePolicyDetailsDto>>> GetEffectivePolicyDetailsAsync(int deviceId)
    {
        var user = await GetAuthorizedUserAsync(false);
        if (user is null)
            return ServiceResult<List<EffectivePolicyDetailsDto>>.Fail("Policy.Forbidden");
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded)
            return ServiceResult<List<EffectivePolicyDetailsDto>>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId);
        if (device is null) return ServiceResult<List<EffectivePolicyDetailsDto>>.Fail(ErrorKeys.Common.NotFound, deviceId);
        if (device.ArchivedAtUtc.HasValue) return ServiceResult<List<EffectivePolicyDetailsDto>>.Ok([]);
        return ServiceResult<List<EffectivePolicyDetailsDto>>.Ok(await PolicyDelivery.ReadDetailsAsync(db, deviceId));
    }

    private async Task<ClaimsPrincipal?> GetAuthorizedUserAsync(bool edit)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded ||
            (edit && (!(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyEdit)).Succeeded ||
                      !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _)))) return null;
        return user;
    }

    private static void AddAudit(CloneDeployDbContext db, ClaimsPrincipal user, Policy policy, AuditType type, object details)
    {
        var userId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = type,
            ObjectType = nameof(Policy), ObjectId = policy.Id, ObjectName = policy.Guid.ToString(), ObjectJson = JsonSerializer.Serialize(new { policy.Name, Details = details })
        });
    }

}
