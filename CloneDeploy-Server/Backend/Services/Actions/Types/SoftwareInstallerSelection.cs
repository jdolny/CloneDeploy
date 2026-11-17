using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks and updates the installer selected for a software action.
/// </summary>
public static class SoftwareInstallerSelection
{
    public const string MsiArguments = "/qn /norestart";

    public static string Extension(SoftwareAction action) => action.Type switch
    {
        SoftwareActionType.Msi => action.MsiInstallType == MsiInstallType.Patch ? ".msp" : ".msi",
        SoftwareActionType.Exe => ".exe",
        SoftwareActionType.Pkg => ".pkg",
        _ => ""
    };

    public static bool Matches(SoftwareAction action, string name) =>
        Extension(action) is { Length: > 0 } extension &&
        string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase);

    public static ServiceResult Select(SoftwareAction action, ActionFileItemDto file)
    {
        if (!Matches(action, file.Name))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.SoftwareInstallerSelection.ChooseAFileMatchingTheInstallerType));
        var changed = action.InstallerFileId != file.Id;
        action.InstallerFileId = file.Id;
        action.Command = file.Name;
        if (changed && action.Type == SoftwareActionType.Msi && string.IsNullOrWhiteSpace(action.AdditionalArguments))
            action.AdditionalArguments = MsiArguments;

        return ServiceResult.Ok();
    }

    public static void Synchronize(SoftwareAction action, IReadOnlyList<ActionFileItemDto> files)
    {
        if (action.Type == SoftwareActionType.WinGet) return;
        var candidates = files.Where(x => Matches(action, x.Name)).ToList();
        if (action.InstallerFileId.HasValue)
        {
            var current = candidates.FirstOrDefault(x => x.Id == action.InstallerFileId);
            if (current is not null) Select(action, current);
            // Do not silently replace a deleted or incompatible installer with a different file.
            return;
        }

        var named = candidates.FirstOrDefault(x => string.Equals(x.Name, action.Command, StringComparison.OrdinalIgnoreCase));
        if (named is not null) Select(action, named);
        else if (string.IsNullOrWhiteSpace(action.Command) && candidates.Count == 1) Select(action, candidates[0]);
    }

    public static bool Ready(ActionFileItemDto file) => file.Size.HasValue && file.Status == "Complete" && !file.Busy;
}
