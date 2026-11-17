using System.Xml.Linq;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Server.Backend.Services;

public static class ImagePrepAnswerGenerator
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    public static string? Validate(ImagePrepGeneratorOptions options, IReadOnlyDictionary<int, string> usernames)
    {
        if (options.MarkerId == Guid.Empty || options.Architecture is not ("amd64" or "arm64")) return "Choose an x64 or ARM64 architecture.";
        if (options.LocalAccounts is null || options.LocalAccounts.Any(x => x is null)) return "Invalid local account settings.";
        if (options.LocalAccounts.Count > 20) return "A maximum of 20 local accounts is supported.";
        if (options.CredentialIds().Any(id => !usernames.ContainsKey(id))) return "A selected managed credential no longer exists.";
        if (options.LocalAccounts.Select(x => x.CredentialId).Distinct().Count() != options.LocalAccounts.Count) return "Select each local account credential only once.";
        var names = options.LocalAccounts.Select(x => usernames[x.CredentialId]).ToList();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) return "Local account names must be unique.";
        if (names.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 20 || x.Any(c => char.IsControl(c) || "\"/\\[]:;|=,+*?<>@".Contains(c)) || x.EndsWith('.') || x.Equals("Administrator", StringComparison.OrdinalIgnoreCase)))
            return "Local account credentials require a plain local username (1-20 characters). Use the separate built-in Administrator option for Administrator.";
        if (options.AdministratorCredentialId is { } admin && !usernames[admin].Equals("Administrator", StringComparison.OrdinalIgnoreCase))
            return "The built-in Administrator credential must have username Administrator.";
        if (options.AutoLogonCredentialId is { } logon && logon != options.AdministratorCredentialId && !options.LocalAccounts.Any(x => x.CredentialId == logon))
            return "Automatic logon must use one of the configured local accounts.";
        if (options.AutoLogonCredentialId is { } auto && options.LocalAccounts.Any(x => x.CredentialId == auto && !x.Administrator))
            return "Automatic logon requires an administrator account to apply first-logon settings.";
        if (options.AutoLogonCount is < 1 or > 999) return "Automatic logon count must be between 1 and 999.";
        if (options.ProtectYourPc is not (null or 1 or 2 or 3)) return "Choose a valid privacy/express settings option.";
        if (options.DomainCredentialId.HasValue != !string.IsNullOrWhiteSpace(options.JoinDomain)) return "Domain join requires both a domain and a managed credential.";
        if (options.DomainCredentialId is { } domain && (string.IsNullOrWhiteSpace(usernames[domain]) || usernames[domain].Any(char.IsControl))) return "Choose a domain credential with a valid username.";
        if (options.CredentialIds().Any(id => id <= 0)) return "Choose an existing managed credential.";
        var fields = new[] { options.Language, options.InputLocale, options.SystemLocale, options.UserLocale, options.TimeZone,
            options.RegisteredOwner, options.RegisteredOrganization, options.ProductKey, options.JoinDomain, options.OrganizationalUnit };
        if (fields.Any(x => x?.Length > 1000 || x?.Any(char.IsControl) == true)) return "Generator values must be printable text of at most 1000 characters.";
        if (!string.IsNullOrWhiteSpace(options.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(options.TimeZone, out _)) return "Enter a valid Windows time zone ID.";
        return null;
    }

    public static string Generate(ImagePrepGeneratorOptions o, IReadOnlyDictionary<int, string> usernames)
    {
        var error = Validate(o, usernames);
        if (error is not null) throw new InvalidOperationException(error);
        XElement Component(string name, params object?[] children) => new(Ns + "component", new XAttribute("name", name),
            new XAttribute("processorArchitecture", o.Architecture), new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"), children);
        XElement? Value(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : new(Ns + name, value.Trim());
        XElement Password(int id) => new(Ns + "Password", new XElement(Ns + "Value", o.Marker(id)), new XElement(Ns + "PlainText", true));
        var specialize = new XElement(Ns + "settings", new XAttribute("pass", "specialize"),
            Component("Microsoft-Windows-Shell-Setup", new XElement(Ns + "ComputerName", "*"),
                Value("TimeZone", o.TimeZone), Value("RegisteredOwner", o.RegisteredOwner),
                Value("RegisteredOrganization", o.RegisteredOrganization), Value("ProductKey", o.ProductKey)));
        if (o.DomainCredentialId is { } domain)
        {
            var username = usernames[domain];
            var parts = username.Split('\\', 2);
            specialize.Add(Component("Microsoft-Windows-UnattendedJoin", new XElement(Ns + "Identification",
                new XElement(Ns + "Credentials", new XElement(Ns + "Domain", parts.Length == 2 ? parts[0] : o.JoinDomain),
                    new XElement(Ns + "Username", parts.Length == 2 ? parts[1] : username), new XElement(Ns + "Password", o.Marker(domain))),
                Value("JoinDomain", o.JoinDomain), Value("MachineObjectOU", o.OrganizationalUnit))));
        }
        var shell = Component("Microsoft-Windows-Shell-Setup", new XElement(Ns + "OOBE",
            o.HideEula ? new XElement(Ns + "HideEULAPage", true) : null,
            o.HideOemRegistration ? new XElement(Ns + "HideOEMRegistrationScreen", true) : null,
            o.HideOnlineAccountScreens ? new XElement(Ns + "HideOnlineAccountScreens", true) : null,
            o.HideWirelessSetup ? new XElement(Ns + "HideWirelessSetupInOOBE", true) : null,
            o.ProtectYourPc is { } privacy ? new XElement(Ns + "ProtectYourPC", privacy) : null));
        if (o.LocalAccounts.Count > 0 || o.AdministratorCredentialId.HasValue)
        {
            var accounts = new XElement(Ns + "UserAccounts");
            if (o.AdministratorCredentialId is { } admin)
                accounts.Add(new XElement(Ns + "AdministratorPassword", new XElement(Ns + "Value", o.Marker(admin)), new XElement(Ns + "PlainText", true)));
            if (o.LocalAccounts.Count > 0)
                accounts.Add(new XElement(Ns + "LocalAccounts", o.LocalAccounts.Select(account => new XElement(Ns + "LocalAccount",
                    new XAttribute(Wcm + "action", "add"), new XElement(Ns + "Name", usernames[account.CredentialId]),
                    new XElement(Ns + "Group", account.Administrator ? "Administrators" : "Users"), Password(account.CredentialId)))));
            shell.Add(accounts);
        }
        if (o.AutoLogonCredentialId is { } auto)
            shell.Add(new XElement(Ns + "AutoLogon", Password(auto), new XElement(Ns + "Enabled", true),
                new XElement(Ns + "Username", usernames[auto]), new XElement(Ns + "Domain", "."), new XElement(Ns + "LogonCount", Math.Max(1, o.AutoLogonCount - 1))));
        if (o.AutoLogonCredentialId.HasValue && o.AutoLogonCount == 1)
            shell.Add(new XElement(Ns + "FirstLogonCommands", new XElement(Ns + "SynchronousCommand", new XAttribute(Wcm + "action", "add"),
                new XElement(Ns + "Order", 1), new XElement(Ns + "CommandLine", @"reg add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v AutoLogonCount /t REG_DWORD /d 0 /f"))));
        var oobe = new XElement(Ns + "settings", new XAttribute("pass", "oobeSystem"), shell);
        if (new[] { o.Language, o.InputLocale, o.SystemLocale, o.UserLocale }.Any(x => !string.IsNullOrWhiteSpace(x)))
            oobe.Add(Component("Microsoft-Windows-International-Core", Value("UILanguage", o.Language), Value("InputLocale", o.InputLocale),
                Value("SystemLocale", o.SystemLocale), Value("UserLocale", o.UserLocale)));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(Ns + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", Wcm), specialize, oobe)).ToString();
    }
}
