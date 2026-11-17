using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Service-owned branding survives restarts and never needs network access for display.</summary>
public sealed class AgentBrandingCache(AgentOptions options)
{
    private const int MaximumOrganizationNameCharacters = 100;
    private const int MaximumTaglineCharacters = 160;
    private const int MaximumPortalTitleCharacters = 100;
    private const int MaximumHelpTabCharacters = 40;

    private string PathName => Path.Combine(options.StateDirectory, "branding.json");

    public AgentBranding Read()
    {
        try
        {
            return JsonSerializer.Deserialize<AgentBranding>(File.ReadAllText(PathName)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(AgentBranding value)
    {
        if (!AgentBranding.ValidColor(value.BackgroundColor) || !AgentBranding.ValidColor(value.AccentColor)
            || value.OrganizationName.Length > MaximumOrganizationNameCharacters
            || value.Tagline.Length > MaximumTaglineCharacters
            || value.PortalTitle?.Length > MaximumPortalTitleCharacters || value.HelpTabName?.Length > MaximumHelpTabCharacters
            || value.LogoPng?.Length > CloneDeploy_Agent.Contracts.AgentBranding.MaximumLogoBytes)
            throw new InvalidDataException("Invalid branding response.");

        Directory.CreateDirectory(options.StateDirectory);
        var temporary = $"{PathName}.{Guid.NewGuid().ToString("N")}.tmp";

        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, PathName, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
