using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using CloneDeploy.SiteServices;
using Microsoft.JSInterop;

namespace CloneDeploy_Server.UI.Pages.Administration.SiteServices;

internal static class SiteServicePresentation
{
    public static string Enrollment(SiteServiceDetails service) => UiText.Label(service.State == SiteRegistrationState.Created && service.TokenExpiresAtUtc < DateTime.UtcNow
        ? "Setup expired" : service.State.ToString());
    public static string Proxy(SiteServiceDetails service) => UiText.Label(service.State != SiteRegistrationState.Approved || !service.ProxyEnabled ? "Disabled"
        : service.LastContactAtUtc is null ? "Awaiting first contact"
        : service.LastContactAtUtc < DateTime.UtcNow.AddMinutes(-3) ? "Contact overdue" : service.ProxyRunning ? "Running" : "Starting / stopped");
    public static async Task DownloadAsync(IJSRuntime javascript, SiteEnrollmentPackage package)
    {
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(package, new JsonSerializerOptions { WriteIndented = true }));
        using var reference = new DotNetStreamReference(stream);
        await using var module = await javascript.InvokeAsync<IJSObjectReference>("import", "./js/effective-policy.js");
        await module.InvokeVoidAsync("download", reference, $"site-enrollment-{package.RegistrationId:N}.json");
    }
}
