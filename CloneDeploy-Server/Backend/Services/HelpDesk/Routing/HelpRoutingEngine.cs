using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record HelpRoutingInput(string Title, string Description, string DeviceName, string[] Addresses, int? CategoryId = null)
{
    public string? UserDirectoryKey { get; init; }
    public string? DeviceDirectoryKey { get; init; }
    public HashSet<string> UserGroups { get; init; } = [];
    public HashSet<string> UserOus { get; init; } = [];
    public HashSet<string> DeviceGroups { get; init; } = [];
    public HashSet<string> DeviceOus { get; init; } = [];
}
public sealed record HelpRoutingResult(int? CategoryId, int? QueueId, string Summary);

public static class HelpRoutingEngine
{
    public static HelpRuleCondition[] Conditions(HelpRoutingRule rule) =>
        JsonSerializer.Deserialize<HelpRuleCondition[]>(rule.ConditionsJson) ?? [];

    public static HelpRoutingResult Evaluate(IEnumerable<HelpRoutingRule> rules, HelpRoutingInput input)
    {
        var ordered = rules.Where(x => x.Enabled).OrderBy(x => x.Order).ThenBy(x => x.Id).ToArray();
        var category = ordered.FirstOrDefault(x => x.Stage == HelpRuleStage.Categorization && Matches(x, input));
        var categoryId = category?.CategoryId ?? input.CategoryId;
        var queue = ordered.FirstOrDefault(x => x.Stage == HelpRuleStage.Assignment && Matches(x, input with { CategoryId = categoryId }));
        return new(categoryId, queue?.QueueId ?? HelpQueue.UnassignedId,
            $"Category: {category?.Name ?? "no matching rule"}. Queue: {queue?.Name ?? "Unassigned (no matching rule)"}.");
    }

    public static bool Matches(HelpRoutingRule rule, HelpRoutingInput input)
    {
        var conditions = Conditions(rule);
        // An empty rule is an explicit catch-all, regardless of its match mode.
        return conditions.Length == 0 || (rule.MatchAll ? conditions.All(Match) : conditions.Any(Match));
        bool Match(HelpRuleCondition c)
        {
            if (c.Field == HelpRuleField.Category) return int.TryParse(c.Value, out var id) && input.CategoryId == id;
            if (c.Field == HelpRuleField.DeviceIp) return input.Addresses.Any(ip => InRange(ip, c.Value));
            return c.Field switch
            {
                HelpRuleField.UserDirectoryGroup => input.UserGroups.Contains(c.Value),
                HelpRuleField.UserDirectoryOu => input.UserOus.Contains(c.Value),
                HelpRuleField.DeviceDirectoryGroup => input.DeviceGroups.Contains(c.Value),
                HelpRuleField.DeviceDirectoryOu => input.DeviceOus.Contains(c.Value),
                HelpRuleField.Title => Text(input.Title, c),
                HelpRuleField.Description => Text(input.Description, c),
                HelpRuleField.TitleOrDescription => Text(input.Title, c) || Text(input.Description, c),
                HelpRuleField.DeviceName => Text(input.DeviceName, c),
                _ => false
            };
        }
    }

    private static bool Text(string text, HelpRuleCondition c) => c.Operator switch
    {
        HelpRuleOperator.Regex => RegexMatch(text, c.Value) == "Match",
        HelpRuleOperator.Contains => text.Contains(c.Value, StringComparison.OrdinalIgnoreCase),
        HelpRuleOperator.StartsWith => text.StartsWith(c.Value, StringComparison.OrdinalIgnoreCase),
        HelpRuleOperator.Equals => text.Equals(c.Value, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    public static bool IsDirectory(HelpRuleField field) => field is HelpRuleField.UserDirectoryGroup or HelpRuleField.UserDirectoryOu or HelpRuleField.DeviceDirectoryGroup or HelpRuleField.DeviceDirectoryOu;
    public static bool IsOu(HelpRuleField field) => field is HelpRuleField.UserDirectoryOu or HelpRuleField.DeviceDirectoryOu;
    private static Regex Pattern(string value) => new(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public static string? RegexError(string value)
    {
        try { _ = Pattern(value); return null; }
        catch (ArgumentException ex) { return "Invalid regular expression: " + ex.Message; }
    }
    public static string RegexMatch(string sample, string pattern)
    {
        try { return Pattern(pattern).IsMatch(sample) ? "Match" : "No match"; }
        catch (RegexMatchTimeoutException) { return "Pattern took too long. Simplify the expression."; }
        catch (ArgumentException) { return "Invalid regular expression."; }
    }

    public static bool ValidRange(string value) => TryRange(value, out _, out _);

    public static bool InRange(string address, string range)
    {
        if (!IPAddress.TryParse(address, out var ip) || !TryRange(range, out var start, out var end)) return false;
        var bytes = Normalize(ip).GetAddressBytes();
        return bytes.Length == start.Length && bytes.AsSpan().SequenceCompareTo(start) >= 0 && bytes.AsSpan().SequenceCompareTo(end) <= 0;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static bool TryRange(string value, out byte[] start, out byte[] end)
    {
        start = end = [];
        var range = value.Split('-', StringSplitOptions.TrimEntries);
        if (range.Length == 2 && IPAddress.TryParse(range[0], out var first) && IPAddress.TryParse(range[1], out var last))
        {
            start = Normalize(first).GetAddressBytes(); end = Normalize(last).GetAddressBytes();
            return start.Length == end.Length && start.AsSpan().SequenceCompareTo(end) <= 0;
        }
        var cidr = value.Split('/', StringSplitOptions.TrimEntries);
        if (cidr.Length != 2 || !IPAddress.TryParse(cidr[0], out var network) || !int.TryParse(cidr[1], out var prefix)) return false;
        start = Normalize(network).GetAddressBytes();
        if (prefix < 0 || prefix > start.Length * 8) return false;
        end = new byte[start.Length];
        for (var i = 0; i < start.Length; i++)
        {
            var mask = (byte)(255 << (8 - Math.Clamp(prefix - i * 8, 0, 8)));
            start[i] &= mask;
            end[i] = (byte)(start[i] | ~mask);
        }
        return true;
    }
}
