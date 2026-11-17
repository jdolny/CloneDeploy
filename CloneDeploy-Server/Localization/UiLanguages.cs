using System.Globalization;
using System.Security.Claims;
using CloneDeploy_Server.Backend.Data;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Localization;

public static class UiLanguages
{
    // Only offer languages with a shipped translation. Neutral resources provide English fallback.
    public static IReadOnlyList<CultureInfo> Supported { get; } = Discover();
    public static bool IsSupported(string? language) => string.IsNullOrEmpty(language) ||
        Supported.Any(x => x.Name.Equals(language, StringComparison.OrdinalIgnoreCase));
    public static string? Normalize(string? language) => string.IsNullOrWhiteSpace(language) ? null :
        Supported.FirstOrDefault(x => x.Name.Equals(language, StringComparison.OrdinalIgnoreCase))?.Name;

    private static IReadOnlyList<CultureInfo> Discover()
    {
        return UiResourceCultures.Names.Select(CultureInfo.GetCultureInfo)
            .OrderBy(c => c.NativeName).ToArray();
    }
}

public sealed class UserRequestCultureProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
    {
        // Agent/imaging protocols and their machine-readable values do not use account UI preferences.
        if (context.User.Identity?.IsAuthenticated != true ||
            context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/ui") ||
            context.Request.Path.StartsWithSegments("/_framework") || context.Request.Path.StartsWithSegments("/_content") ||
            Path.HasExtension(context.Request.Path.Value) ||
            !int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return null;
        await using var db = await context.RequestServices.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var language = await db.AppUserOptions.AsNoTracking().Where(x => x.AppUserId == userId)
            .Select(x => x.Language).FirstOrDefaultAsync(context.RequestAborted);
        var selected = UiLanguages.Normalize(language);
        return selected is null ? null : new ProviderCultureResult(selected);
    }
}
