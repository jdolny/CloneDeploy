using System.Text.Json;

namespace CloneDeploy_Agent.Contracts;

/// <summary>Defines the supported trigger/context combinations and restrictions shared by server and agent.</summary>
public static class PolicyUserContext
{
    private static int Number(JsonElement settings, string key) => settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    private static bool Flag(JsonElement settings, string key) => settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement settings, string key) => settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    public static bool UsesUserContext(JsonElement settings)
    {
        var trigger = Number(settings, "Trigger");
        var requestingUser = Flag(settings, "SelfServiceRunAsUser");
        if (requestingUser && trigger != (int)PolicyTrigger.SelfServiceOnly) throw new InvalidDataException("Requesting-user execution is only available for the self-service-only trigger.");
        return trigger == (int)PolicyTrigger.Login || requestingUser;
    }

    public static int SchemaVersion(JsonElement settings) => PolicyStartWindow.Read(settings) is not null
        ? 3 : Flag(settings, "SelfServiceRunAsUser") ? 2 : 1;

    public static void Validate(AgentPolicyContent policy)
    {
        var settings = policy.Settings;
        if (!UsesUserContext(settings)) throw new InvalidDataException("This policy is not configured for user execution.");
        if (Number(settings, "Trigger") == (int)PolicyTrigger.Login && Number(settings, "Frequency") is not ((int)PolicyFrequency.Ongoing or (int)PolicyFrequency.OncePerUserPerComputer))
            throw new NotSupportedException("Login policies support ongoing or once per user per computer.");
        if (Flag(settings, "RunInventory") || Number(settings, "RunInventory") != 0 ||
            Number(settings, "CompletedAction") != 0 || Number(settings, "RemoteAccess") != 0)
            throw new NotSupportedException("User-context policies cannot run device inventory, machine update tasks, remote-access configuration, domain join, or restart the computer.");
        if (policy.Condition is not null) ValidateTree(policy.Condition);
        foreach (var action in policy.Actions) ValidateTree(action);
    }

    private static void ValidateTree(AgentPolicyAction action)
    {
        ValidateAction(action);
        if (action.Condition is not null) ValidateTree(action.Condition);
    }

    public static void ValidateAction(AgentPolicyAction action)
    {
        var settings = action.Settings;
        if (action.Type == (int)ActionType.Script &&
            ScriptRuntime.ValidationError((ActionScriptType)Number(settings, "ScriptType")) is { } scriptError)
            throw new NotSupportedException(scriptError);
        if (action.Type == (int)ActionType.Notification && (string.IsNullOrWhiteSpace(Text(settings, "Title")) || Text(settings, "Title").Length > 200 ||
            string.IsNullOrWhiteSpace(Text(settings, settings.TryGetProperty("Body", out _) ? "Body" : "Message")) || Text(settings, settings.TryGetProperty("Body", out _) ? "Body" : "Message").Length > 2000 || Number(settings, "Timeout") is < 0 or > 86400))
            throw new InvalidDataException("User messages require a title, text, and a valid timeout.");
        if (action.Type is not ((int)ActionType.Printer or (int)ActionType.Software or (int)ActionType.Script
            or (int)ActionType.Command or (int)ActionType.File or (int)ActionType.Notification))
            throw new NotSupportedException("This action requires machine execution and cannot run in user context.");
        if (action.Type == (int)ActionType.Printer && Flag(settings, "RestartSpooler")) throw new NotSupportedException("User-context printer actions cannot restart the system spooler.");
        if (action.Type == (int)ActionType.Software && Number(settings, "Type") == (int)SoftwareActionType.WinGet
            && Number(settings, "WinGetInstallType") == (int)WinGetInstallType.UpdateManaged)
            throw new NotSupportedException("Managed application update planning requires machine execution; select an explicit WinGet operation.");
    }
}
