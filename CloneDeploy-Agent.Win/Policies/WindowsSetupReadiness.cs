using Microsoft.Win32;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

internal static class WindowsSetupReadiness
{
    internal static AgentSetupReadiness Evaluate(string? imageState, object? oobeInProgress)
    {
        if (!string.Equals(imageState, "IMAGE_STATE_COMPLETE", StringComparison.OrdinalIgnoreCase))
            return new(false, $"Waiting for Windows setup: image state is {imageState ?? "unavailable"}.");
        return oobeInProgress switch
        {
            0 => new(true, null),
            1 => new(false, "Waiting for Windows OOBE to finish: SYSTEM\\Setup\\OOBEInProgress is 1."),
            _ => new(false, "Unable to determine Windows OOBE status: SYSTEM\\Setup\\OOBEInProgress is missing or invalid.")
        };
    }

    internal static AgentSetupReadiness EvaluateEnrollment(string? imageState, object? oobeInProgress, object? auditInProgress)
    {
        // Permit the reference machine in audit mode, but never a generalized image
        // awaiting specialize, even if its captured audit flag remains set.
        // The specialized audit state is sufficient; AuditInProgress need not
        // remain set while the administrator is working in audit mode.
        if (string.Equals(imageState, "IMAGE_STATE_SPECIALIZE_RESEAL_TO_AUDIT", StringComparison.OrdinalIgnoreCase) ||
            (auditInProgress is int audit && audit == 1 &&
             string.Equals(imageState, "IMAGE_STATE_COMPLETE", StringComparison.OrdinalIgnoreCase)))
            return new(true, null);
        return Evaluate(imageState, oobeInProgress);
    }

    public static AgentSetupReadiness Read() => ReadRegistry(false);
    public static AgentSetupReadiness ReadForEnrollment() => ReadRegistry(true);

    private static AgentSetupReadiness ReadRegistry(bool enrollment)
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var state = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State");
            using var setup = registry.OpenSubKey(@"SYSTEM\Setup");
            var imageState = state?.GetValue("ImageState") as string;
            return enrollment
                ? EvaluateEnrollment(imageState, setup?.GetValue("OOBEInProgress"), setup?.GetValue("AuditInProgress"))
                : Evaluate(imageState, setup?.GetValue("OOBEInProgress"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
            or IOException)
        {
            return new(false, $"Unable to read Windows setup status ({ex.GetType().Name}).");
        }
    }

}
