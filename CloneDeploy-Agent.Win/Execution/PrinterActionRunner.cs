using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

public sealed record PrinterRequest(string NetworkPath, PrinterActionType Operation, bool UserContext,
    bool SetDefault, bool RestartSpooler, bool WaitForEnumeration);

/// <summary>Runs blocking spooler RPCs in a killable child under the policy's existing identity.</summary>
public static class PrinterActionRunner
{
    public static Task<PolicyProcessResult> RunAsync(JsonElement settings, bool userContext,
        IPolicyProcess process, string directory, CancellationToken ct)
    {
        var request = new PrinterRequest(settings.Text("NetworkPath").Trim(),
            (PrinterActionType)settings.Number("Action"), userContext,
            settings.Flag("IsDefault"), settings.Flag("RestartSpooler"), settings.Flag("WaitForEnumeration"));
        if (Validate(request) is { } error) return Task.FromResult(new PolicyProcessResult(1, "", error));
        var executable = Path.Combine(AppContext.BaseDirectory, "CloneDeploy-Agent.exe");
        // The single-file portal is installed in the agent's UI subdirectory.
        if (!File.Exists(executable))
            executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "CloneDeploy-Agent.exe"));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--printer-action");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        // No run-as credentials, elevation, or driver staging. The child inherits this identity.
        return process.RunAsync(start, settings.Number("Timeout"), ct);
    }

    internal static string? Validate(PrinterRequest request)
    {
        if (PrinterOperation.Validate(request.NetworkPath, request.Operation, request.SetDefault) is { } error) return error;
        if (!request.UserContext && request.SetDefault)
            return "Set as default printer requires a login or requesting-user policy. No printer changes were made.";
        if (request.UserContext && request.RestartSpooler)
            return "User-context printer actions cannot restart the system spooler.";
        return null;
    }

    public static async Task<int> RunHelperAsync(string encoded)
    {
        try
        {
            if (encoded.Length > 16384) throw new InvalidDataException("Printer request is too large.");
            var request = JsonSerializer.Deserialize<PrinterRequest>(Convert.FromBase64String(encoded))
                ?? throw new InvalidDataException("Missing printer request.");
            if (Validate(request) is { } error) throw new InvalidDataException(error);
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (request.UserContext == identity.IsSystem)
                throw new InvalidOperationException("Printer scope does not match the policy execution identity.");
            var result = await new PrinterConnectionRunner(new WindowsPrinterConnection()).RunAsync(request, CancellationToken.None);
            if (result.Output.Length > 0) Console.WriteLine(result.Output);
            if (result.Error.Length > 0) Console.Error.WriteLine(result.Error);
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Printer action failed: {ex.Message}");
            return 1;
        }
    }
}

public interface IPrinterConnection
{
    bool Exists(string path, bool userContext);
    Task InstallAsync(string path, bool userContext, CancellationToken ct);
    Task RemoveAsync(string path, bool userContext, CancellationToken ct);
    void SetDefault(string path);
    string? GetDefault();
    void RestartSpooler();
}

/// <summary>Verifies the connection in the same scope that was changed.</summary>
public sealed class PrinterConnectionRunner(IPrinterConnection printers)
{
    public TimeSpan EnumerationInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int EnumerationAttempts { get; init; } = 24;

    public async Task<PolicyProcessResult> RunAsync(PrinterRequest request, CancellationToken ct)
    {
        if (PrinterActionRunner.Validate(request) is { } error) return new(1, "", error);
        try
        {
            ct.ThrowIfCancellationRequested();
            var operation = request.Operation;
            var path = request.NetworkPath;
            var exists = printers.Exists(path, request.UserContext);
            if (operation == PrinterActionType.Install && !exists)
                await printers.InstallAsync(path, request.UserContext, ct);
            else if (operation == PrinterActionType.Delete && exists)
                await printers.RemoveAsync(path, request.UserContext, ct);
            if (request.RestartSpooler) printers.RestartSpooler();

            if (operation != PrinterActionType.None || request.SetDefault)
            {
                var expected = operation != PrinterActionType.Delete;
                var attempts = request.WaitForEnumeration ? EnumerationAttempts : 1;
                for (var attempt = 0; ; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (printers.Exists(path, request.UserContext) == expected) break;
                    if (attempt + 1 >= attempts)
                        return new(1, "", $"Windows did not confirm printer {(expected ? "installation" : "removal")} in the {(request.UserContext ? "user" : "computer")} scope. Check the print server, driver requirements, and Windows printer policies.");
                    await Task.Delay(EnumerationInterval, ct);
                }
            }
            if (request.SetDefault)
            {
                printers.SetDefault(path);
                if (!string.Equals(printers.GetDefault(), path, StringComparison.OrdinalIgnoreCase))
                    return new(1, "", "Windows did not confirm the requested default printer for this user.");
            }
            var message = operation switch
            {
                PrinterActionType.Install when !request.UserContext => "Computer-wide printer connection registered. Windows applies it when users sign in; user driver installation can still be blocked by Windows policy.",
                PrinterActionType.Install => "Printer connection verified for the policy user.",
                PrinterActionType.Delete when !request.UserContext => "Computer-wide printer registration removed. Windows applies removal when users sign in.",
                PrinterActionType.Delete => "Printer connection removal verified for the policy user.",
                _ => "Printer settings applied."
            };
            return new(0, message + (request.SetDefault ? " Default printer verified." : ""), "");
        }
        catch (Win32Exception ex)
        {
            return new(ex.NativeErrorCode == 0 ? 1 : ex.NativeErrorCode, "",
                $"Windows printer operation failed ({ex.NativeErrorCode}): {ex.Message}. Drivers must be supplied by the print server and allowed by Windows policy under the current identity. No elevation was attempted.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(1, "", $"Printer action failed: {ex.Message}");
        }
    }
}
