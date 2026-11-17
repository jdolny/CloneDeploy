using System.Text.RegularExpressions;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

// Shared contract for the future imaging client: trimmed, case-insensitive invariant matches.
// Exact model links OR the regex pair may match; when both regex fields exist they must BOTH match.
// Return all candidates. A caller must not choose an arbitrary winner when several match.
/// <summary>
/// Finds driver profiles that match a device's manufacturer and model.
/// </summary>
public static class DriverMatching
{
    public static bool MatchesPack(DriverPack pack, string? manufacturer, string? model) =>
        Matches(new(pack.Id, pack.Name, pack.ManufacturerRegex, pack.ModelRegex,
            pack.Models.Select(x => new InventoriedModelDto(x.Manufacturer, x.Model, 0)).ToList()), manufacturer, model) ||
        pack.RegexRules.Any(rule => Matches(new(pack.Id, pack.Name, rule.ManufacturerRegex, rule.ModelRegex, []), manufacturer, model));

    public static string? GetPackValidationError(DriverPack pack)
    {
        if (pack.Models.Count > 500 || pack.RegexRules.Count > 500) return "A pack can contain up to 500 models and 500 regex rules.";
        foreach (var pattern in new[] { pack.ManufacturerRegex, pack.ModelRegex }
                     .Concat(pack.RegexRules.SelectMany(r => new[] { r.ManufacturerRegex, r.ModelRegex })))
            if (GetValidationError(pattern) is { } error) return error;
        if (pack.RegexRules.Any(r => string.IsNullOrWhiteSpace(r.ManufacturerRegex) && string.IsNullOrWhiteSpace(r.ModelRegex)))
            return "Enter a manufacturer or model expression for every regex rule.";
        return null;
    }

    public static string? GetValidationError(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        if (pattern.Length > 500) return "Regex patterns are limited to 500 characters.";
        try
        {
            _ = Expression(pattern);
        }
        catch (ArgumentException)
        {
            return "Enter a valid .NET regular expression.";
        }

        return null;
    }

    public static void Validate(string? pattern)
    {
        var error = GetValidationError(pattern);
        if (error is not null)
            throw new InvalidOperationException(error);
    }

    public static bool Matches(DriverMatchRuleDto rule, string? manufacturer, string? model)
    {
        manufacturer = manufacturer?.Trim() ?? "";
        model = model?.Trim() ?? "";
        if (rule.Models.Any(x => string.Equals(x.Manufacturer.Trim(), manufacturer, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(x.Model.Trim(), model, StringComparison.OrdinalIgnoreCase))) return true;
        if (string.IsNullOrWhiteSpace(rule.ManufacturerRegex) && string.IsNullOrWhiteSpace(rule.ModelRegex)) return false;
        try
        {
            return (string.IsNullOrWhiteSpace(rule.ManufacturerRegex) || Expression(rule.ManufacturerRegex).IsMatch(manufacturer)) &&
                   (string.IsNullOrWhiteSpace(rule.ModelRegex) || Expression(rule.ModelRegex).IsMatch(model));
        }
        catch (RegexMatchTimeoutException)
        {
            throw new InvalidOperationException("A matching expression took too long. Simplify the regex before using it.");
        }
    }

    private static Regex Expression(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
