using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

/// <summary>
/// Holds the saved rules used to choose members of an automatic device group.
/// </summary>
public sealed class DeviceQueryDefinitionDto
{
    public int Version { get; set; } = 1;
    public DeviceQueryNodeDto Root { get; set; } = new();
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() }, MaxDepth = 32 };
    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static DeviceQueryDefinitionDto Parse(string json)
    {
        if (json.Length > 100_000) throw new InvalidOperationException("The query is too large.");
        try { return JsonSerializer.Deserialize<DeviceQueryDefinitionDto>(json, Options) ?? throw new InvalidOperationException("The query is empty."); }
        catch (JsonException) { throw new InvalidOperationException("The saved query format is invalid."); }
    }
}
