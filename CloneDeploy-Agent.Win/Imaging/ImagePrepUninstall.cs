using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CloneDeploy_Agent;

internal static class ImagePrepUninstall
{
    private const int NoMoreItems = 259;
    private const int InstallationPathBufferCharacters = 32768;

    private static readonly string[] UpgradeCodes = ["{D75325AF-F72B-4039-9B5D-DBAA9E8AB0F5}", "{620CDB5F-90AA-4381-9FAA-6546CBC2E350}"];
    internal sealed record InstalledProduct(Guid ProductCode, string Location);

    internal static Guid FindProduct(string installDirectory)
    {
        var products = new List<InstalledProduct>();
        foreach (var upgrade in UpgradeCodes)
            for (uint index = 0; ; index++)
            {
                var code = new StringBuilder(39);
                var result = MsiEnumRelatedProducts(upgrade, 0, index, code);
                if (result == NoMoreItems) break;
                if (result != 0) throw new InvalidOperationException($"Cannot inspect the installed agent MSI ({result}).");
                var location = new StringBuilder(InstallationPathBufferCharacters);
                uint length = (uint)location.Capacity;
                if (MsiGetProductInfo(code.ToString(), "InstallLocation", location, ref length) == 0 && Guid.TryParse(code.ToString(), out var product))
                    products.Add(new(product, location.ToString()));
            }
        return MatchProduct(installDirectory, products);
    }

    internal static Guid MatchProduct(string installDirectory, IEnumerable<InstalledProduct> products)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        var matches = products.Where(x => !string.IsNullOrWhiteSpace(x.Location) &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.Location)).Equals(path, StringComparison.OrdinalIgnoreCase)).Select(x => x.ProductCode).Distinct().ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Agent uninstall requires a registered CloneDeploy MSI installed at this agent's location. Install or repair the agent MSI before preparing this image.");
        return matches[0];
    }

    internal static async Task LaunchAsync(string stateDirectory, Guid product, bool capture)
    {
        PolicyActionRunner.RejectReparsePoints(stateDirectory);
        AgentDiagnosticLogs.Prepare(stateDirectory);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "CloneDeploy-ImagePrep-Uninstall-" + Guid.NewGuid().ToString("N"));
        PolicyActionRunner.RejectReparsePoints(folder);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).Create(acl);
        var powerAction = ImagePrepPowerAction.Load(stateDirectory);
        var config = Path.Combine(folder, "request.json");
        var script = Path.Combine(folder, "uninstall.ps1");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            ProductCode = product, StateDirectory = Path.GetFullPath(stateDirectory), InstallDirectory = Path.GetFullPath(AppContext.BaseDirectory),
            LogDirectory = AgentDiagnosticLogs.DirectoryFor(stateDirectory),
            FinalizerProcessId = Environment.ProcessId, Capture = capture, powerAction.RunSysprep, powerAction.UseAnswerFile
        }));
        await File.WriteAllTextAsync(script, Script, new UTF8Encoding(true));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-RequestPath", config })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("Could not start the image prep uninstall helper.");
    }

    internal const string Script = """
        param([Parameter(Mandatory=$true)][string]$RequestPath)
        $ErrorActionPreference = 'Stop'
        $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
        $state = [IO.Path]::GetFullPath($request.StateDirectory).TrimEnd('\')
        $installed = [IO.Path]::GetFullPath($request.InstallDirectory).TrimEnd('\')
        if ($state -eq [IO.Path]::GetPathRoot($state).TrimEnd('\') -or $installed -eq [IO.Path]::GetPathRoot($installed).TrimEnd('\')) { throw 'Invalid agent directory.' }
        $product = ([guid]$request.ProductCode).ToString('B')
        $logDirectory = [IO.Path]::GetFullPath($request.LogDirectory).TrimEnd('\')
        if ($logDirectory -ine ($state + 'Logs')) { throw 'Invalid agent diagnostic directory.' }
        $log = Join-Path $logDirectory 'image-prep-finalization.log'
        function Assert-NoLinks([string]$path) {
            for ($current = $path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
                if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Agent cleanup paths cannot contain links.' }
            }
        }
        try {
            Assert-NoLinks $state
            Assert-NoLinks $installed
            Assert-NoLinks $log
            # The finalizer must release the installed executable before MSI starts.
            if (Get-Process -Id $request.FinalizerProcessId -ErrorAction SilentlyContinue) {
                Wait-Process -Id $request.FinalizerProcessId -Timeout 120 -ErrorAction Stop
            }
            $loader = $null
            if ($request.Capture) {
                $pending = Get-Content -LiteralPath (Join-Path $state 'WinPe\pending.json') -Raw | ConvertFrom-Json
                if (-not $pending.CaptureUploadId) { throw 'Capture boot preparation is missing.' }
                $loader = ([guid]$pending.Loader).ToString('B')
            }
            # Close only this installation's processes, including user-session tray windows.
            foreach ($process in Get-Process) {
                $location = $null
                try { $location = $process.Path } catch { continue }
                if ($location -and $location.StartsWith($installed + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Process -Id $process.Id -Force -ErrorAction Stop
                    $process.WaitForExit(30000) | Out-Null
                }
            }
            $msiLog = Join-Path $logDirectory 'image-prep-uninstall.log'
            Assert-NoLinks $msiLog
            Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) Removing the registered CloneDeploy Agent MSI."
            $installer = Start-Process -FilePath "$env:WINDIR\System32\msiexec.exe" -ArgumentList @('/x', $product, '/qn', '/norestart', 'REBOOT=ReallySuppress', '/L*v', ('"' + $msiLog + '"')) -WindowStyle Hidden -PassThru
            if (-not $installer.WaitForExit(900000)) { throw 'MSI uninstall timed out. Do not capture until removal is verified.' }
            $installer.Refresh()
            if ($installer.ExitCode -notin @(0, 3010)) { throw "MSI uninstall failed with exit code $($installer.ExitCode)." }
            if ((Get-Service -Name CloneDeployAgent -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath (Join-Path $installed 'CloneDeploy-Agent.exe')) -or (Test-Path -LiteralPath (Join-Path $installed 'UI\CloneDeploy-Agent.UI.exe'))) {
                throw 'Agent removal is incomplete or awaiting a reboot. Do not capture this image yet.'
            }
            # Remove known identity/configuration databases only; retain diagnostic logs.
            foreach ($name in @('agent', 'policies', 'user-logins', 'device-tasks', 'portal-messages', 'run-as')) {
                foreach ($suffix in @('.db', '.db-wal', '.db-shm')) {
                    $path = Join-Path $state ($name + $suffix)
                    Assert-NoLinks $path
                    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
                }
            }
            $bootstrap = Join-Path $state 'image-enrollment.json'
            Assert-NoLinks $bootstrap
            if (Test-Path -LiteralPath $bootstrap) { Remove-Item -LiteralPath $bootstrap -Force }
            if ($request.Capture) {
                & "$env:WINDIR\System32\bcdedit.exe" /bootsequence $loader /addfirst
                if ($LASTEXITCODE -ne 0) { throw 'Could not arm the prepared WinPE capture boot.' }
            }
            Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) Agent removed. Image preparation completed."
            Remove-Item -LiteralPath $RequestPath -Force
            Remove-Item -LiteralPath $PSCommandPath -Force
            [IO.Directory]::Delete($PSScriptRoot) # Nonrecursive: never remove unexpected contents.
            $powerRequest = Join-Path $state 'ImagePrep\power-action.json'
            Assert-NoLinks $powerRequest
            if (Test-Path -LiteralPath $powerRequest) { Remove-Item -LiteralPath $powerRequest -Force }
            if ($request.RunSysprep) {
                $arguments = @('/generalize', '/oobe', '/quiet', $(if ($request.Capture) { '/reboot' } else { '/shutdown' }))
                if ($request.UseAnswerFile) { $arguments += ('/unattend:"' + "$env:WINDIR\System32\Sysprep\unattend.xml" + '"') }
                Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) Starting Sysprep as the final operation. Logs: $env:WINDIR\System32\Sysprep\Panther"
                $sysprep = Start-Process -FilePath "$env:WINDIR\System32\Sysprep\sysprep.exe" -ArgumentList $arguments -WindowStyle Hidden -PassThru
                $sysprep.WaitForExit()
                if ($sysprep.ExitCode -ne 0) { throw "Sysprep failed with exit code $($sysprep.ExitCode). Check the Sysprep Panther logs." }
                exit 0
            }
            $operation = if ($request.Capture) { '/r' } else { '/s' }
            & "$env:WINDIR\System32\shutdown.exe" $operation /t 30 /c 'CloneDeploy image preparation completed. Agent removed.'
            if ($LASTEXITCODE -ne 0) { throw 'Could not schedule shutdown after agent removal.' }
            exit 0
        }
        catch {
            Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) Uninstall finalization failed: $($_.Exception.Message) Do not capture until corrected."
            if ($request.Capture -and $loader) {
                & "$env:WINDIR\System32\bcdedit.exe" /bootsequence $loader /remove
                if ($LASTEXITCODE -ne 0) { Add-Content -LiteralPath $log -Value 'Could not disarm capture boot. Do not capture until corrected.' }
            }
            # Leave Windows running for diagnosis. Never restart the cleared agent identity.
            exit 1
        }
        """;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfo(string productCode, string property, StringBuilder value, ref uint length);
}
