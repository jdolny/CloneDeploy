using System.Text.Json;
using System.Text.RegularExpressions;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record OuNamingDomain(int SourceId, string SourceName, string Name, string Path);
public sealed record OuNamingCatalog(List<OuNamingDomain> Domains, List<PolicyTargetDirectoryNodeDto> Nodes);
public sealed record OuNamingResult(bool Success, string? OrganizationalUnit, string? MatchedRule, string Message);

// Shared internal resolver for future imaging and agent callers. Browser entry points live in OuNamingRuleService.
public sealed class OuNamingResolver(ICloneDeployDbFactory factory)
{
    public async Task<OuNamingResult> ResolveAsync(int ruleSetId, string domainName, string computerName, CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        var set = await db.OuNamingRuleSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ruleSetId, ct);
        if (set is null || !set.Enabled) return new(false, null, null, "OU naming rule set is missing or disabled.");
        if (!string.Equals(set.DomainName, domainName.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            return new(false, null, null, "OU naming rule set does not belong to the target domain.");
        return Evaluate(set, computerName, await CatalogAsync(db, ct));
    }

    internal static async Task<OuNamingCatalog> CatalogAsync(CloneDeployDbContext db, CancellationToken ct = default)
    {
        var objects = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource)
            .Where(x => !x.IsMissing && x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly &&
                x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory &&
                (x.Kind == DirectoryObjectKind.Domain || x.Kind == DirectoryObjectKind.Container || x.Kind == DirectoryObjectKind.OrganizationalUnit))
            .OrderBy(x => x.DistinguishedName).ToListAsync(ct);
        var domains = objects.Where(x => x.Kind == DirectoryObjectKind.Domain)
            .Select(x => new OuNamingDomain(x.DirectorySourceId, x.DirectorySource.Name,
                string.Join('.', x.DistinguishedName.Split(',').Select(p => p.Trim()).Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p[3..])).ToLowerInvariant(), x.DistinguishedName))
            .Where(x => Uri.CheckHostName(x.Name) == UriHostNameType.Dns).ToList();
        return new(domains, objects.Select(x => new PolicyTargetDirectoryNodeDto(x.DirectorySourceId, x.DirectorySource.Name,
            x.ObjectGuid, x.ParentGuid, x.Name, x.DistinguishedName, x.Kind == DirectoryObjectKind.OrganizationalUnit, true)).ToList());
    }

    internal static PolicyTargetDirectoryNodeDto? FindOu(OuNamingRuleSet set, Guid? id, OuNamingCatalog catalog)
    {
        var domain = catalog.Domains.FirstOrDefault(x => x.SourceId == set.DirectorySourceId && x.Name.Equals(set.DomainName, StringComparison.OrdinalIgnoreCase));
        return domain is null ? null : catalog.Nodes.FirstOrDefault(x => x.SourceId == set.DirectorySourceId && x.Guid == id && x.IsOu && x.Available &&
            catalog.Domains.Where(d => d.SourceId == x.SourceId && x.Path.EndsWith("," + d.Path, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.Path.Length).FirstOrDefault()?.Name == domain.Name);
    }

    public static string? Validate(OuNamingRuleSet set, OuNamingCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(set.Name) || set.Name.Length > 200 || set.Name.Any(char.IsControl)) return "Enter a rule set name of up to 200 printable characters.";
        if (!catalog.Domains.Any(x => x.SourceId == set.DirectorySourceId && x.Name.Equals(set.DomainName, StringComparison.OrdinalIgnoreCase)))
            return "Choose a domain from an enabled, synchronized Active Directory source.";
        if (!System.Enum.IsDefined(set.Fallback)) return "Choose a valid no-match fallback.";
        if (set.Fallback == OuNoMatch.SpecificOu && FindOu(set, set.FallbackOuGuid, catalog) is null) return "Choose an available fallback OU in the selected domain.";
        if (set.RulesJson is null || set.RulesJson.Length > 131072) return "Rule configuration is too large.";
        List<OuNamingRule> rules;
        try { rules = set.Rules; } catch (JsonException) { return "Invalid rule configuration."; }
        if (rules.Count > 100) return "A rule set supports at most 100 rules.";
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 200 || rule.Name.Any(char.IsControl)) return "Give every rule a name of up to 200 printable characters.";
            if (!System.Enum.IsDefined(rule.Match) || string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern.Length > 256 || rule.Pattern.Any(char.IsControl)) return $"Rule '{rule.Name}' needs a valid pattern of up to 256 characters.";
            try { _ = Pattern(rule); } catch (ArgumentException) { return $"Rule '{rule.Name}' has an invalid regular expression."; }
            if (FindOu(set, rule.OuGuid, catalog) is null) return $"Choose an available OU in the selected domain for rule '{rule.Name}'.";
        }
        return null;
    }

    public static OuNamingResult Evaluate(OuNamingRuleSet set, string computerName, OuNamingCatalog catalog)
    {
        var error = Validate(set, catalog) ?? ComputerNameValidation.Error(computerName);
        if (error is not null) return new(false, null, null, error);
        foreach (var rule in set.Rules)
        {
            try
            {
                if (Pattern(rule).IsMatch(computerName)) return new(true, FindOu(set, rule.OuGuid, catalog)!.Path, rule.Name, $"Matched rule '{rule.Name}'.");
            }
            catch (RegexMatchTimeoutException) { return new(false, null, rule.Name, $"Rule '{rule.Name}' exceeded the regex time limit. Resolution stopped."); }
        }
        return set.Fallback switch
        {
            OuNoMatch.DomainDefault => new(true, null, null, "No rule matched. Use domain default / existing account location."),
            OuNoMatch.SpecificOu => new(true, FindOu(set, set.FallbackOuGuid, catalog)!.Path, null, "No rule matched. Use the fallback OU."),
            _ => new(false, null, null, "No rule matched. The configured fallback is to fail.")
        };
    }

    private static Regex Pattern(OuNamingRule rule) => new(rule.Match == OuNameMatch.Wildcard
        ? "\\A" + Regex.Escape(rule.Pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z" : rule.Pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));
}
