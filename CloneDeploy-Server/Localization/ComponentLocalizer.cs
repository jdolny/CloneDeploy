using Microsoft.Extensions.Localization;
using MudBlazor;

namespace CloneDeploy_Server.Localization;

/// <summary>Uses the same account culture for built-in table, filter, date picker and dialog text.</summary>
public sealed class ComponentLocalizer(IStringLocalizer<LocalizationResources> resources) : MudLocalizer
{
    public override LocalizedString this[string key] => resources["MudBlazor." + key];
    public override LocalizedString this[string key, params object[] arguments] => resources["MudBlazor." + key, arguments];
}

public sealed class ComponentEnumLocalizer : ILocalizationEnumInterceptor
{
    public string Handle(System.Enum value) => UiText.Label(value);
}
