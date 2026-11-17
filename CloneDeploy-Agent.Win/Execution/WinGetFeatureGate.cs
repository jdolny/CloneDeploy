using System.Net.Http.Json;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent;

public static class WinGetFeatureGate
{
    public static bool Applies(AgentPolicyAction action) => action.Type == (int)ActionType.Software && !action.Settings.Flag("Uninstall") && action.Settings.Number("Type") == (int)SoftwareActionType.WinGet;
    public static async Task<PolicyProcessResult?> CheckAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            var features = await client.GetFromJsonAsync<AgentFeatures>("api/agent/v1/features", ct);
            return features?.WinGetEnabled == true ? null : new(1, "", "WinGet integration is disabled. Enable it and acknowledge unattended deployments in Agent Settings → Features.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { return new(1, "", "Could not verify that WinGet integration is enabled. No WinGet operation was started. " + ex.Message); }
    }
}
