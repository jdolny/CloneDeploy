using CloneDeploy_Imager;
using CloneDeploy_Imager.Configuration;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.UI;

const int CancelledExitCode = 130;

if (args.Contains("--help"))
{
    Console.WriteLine("CloneDeploy-Imager [--config <file>] [--plain] [--inventory]");
    Console.WriteLine("--inventory prints read-only discovery as JSON.");
    return 0;
}
var started = false;
try
{
    var options = LaunchOptions.Parse(args);
    var settings = ImagerSettings.Load(options.ConfigPath);
    using var log = new DiagnosticLog(settings.LogDirectory);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    if (!options.Inventory) ConsoleWindow.TryEnterFullScreen(log);
    var ui = new ImagerConsole(options.Plain);
    started = true;
    try
    {
        log.Write("Imager started.");
        return await new ImagerApplication(settings, ui, log).RunAsync(options, cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        log.Write("Operation cancelled.");
        return CancelledExitCode;
    }
    catch (Exception exception)
    {
        log.Failure("Application", exception);
        ui.Error(ImagerApplication.Describe(exception), log.FilePath);
        return 1;
    }
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine((started ? "Imager failed: " : "Could not start Imager: ") + exception.Message);
    return 1;
}
