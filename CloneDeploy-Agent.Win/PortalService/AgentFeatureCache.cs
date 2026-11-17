using System.Text.Json;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent;

/// <summary>Service-owned feature availability for local portal display.</summary>
public sealed class AgentFeatureCache(AgentOptions options)
{
    private string FileName => Path.Combine(options.StateDirectory, "features.json");
    public AgentFeatures Read()
    {
        try { return JsonSerializer.Deserialize<AgentFeatures>(File.ReadAllText(FileName)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(AgentFeatures value)
    {
        if (Read() == value && File.Exists(FileName)) return;
        Directory.CreateDirectory(options.StateDirectory);
        var temporary = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, FileName, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
