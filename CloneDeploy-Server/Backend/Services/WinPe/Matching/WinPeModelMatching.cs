using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Chooses a ready boot environment using device model rules and the default selection.
/// </summary>
public static class WinPeModelMatching
{
    public static List<WinPeModelRuleDto> Read(string json)
    {
        if (json.Length > 220000) throw new InvalidOperationException("Model matching rules are too large.");
        List<WinPeModelRuleDto> rules;
        try { rules = JsonSerializer.Deserialize<List<WinPeModelRuleDto>>(json) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("Invalid model matching rules."); }
        if (rules.Count > 100) throw new InvalidOperationException("Use at most 100 model matching rules per boot environment.");
        foreach (var rule in rules)
        {
            if (rule is null) throw new InvalidOperationException("Invalid model matching rule.");
            rule.Manufacturer = Clean(rule.Manufacturer); rule.Model = Clean(rule.Model);
            rule.ManufacturerRegex = Clean(rule.ManufacturerRegex); rule.ModelRegex = Clean(rule.ModelRegex);
            // Keep the same validation and matching semantics as deployment profile driver rules.
            var error = DeploymentProfileService.GetDriverRuleValidationError(new DeploymentDriverRule { Manufacturer = rule.Manufacturer, Model = rule.Model,
                ManufacturerRegex = rule.ManufacturerRegex, ModelRegex = rule.ModelRegex });
            if (error is not null)
                throw new InvalidOperationException(error);
        }
        return rules;
    }

    public static WinPeDefinition Resolve(IEnumerable<WinPeDefinition> definitions, string? manufacturer, string? model)
    {
        if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(model) || manufacturer.Length > 500 || model.Length > 500)
            throw new InvalidOperationException("Supply both manufacturer and model, up to 500 characters each.");
        var published = definitions.Where(x => !x.IsBase && x.PublishedBuildId.HasValue).ToList();
        var matches = published.Where(x => Read(x.ModelRulesJson).Any(r =>
            DriverMatching.Matches(new DriverMatchRuleDto(x.Id, x.Name, r.ManufacturerRegex, r.ModelRegex,
                r.Manufacturer is null || r.Model is null ? [] : [new(r.Manufacturer, r.Model, 0)]), manufacturer, model))).ToList();
        if (matches.Count == 0)
        {
            var defaults = published.Where(x => x.IsDefault).ToList();
            if (defaults.Count > 1) throw new InvalidOperationException("Multiple default WinPE boot environments are configured.");
            return defaults.SingleOrDefault() ?? throw new InvalidOperationException("No ready WinPE boot environment matches this device and no ready default is configured.");
        }
        if (matches.Count > 1) throw new InvalidOperationException("Multiple ready WinPE boot environments match this device: " + string.Join(", ", matches.Select(x => x.Name)) + ". Adjust their model rules.");
        return matches[0];
    }

    // Missing inventory can still use an explicitly ready default; never guess a model rule.
    public static WinPeDefinition ResolveForAgent(IEnumerable<WinPeDefinition> definitions, string? manufacturer, string? model)
    {
        if (!string.IsNullOrWhiteSpace(manufacturer) && !string.IsNullOrWhiteSpace(model)) return Resolve(definitions, manufacturer, model);
        return definitions.SingleOrDefault(x => !x.IsBase && x.IsDefault && x.PublishedBuildId.HasValue)
            ?? throw new InvalidOperationException("No ready default WinPE boot environment is configured and the device model is unavailable.");
    }
    public static bool MatchesModel(WinPeDefinition variant, string? manufacturer, string? model) =>
        !string.IsNullOrWhiteSpace(manufacturer) && !string.IsNullOrWhiteSpace(model) && Read(variant.ModelRulesJson).Any(r =>
            DriverMatching.Matches(new DriverMatchRuleDto(variant.Id, variant.Name, r.ManufacturerRegex, r.ModelRegex,
                r.Manufacturer is null || r.Model is null ? [] : [new(r.Manufacturer, r.Model, 0)]), manufacturer, model));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
