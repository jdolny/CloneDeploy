using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class DomainJoinPlan
{
    // The caller must supply an action from the authenticated device's active run snapshot.
    internal static async Task<ServiceResult<AgentDomainJoinPlan>> CreateAsync(ICloneDeployDbFactory factory, CloneDeployDbContext db,
        CompiledPolicyActionDto? action, AgentDomainJoinRequest request, ISecretProtector secrets, CancellationToken ct)
    {
        if (action is null || action.Type != ActionType.DomainJoin) return ServiceResult<AgentDomainJoinPlan>.Fail(ServiceErrorCodes.NotFound);
        var settings = action.Settings;
        if (!settings.TryGetProperty("OuMode", out var mode) || !mode.TryGetInt32(out var modeValue) || modeValue != (int)DomainJoinOuMode.NamingRules ||
            !settings.TryGetProperty("OuNamingRuleSetId", out var rule) || !rule.TryGetInt32(out var ruleId) ||
            !settings.TryGetProperty("DomainJoinCredentialId", out var credential) || !credential.TryGetInt32(out var credentialId))
            return ServiceResult<AgentDomainJoinPlan>.Fail(ServiceErrorCodes.NotFound);
        var error = ComputerNameValidation.Error(request.ComputerName);
        if (error is not null) return ServiceResult<AgentDomainJoinPlan>.Ok(new(false, null, error, null));
        var domain = settings.GetProperty("DomainName").GetString()!;
        var result = await new OuNamingResolver(factory).ResolveAsync(ruleId, domain, request.ComputerName, ct);
        if (!result.Success) return ServiceResult<AgentDomainJoinPlan>.Ok(new(false, null, result.Message, null));
        var secret = await db.ManagedCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == credentialId, ct);
        if (secret is null) return ServiceResult<AgentDomainJoinPlan>.Fail(ServiceErrorCodes.NotFound);
        return ServiceResult<AgentDomainJoinPlan>.Ok(new(true, result.OrganizationalUnit, result.Message,
            new(secret.Username, secrets.Unprotect(secret.PasswordEncrypted))));
    }
}
