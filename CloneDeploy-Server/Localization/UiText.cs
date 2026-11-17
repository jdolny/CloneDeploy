using System.Globalization;
using System.Resources;

namespace CloneDeploy_Server.Localization;

/// <summary>Resource lookup for presentation helpers that are not Blazor components.</summary>
public static class UiText
{
    private static readonly ResourceManager Resources = new(typeof(LocalizationResources));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> EnglishKeys = new(() => Resources
        .GetResourceSet(CultureInfo.InvariantCulture, true, true)!.Cast<System.Collections.DictionaryEntry>()
        .Where(entry => entry.Value is string)
        .GroupBy(entry => (string)entry.Value!, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => (string)group.First().Key, StringComparer.Ordinal));

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string Get(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    // Use only for application-owned display labels, never user-entered names, identifiers or protocol values.
    public static string Label(string? value) => value is not null && EnglishKeys.Value.TryGetValue(value, out var key)
        ? Get(key) : value ?? "";

    public static string Label(System.Enum? value) => value is null ? "" :
        Resources.GetString("Enum." + value.GetType().FullName + "." + value, CultureInfo.CurrentUICulture)
        ?? Label(System.Text.RegularExpressions.Regex.Replace(value.ToString(), "([a-z])([A-Z])", "$1 $2"));

    public static string Label(object? value) => value switch
    {
        System.Enum item => Label(item),
        string text => Label(text),
        bool flag => Get(flag ? LocKeys.Common.Yes : LocKeys.Common.No),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""
    };

    public static IReadOnlyDictionary<string, string> BrowserResources() => Resources
        .GetResourceSet(CultureInfo.InvariantCulture, true, true)!.Cast<System.Collections.DictionaryEntry>()
        .Where(entry => ((string)entry.Key).StartsWith("Js.", StringComparison.Ordinal))
        .ToDictionary(entry => (string)entry.Key, entry => Get((string)entry.Key));
}
