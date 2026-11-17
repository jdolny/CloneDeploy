using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text;

[assembly: NeutralResourcesLanguage("en")]
namespace CloneDeploy_Agent.Localization;

/// <summary>Built-in portal text only. Never pass administrator-authored content through this class.</summary>
public static class PortalText
{
    private static readonly ResourceManager Resources = new("CloneDeploy_Agent.Localization.PortalResources", typeof(PortalText).Assembly);
    public static CultureInfo SelectCulture(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return CultureInfo.GetCultureInfo("en");
        try
        {
            var culture = CultureInfo.GetCultureInfo(language.Replace('_', '-'));
            return CultureInfo.GetCultureInfo(culture.TwoLetterISOLanguageName switch
            {
                "es" => "es", "fr" => "fr", "de" => "de", "pt" => "pt-BR", "sv" => "sv", _ => "en"
            });
        }
        catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en"); }
    }

    public static void Initialize()
    {
        var language = OperatingSystem.IsMacOS() ? MacDisplayLanguage() : CultureInfo.CurrentUICulture.Name;
        var culture = SelectCulture(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        // Preserve the user's regional date and number formatting independently of translations.
    }

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing portal resource: {key}");
    public static string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    private static string MacDisplayLanguage()
    {
        var languages = CFLocaleCopyPreferredLanguages();
        if (languages == IntPtr.Zero) return CultureInfo.CurrentUICulture.Name;
        try
        {
            if (CFArrayGetCount(languages) == 0) return CultureInfo.CurrentUICulture.Name;
            var language = CFArrayGetValueAtIndex(languages, 0);
            var bytes = new byte[256];
            if (!CFStringGetCString(language, bytes, bytes.Length, 0x08000100)) return CultureInfo.CurrentUICulture.Name;
            var end = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
        }
        finally { CFRelease(languages); }
    }
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(CoreFoundation)] private static extern IntPtr CFLocaleCopyPreferredLanguages();
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint capacity, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
}
