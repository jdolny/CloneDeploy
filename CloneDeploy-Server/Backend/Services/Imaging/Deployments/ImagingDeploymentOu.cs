using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class ImagingDeploymentClaims
{
    public ImagingComputerAccountLookup? ComputerAccounts { get; init; }

    public async Task<ImagingOuPlan> ResolveOuAsync(string? token, ImagingOuRequest request, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var deployment = await RequireExecutionAsync(db, token, ct);
        if (deployment.ImagerStartedAtUtc is null) throw new InvalidOperationException("Deployment has not started.");
        var settings = JsonSerializer.Deserialize<DeploymentSettingsDto>(deployment.SettingsJson)!;
        if (settings.OuNamingRuleSetId is null && settings.OuPrompt == DeploymentOuPrompt.Never)
            throw new InvalidOperationException("OU selection is not configured for this deployment.");
        var domain = request.DomainName?.Trim().TrimEnd('.');
        if (domain is null || domain.Length > 253 || Uri.CheckHostName(domain) != UriHostNameType.Dns ||
            request.SelectedOu?.Length > 2000)
            throw new InvalidOperationException("Invalid domain or OU selection.");
        var name = settings.SetComputerName ? settings.ComputerName : request.ComputerName;
        if (ComputerNameValidation.Error(name) is { } nameError)
            return new(false, false, null, nameError, []);
        var catalog = await OuNamingResolver.CatalogAsync(db, ct);
        OuNamingRuleSet? rules = null;
        OuNamingResult result = new(true, null, null, "No naming rules selected. Use domain default / existing account location.");
        if (settings.OuNamingRuleSetId is { } id)
        {
            rules = await db.OuNamingRuleSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (rules is null || !rules.Enabled || !rules.DomainName.Equals(domain, StringComparison.OrdinalIgnoreCase))
                return new(false, false, null, "OU naming rules are missing, disabled, or belong to another domain.", []);
        }
        var lookup = ComputerAccounts ?? throw new InvalidOperationException("AD computer account lookup is unavailable.");
        var existingAccount = await lookup.FindAsync(db, catalog, rules?.DirectorySourceId, domain, name, ct);
        if (existingAccount is not null)
            return new(true, false, null, $"Existing AD computer account found: {existingAccount}. Keeping its location; OU selection skipped.", []);
        if (rules is not null)
        {
            if (OuNamingResolver.Validate(rules, catalog) is { } error)
                return new(false, false, null, error, []);
            result = OuNamingResolver.Evaluate(rules, name, catalog);
            // A regex timeout is an error, not a no-match eligible for prompting.
            if (!result.Success && result.MatchedRule is not null)
                return new(false, false, null, result.Message, []);
        }
        var prompt = settings.OuPrompt == DeploymentOuPrompt.Always ||
            settings.OuPrompt == DeploymentOuPrompt.IfNoMatch && result.MatchedRule is null;
        var choices = prompt ? catalog.Domains.Where(d => d.Name.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
                (rules is null || d.SourceId == rules.DirectorySourceId))
            .SelectMany(d => catalog.Nodes.Where(n => n.SourceId == d.SourceId && n.IsOu && n.Available &&
                catalog.Domains.Where(parent => parent.SourceId == n.SourceId && n.Path.EndsWith("," + parent.Path, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(parent => parent.Path.Length).FirstOrDefault()?.Name == d.Name))
            .Select(n => n.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)
            .Select(x => new ImagingOuChoice(x)).ToList() : [];
        if (request.SelectionMade)
        {
            if (!prompt) throw new InvalidOperationException("This deployment does not permit an OU override.");
            var selected = request.SelectedOu;
            if (selected is not null && !choices.Any(x => x.DistinguishedName.Equals(selected, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The selected OU is no longer available in this domain. Refresh the selection.");
            return new(true, false, selected, "OU selected at the Imager.", []);
        }
        var message = prompt && result.MatchedRule is null
            ? "No matching OU rule, select an OU"
            : result.Message;
        return new(result.Success || prompt, prompt, result.OrganizationalUnit, message, choices);
    }
}
