using System.Globalization;

namespace CloneDeploy_Server.UI.Pages.Imaging.ImagePrepProfiles;

public static class ImagePrepRegionalOptions
{
    public sealed record Choice(string Value, string Label);

    public static IReadOnlyList<Choice> Languages => Locales(
        "ar-SA", "bg-BG", "zh-CN", "zh-TW", "hr-HR", "cs-CZ", "da-DK", "nl-NL",
        "en-US", "en-GB", "et-EE", "fi-FI", "fr-FR", "fr-CA", "de-DE", "el-GR",
        "he-IL", "hi-IN", "hu-HU", "id-ID", "it-IT", "ja-JP", "ko-KR", "lv-LV",
        "lt-LT", "nb-NO", "pl-PL", "pt-BR", "pt-PT", "ro-RO", "ru-RU", "sk-SK",
        "sl-SI", "es-ES", "es-MX", "sv-SE", "th-TH", "tr-TR", "uk-UA", "vi-VN");

    public static IReadOnlyList<Choice> Regions => Languages.Concat(Locales(
        "en-AU", "en-CA", "en-IN", "en-IE", "en-NZ", "en-ZA", "de-AT", "de-CH",
        "fr-BE", "fr-CH", "nl-BE", "es-AR", "es-CL", "es-CO", "zh-HK"))
        .OrderBy(x => x.Label, StringComparer.Ordinal).ToArray();

    // Language tags select Windows' default input method, including IMEs.
    // https://learn.microsoft.com/windows-hardware/customize/desktop/unattend/microsoft-windows-international-core-inputlocale
    public static IReadOnlyList<Choice> Keyboards => Regions
        .Select(x => new Choice(x.Value, UiText.Get(LocKeys.Ui.ImagePrepRegionalOptions.Text0DefaultKeyboardInputMethod, x.Label)))
        .Concat(new Choice[] {
            new("0409:00000409", UiText.Get(LocKeys.Ui.ImagePrepRegionalOptions.USQWERTY)),
            new("0409:00020409", UiText.Get(LocKeys.Ui.ImagePrepRegionalOptions.USInternational)),
            new("0409:00010409", UiText.Get(LocKeys.Ui.ImagePrepRegionalOptions.USDvorak))
        }).OrderBy(x => x.Label, StringComparer.Ordinal).ToArray();

    private static Choice[] Locales(params string[] tags) => tags
        .Select(tag => new Choice(tag, CultureInfo.GetCultureInfo(tag).NativeName))
        .OrderBy(x => x.Label, StringComparer.Ordinal).ToArray();
}
