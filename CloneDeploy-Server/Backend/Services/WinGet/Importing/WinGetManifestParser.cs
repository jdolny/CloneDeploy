using CloneDeploy_Server.Backend.Data.Models;
using YamlDotNet.Serialization;

namespace CloneDeploy_Server.Backend.Services;

// Typed scalar strings preserve versions such as 1.10 and identifiers without numeric coercion.
/// <summary>
/// Reads software descriptions and installer choices from WinGet catalog files.
/// </summary>
public sealed class WinGetManifestParser
{
    private readonly IDeserializer _yaml = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    public IReadOnlyList<object> Parse(TextReader reader)
    {
        var manifest = _yaml.Deserialize<Manifest>(reader) ?? throw new InvalidDataException("Empty manifest.");
        if (manifest.ManifestType is not ("version" or "installer" or "defaultLocale" or "locale" or "singleton"))
            throw new InvalidDataException("Unsupported or missing ManifestType.");
        if (string.IsNullOrWhiteSpace(manifest.PackageIdentifier) || string.IsNullOrWhiteSpace(manifest.PackageVersion))
            throw new InvalidDataException("Manifest must specify PackageIdentifier and PackageVersion.");

        var result = new List<object>();
        if (manifest.ManifestType is "version" or "singleton")
        {
            var locale = manifest.ManifestType == "singleton" ? manifest.PackageLocale : manifest.DefaultLocale;
            if (string.IsNullOrWhiteSpace(locale)) throw new InvalidDataException("Version manifest has no default locale.");
            result.Add(new WinGetVersionManifest
            {
                PackageIdentifier = manifest.PackageIdentifier, PackageVersion = manifest.PackageVersion, DefaultLocale = locale
            });
        }
        if (manifest.ManifestType is "installer" or "singleton")
        {
            if (manifest.Installers is not { Count: > 0 }) throw new InvalidDataException("Installer manifest has no installers.");
            var scopes = manifest.Installers.Select(x => x.Scope ?? manifest.Scope).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var user = scopes.Contains("user");
            var machine = scopes.Contains("machine");
            result.Add(new WinGetInstallerManifest
            {
                PackageIdentifier = manifest.PackageIdentifier, PackageVersion = manifest.PackageVersion,
                Scope = user && machine ? "Both" : user ? "User" : machine ? "Machine" : "Not Specified"
            });
        }
        if (manifest.ManifestType is "locale" or "defaultLocale" or "singleton")
        {
            if (string.IsNullOrWhiteSpace(manifest.PackageLocale)) throw new InvalidDataException("Locale manifest has no PackageLocale.");
            if (!string.Equals(manifest.PackageLocale, "en-US", StringComparison.OrdinalIgnoreCase)) return result;
            var parts = manifest.PackageVersion.Split('.');
            int Part(int index) => index < parts.Length && int.TryParse(parts[index], out var number) ? number : 0;
            result.Add(new WinGetLocaleManifest
            {
                PackageIdentifier = manifest.PackageIdentifier, PackageVersion = manifest.PackageVersion,
                Locale = manifest.PackageLocale, ManifestType = manifest.ManifestType,
                Publisher = manifest.Publisher, PublisherUrl = manifest.PublisherUrl,
                PackageName = manifest.PackageName, PackageUrl = manifest.PackageUrl, License = manifest.License,
                ShortDescription = manifest.ShortDescription, Moniker = manifest.Moniker,
                Tags = manifest.Tags is null ? null : string.Join(" ", manifest.Tags),
                Major = Part(0), Minor = Part(1), Build = Part(2), Revision = Part(3)
            });
        }
        return result;
    }

    /// <summary>
    /// Holds the software package details read from a WinGet catalog file.
    /// </summary>
    private sealed class Manifest
    {
        public string? ManifestType { get; set; }
        public string? PackageIdentifier { get; set; }
        public string? PackageVersion { get; set; }
        public string? DefaultLocale { get; set; }
        public string? PackageLocale { get; set; }
        public string? Scope { get; set; }
        public List<Installer>? Installers { get; set; }
        public string? Publisher { get; set; }
        public string? PublisherUrl { get; set; }
        public string? PackageName { get; set; }
        public string? PackageUrl { get; set; }
        public string? License { get; set; }
        public string? ShortDescription { get; set; }
        public string? Moniker { get; set; }
        public List<string>? Tags { get; set; }
    }

    /// <summary>
    /// Holds the download and installation details for a WinGet installer choice.
    /// </summary>
    private sealed class Installer
    {
        public string? Scope { get; set; }
    }
}
