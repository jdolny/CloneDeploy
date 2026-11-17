namespace CloneDeploy_Agent.Contracts;

public sealed record ImagingOuRequest(string DomainName, string ComputerName)
{
    public bool SelectionMade { get; init; }
    public string? SelectedOu { get; init; }
}
public sealed record ImagingOuChoice(string DistinguishedName);
public sealed record ImagingOuPlan(bool Success, bool Prompt, string? OrganizationalUnit, string Message,
    List<ImagingOuChoice> Choices);
