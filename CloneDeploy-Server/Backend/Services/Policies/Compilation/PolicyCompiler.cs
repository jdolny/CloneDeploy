using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Combines a policy's actions, files, and settings into instructions that devices can run.
/// </summary>
internal static class PolicyCompiler
{
    // Reuse the same validation and payload contract for standalone, run-once deployments.
    internal static async Task<(CompiledPolicyActionDto? Value, string? Error)> CompileStandaloneAsync(CloneDeployDbContext db, Guid guid)
    {
        var reference = await db.ActionReferences.AsNoTracking().SingleOrDefaultAsync(x => x.ActionGuid == guid);
        if (reference is null || reference.ArchivedAtUtc is not null)
            return (null, "Choose an existing, non-archived action.");
        var compiled = await CompileActionAsync(db, guid, reference!.ActionType, false);
        if (compiled.Value?.Settings.TryGetProperty("AddInventoryCollection", out var custom) == true && custom.ValueKind == JsonValueKind.True)
            return (null, "Custom inventory scripts must run through a policy with Collect inventory enabled.");
        return compiled;
    }

    public static async Task<(CompiledPolicyContentDto? Value, string? Error)> CompileAsync(CloneDeployDbContext db, Policy policy)
    {
        if (policy.Archived || policy.ArchivedAtUtc is not null)
            return (null, "Restore the policy before activating it.");
        if (string.IsNullOrWhiteSpace(policy.Name) || policy.Guid == Guid.Empty)
            return (null, "The policy needs a name and valid identity.");
        if (policy.ExecutionPriority < 0)
            return (null, "Execution priority must be zero or greater.");
        PolicyStartWindow? allowedStartWindow = null;
        if (policy.StartWindowEnabled)
        {
            if (policy.StartWindowFrom is null || policy.StartWindowUntil is null)
                return (null, "Choose From and Until times for the allowed start window.");
            allowedStartWindow = new(policy.StartWindowDays, policy.StartWindowFrom.Value, policy.StartWindowUntil.Value);
            try { allowedStartWindow.Validate(); }
            catch (InvalidDataException ex) { return (null, ex.Message); }
        }
        var choices = new object[]
        {
            policy.CompletedAction, policy.ExecutionType, policy.ErrorAction,
            policy.LogLevel, policy.MissedAction, policy.RemoteAccess
        };
        if (choices.Any(x => !System.Enum.IsDefined(x.GetType(), x)))
            return (null, "A policy behavior has an invalid value.");
        var triggerError = PolicyAdministration.ValidateTrigger(policy);
        if (triggerError is not null)
            return (null, triggerError);
        if (policy.StartDateUtc == default)
            return (null, "Choose a start date.");
        if (!System.Enum.IsDefined(policy.ConditionFailedAction) || policy.ConditionFailedAction == PolicyConditionFailedAction.GotoModule)
            return (null, "Choose a valid policy condition result.");
        var links = await db.PolicyActions.AsNoTracking().Where(x => x.PolicyId == policy.Id).OrderBy(x => x.Order).ThenBy(x => x.ActionGuid).ToListAsync();
        if (policy.Trigger != PolicyTrigger.Login && await db.Set<PolicyUserAccess>().AnyAsync(x => x.PolicyId == policy.Id && x.LoginAudienceId != null) &&
            !NotificationPolicyAudience.Eligible(policy, links)) return (null, NotificationPolicyAudience.ValidationMessage);
        if (links.Select(x => x.ActionGuid).Distinct().Count() != links.Count || links.Any(x => x.Order < 0))
            return (null, "Action assignments must be unique with non-negative order values.");
        var actions = new List<CompiledPolicyActionDto>();
        foreach (var link in links)
        {
            if (!System.Enum.IsDefined(link.ConditionFailedAction))
                return (null, "Choose a valid action condition result.");
            if (link.ConditionFailedAction == PolicyConditionFailedAction.GotoModule)
            {
                if (!link.ConditionId.HasValue || !links.Any(x => x.ActionGuid == link.ConditionNextAction && x.ActionGuid != link.ActionGuid))
                    return (null, "A condition must jump to a different assigned action.");
            }

            var (action, actionError) = await CompileActionAsync(db, link.ActionGuid, link.ActionType, false);
            if (actionError is not null) return (null, actionError);
            var (condition, conditionError) = await CompileConditionAsync(db, link.ConditionId);
            if (conditionError is not null) return (null, conditionError);
            actions.Add(action! with
            {
                Order = link.Order, Condition = condition,
                ConditionFailedAction = link.ConditionFailedAction,
                ConditionNextAction = link.ConditionFailedAction == PolicyConditionFailedAction.GotoModule ? link.ConditionNextAction : null
            });
        }

        var inventoryScripts = actions.Where(x => x.Settings.TryGetProperty("AddInventoryCollection", out var custom)
            && custom.ValueKind == JsonValueKind.True).Select(x => x.Guid).ToHashSet();
        if (inventoryScripts.Count > InventoryCustomValue.MaximumResults)
            return (null, "Too many custom inventory scripts in one policy.");
        if (inventoryScripts.Count > 0 && !policy.RunInventory)
            return (null, "Enable Collect inventory to run the custom inventory scripts assigned to this policy.");
        if (actions.Any(x => x.ConditionNextAction is { } target
            && (inventoryScripts.Contains(x.Guid) || inventoryScripts.Contains(target))))
            return (null, "Condition jumps cannot use custom inventory scripts.");

        if (!(actions.Count > 0 || policy.RunInventory || policy.RunLoginTracker ||
              policy.RemoteAccess != PolicyRemoteAccess.NotConfigured))
            return (null, "Assign an action or enable a policy behavior before activation.");
        var settings = JsonSerializer.SerializeToElement(new
        {
            policy.Trigger, policy.SelfServiceRunAsUser, policy.Frequency, policy.SubFrequency, policy.StartDateUtc, policy.CompletedAction,
            RunInventory = policy.RunInventory, policy.RunLoginTracker, policy.RemoveInstallCache, policy.ExecutionType,
            policy.ErrorAction, policy.LogLevel, policy.MissedAction, policy.SkipServerResult,
            policy.ConditionFailedAction, policy.RemoteAccess, AllowedStartWindow = allowedStartWindow
        });
        var (policyCondition, policyConditionError) = await CompileConditionAsync(db, policy.ConditionId);
        if (policyConditionError is not null) return (null, policyConditionError);
        var content = new CompiledPolicyContentDto(policy.Id, policy.Guid, policy.Name, policy.ExecutionPriority, settings,
            null, null, policyCondition, actions) { ExecutionRevision = policy.ExecutionRevision };
        try
        {
            if (PolicyUserContext.UsesUserContext(settings))
                PolicyUserContext.Validate(JsonSerializer.Deserialize<AgentPolicyContent>(JsonSerializer.Serialize(content))!);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) { return (null, ex.Message); }
        return (content, null);
    }

    private static async Task<(CompiledPolicyActionDto? Value, string? Error)> CompileConditionAsync(CloneDeployDbContext db, int? id)
    {
        if (id is null) return (null, null);
        var script = await db.ScriptActions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (script is not { IsCondition: true })
            return (null, "Choose an available condition script.");
        return await CompileActionAsync(db, script!.Guid, ActionType.Script, true);
    }

    private static async Task<(CompiledPolicyActionDto? Value, string? Error)> CompileActionAsync(CloneDeployDbContext db, Guid guid, ActionType type, bool condition)
    {
        var reference = await db.ActionReferences.AsNoTracking().SingleOrDefaultAsync(x => x.ActionGuid == guid);
        if (reference is null || reference.ArchivedAtUtc is not null || reference.ActionType != type)
            return (null, "An assigned action or condition is missing, archived, or has the wrong type.");
        var files = await db.ActionFiles.AsNoTracking().Where(x => x.ActionGuid == guid).Select(x => x.File).OrderBy(x => x.Id).ToListAsync();
        if (type == ActionType.Software && await db.SoftwareActions.AnyAsync(x => x.Guid == guid && x.Uninstall)) files.Clear();
        foreach (var file in files)
        {
            if (!(file.Status == StoredFileStatus.Complete && file.Size is >= 0 && file.Sha256Hash is { Length: 64 } && file.Sha256Hash.All(Uri.IsHexDigit)))
                return (null, $"File '{file.Name}' is not ready. Finish its download and hash calculation before activation.");
        }

        string name;
        object settings;
        switch (type)
        {
            case ActionType.WindowsUpdate:
                var update = await db.WindowsUpdateActions.AsNoTracking().SingleOrDefaultAsync(x => x.Guid == guid);
                if (update is null) return (null, "The Windows Update action no longer exists.");
                var updateError = WindowsUpdateActionService.ValidateConfiguration(update);
                if (updateError is not null) return (null, updateError);
                name = update.Name;
                settings = new { update.Mode, update.Timeout, SuccessCodes = "0,3010", RedirectStdOut = true, RedirectStdError = true };
                break;
            case ActionType.DomainJoin:
                var join = await db.DomainJoinActions.AsNoTracking().SingleOrDefaultAsync(x => x.Guid == guid);
                if (join is null) return (null, "The domain-join action no longer exists.");
                var joinError = DomainJoinActionService.ValidateConfiguration(join);
                if (joinError is not null) return (null, joinError);
                var ouRuleError = await DomainJoinActionService.ValidateRuleSetAsync(db, join);
                if (ouRuleError is not null) return (null, ouRuleError);
                if (!await db.ManagedCredentials.AnyAsync(x => x.Id == join.ManagedCredentialId))
                    return (null, "Choose an existing domain-join credential.");
                name = join.Name;
                settings = new { DomainName = join.DomainName.Trim(), OrganizationalUnit = join.OuMode == DomainJoinOuMode.SpecificOu ? join.OrganizationalUnit?.Trim() : null,
                    join.OuMode, OuNamingRuleSetId = join.OuMode == DomainJoinOuMode.NamingRules ? join.OuNamingRuleSetId : null,
                    DomainJoinActionGuid = guid,
                    // Older agents reject the missing conventional credential rather than join without resolving rules.
                    ManagedCredentialId = join.OuMode == DomainJoinOuMode.NamingRules ? null : join.ManagedCredentialId,
                    DomainJoinCredentialId = join.OuMode == DomainJoinOuMode.NamingRules ? join.ManagedCredentialId : null,
                    SuccessCodes = "0,3010", RedirectStdOut = true, RedirectStdError = true };
                break;
            case ActionType.Command:
                var command = await db.CommandActions.AsNoTracking().SingleOrDefaultAsync(x => x.Guid == guid);
                if (command is null || string.IsNullOrWhiteSpace(command.Command))
                    return (null, "A command action is missing its command.");
                var commandError = await ValidateRunAsync(db, command!.Timeout, command.SuccessCodes, command.ManagedCredentialId);
                if (commandError is not null) return (null, commandError);
                name = command.Name;
                settings = new { command.Command, command.Arguments, command.Timeout, command.WorkingDirectory, command.RedirectStdOut, command.RedirectStdError, command.SuccessCodes, command.ManagedCredentialId };
                break;
            case ActionType.Script:
                var script = await db.ScriptActions.AsNoTracking().SingleOrDefaultAsync(x => x.Guid == guid);
                if (script is not null && ScriptRuntime.ValidationError(script.ScriptType) is { } runtimeError)
                    return (null, runtimeError);
                if (script is null || script.IsCondition != condition || string.IsNullOrWhiteSpace(script.ScriptContents) || !System.Enum.IsDefined(script.ScriptType))
                    return (null, "A script or condition is missing its contents or has an invalid type.");
                var scriptError = await ValidateRunAsync(db, script!.Timeout, script.SuccessCodes, script.ManagedCredentialId);
                if (scriptError is not null) return (null, scriptError);
                name = script.Name;
                settings = new { script.ScriptType, script.ScriptContents, script.Arguments, script.Timeout, script.WorkingDirectory, script.RedirectStdOut, script.RedirectStdError, script.SuccessCodes, script.AddInventoryCollection, script.ManagedCredentialId };
                break;
            case ActionType.File:
                var copy = await db.FileActions.AsNoTracking().SingleOrDefaultAsync(x => x.Guid == guid);
                if (copy is null || string.IsNullOrWhiteSpace(copy.Destination) || files.Count == 0)
                    return (null, "A file action needs files and a destination.");
                name = copy!.Name;
                settings = new { copy.Destination, copy.DecompressAfterCopy, copy.OverwriteExisting };
                break;
            case ActionType.Notification:
                var message = await db.NotificationActions.AsNoTracking().SingleOrDefaultAsync(x => x.Guid == guid);
                if (message is null || string.IsNullOrWhiteSpace(message.Title) || string.IsNullOrWhiteSpace(message.Body) || message.Timeout is < 0 or > 86400 || !System.Enum.IsDefined(message.Position) || !System.Enum.IsDefined(message.Kind))
                    return (null, "A message action needs a title, message, valid position, and timeout between 0 and 86,400 seconds.");
                if (message.RequireAcknowledgment && (message.NoticeId == Guid.Empty || message.ExpiresAtUtc <= DateTime.UtcNow))
                    return (null, "Set a future expiration date before delivering this required notice.");
                name = message!.Name;
                settings = new { message.NoticeId, message.ExpiresAtUtc, message.Title, Message = message.Body, message.Timeout, message.RequireAcknowledgment, RemindAtLogin = message.RequireAcknowledgment, message.Position, Branding = CloneDeploy_Agent.Contracts.NotificationBranding.Organization, message.Kind };
                break;
            case ActionType.Printer:
                var printer = await db.PrinterActions.AsNoTracking().SingleOrDefaultAsync(x => x.Guid == guid);
                if (printer is null || string.IsNullOrWhiteSpace(printer.NetworkPath) || !System.Enum.IsDefined(printer.Action))
                    return (null, "A printer action needs a network path and valid operation.");
                name = printer!.Name;
                settings = new { printer.NetworkPath, printer.Action, printer.IsDefault, printer.RestartSpooler, printer.WaitForEnumeration };
                break;
            case ActionType.Software:
                var software = await db.SoftwareActions.AsNoTracking().SingleOrDefaultAsync(x => x.Guid == guid);
                if (software is null || !System.Enum.IsDefined(software.Type))
                    return (null, "A software action is missing or has an invalid type.");
                if (software.Uninstall)
                {
                    if (string.IsNullOrWhiteSpace(software.UninstallName)) return (null, "Select an application to uninstall.");
                    var uninstallError = await ValidateRunAsync(db, software.Timeout, software.SuccessCodes, software.ManagedCredentialId);
                    if (uninstallError is not null) return (null, uninstallError);
                    name = software.Name;
                    files.Clear();
                    settings = new { Type = 0, software.Uninstall, software.UninstallName, software.UninstallVersion,
                        software.Arguments, software.Timeout, software.SuccessCodes, software.ManagedCredentialId,
                        software.RedirectStdOut, software.RedirectStdError };
                    break;
                }
                if (software.MsiInstallType == MsiInstallType.Uninstall || software.WinGetInstallType == WinGetInstallType.Uninstall)
                    return (null, "Use the Uninstall operation and select an application from inventory.");
                var managedUpdate = software.Type == SoftwareActionType.WinGet && software.WinGetInstallType == WinGetInstallType.UpdateManaged;
                if (software.Type == SoftwareActionType.Pkg && (software.ManagedCredentialId.HasValue ||
                    !string.IsNullOrWhiteSpace(software.Arguments) || !string.IsNullOrWhiteSpace(software.AdditionalArguments)))
                    return (null, "PKG installs require root execution and standard installer arguments.");
                var softwareError = await ValidateRunAsync(db, software!.Timeout, managedUpdate ? "0,3010" : software.SuccessCodes, managedUpdate ? null : software.ManagedCredentialId);
                if (softwareError is not null) return (null, softwareError);
                if (!System.Enum.IsDefined(software.MsiInstallType) || !System.Enum.IsDefined(software.WinGetInstallType))
                    return (null, "Choose valid software installation options.");
                if (software.Type == SoftwareActionType.WinGet)
                {
                    if (!managedUpdate)
                    {
                        try { WinGetScopeOptions.Resolve(software.WinGetScope, software.Arguments, software.AdditionalArguments); }
                        catch (InvalidDataException ex) { return (null, ex.Message); }
                    }
                    if ((software.WinGetInstallType is WinGetInstallType.Install or WinGetInstallType.Uninstall or WinGetInstallType.Update && string.IsNullOrWhiteSpace(software.PackageId)) || (software.WinGetInstallType == WinGetInstallType.Install && !software.InstallLatest && string.IsNullOrWhiteSpace(software.PackageVersion)))
                        return (null, "Choose a WinGet package and version, or install latest.");
                }
                else
                {
                    if (!files.Any(x => x.Id == software.InstallerFileId && SoftwareInstallerSelection.Matches(software, x.Name)))
                        return (null, "Choose an attached installer matching the software type.");
                }

                name = software.Name;
                settings = new
                {
                    software.Type, software.Arguments, software.Timeout, software.RedirectStdOut, software.RedirectStdError, SuccessCodes = software.Type == SoftwareActionType.WinGet && software.WinGetInstallType == WinGetInstallType.UpdateManaged ? "0,3010" : software.SuccessCodes,
                    ManagedCredentialId = software.Type == SoftwareActionType.WinGet && software.WinGetInstallType == WinGetInstallType.UpdateManaged ? null : software.ManagedCredentialId, software.MsiInstallType, software.Command, software.InstallerFileId, software.AdditionalArguments,
                    software.Override, software.PackageId, software.PackageVersion, software.WinGetInstallType, software.WinGetScope, software.KeepUpdated, software.InstallLatest, software.UseDownloadQueue, ExplicitWinGetOperation = true
                };
                break;
            default: return (null, $"Action type {type} is not supported for activation.");
        }

        if (string.IsNullOrWhiteSpace(name))
            return (null, "An action needs a name.");
        return (new(guid, name, type, 0, JsonSerializer.SerializeToElement(settings), null, PolicyConditionFailedAction.MarkFailed, null,
            files.Select(x => new CompiledPolicyFileDto(x.Id, x.DirectoryGuid, x.Name, x.Size!.Value, x.Sha256Hash!)).ToList()), null);
    }

    private static async Task<string?> ValidateRunAsync(CloneDeployDbContext db, int timeout, string codes, int? credential)
    {
        if (timeout < 0 || string.IsNullOrWhiteSpace(codes) || codes.Split(',').Any(x => !int.TryParse(x.Trim(), out _)))
            return "Use a non-negative timeout and comma-separated numeric success codes.";
        if (credential.HasValue && !await db.ManagedCredentials.AnyAsync(x => x.Id == credential))
            return "A referenced managed credential no longer exists.";
        return null;
    }
}
