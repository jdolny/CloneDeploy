using System.Text;
using System.Text.Json;

namespace CloneDeploy_Agent;

public static class SoftwareUninstall
{
    public static string Script(JsonElement settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Text("UninstallName")))
            throw new InvalidDataException("Missing uninstall application name.");

        var config = Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.GetRawText()));
        // Set this before ConvertFrom-Json can trigger PowerShell module initialization.
        return $"$ProgressPreference = 'SilentlyContinue';\n$config = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{config}')) | ConvertFrom-Json;\n{Body}";
    }

    // Resolve registration in the actual execution identity, including scheduled Run As actions.
    public const string Body = """
$ErrorActionPreference = 'Stop'
try {
    $installedEntries = @()
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
            $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                $prefixes = @('')
                foreach ($prefix in $prefixes) {
                    $path = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
                    if ($prefix) { $path = $prefix + '\' + $path }
                    $parent = $root.OpenSubKey($path)
                    if ($null -eq $parent) { continue }
                    try {
                        foreach ($keyName in $parent.GetSubKeyNames()) {
                            $key = $parent.OpenSubKey($keyName)
                            if ($null -eq $key) { continue }
                            try {
                                if ([string]$key.GetValue('DisplayName') -ine $config.UninstallName) { continue }
                                if ($null -ne $config.UninstallVersion -and [string]$key.GetValue('DisplayVersion') -ine $config.UninstallVersion) { continue }
                                $installedEntries += [pscustomobject]@{
                                    Key = $keyName; Msi = $key.GetValue('WindowsInstaller');
                                    Command = [string]$key.GetValue('UninstallString');
                                    Quiet = [string]$key.GetValue('QuietUninstallString')
                                }
                            } finally { $key.Dispose() }
                        }
                    } finally { $parent.Dispose() }
                }
            } finally { $root.Dispose() }
        }
    }
    if ($installedEntries.Count -eq 0) { Write-Output 'Already absent in the current machine/user context.'; exit 0 }
    $commands = @()
    foreach ($entry in $installedEntries) {
        $product = [Guid]::Empty
        $isMsi = $entry.Msi -eq 1 -and [Guid]::TryParse($entry.Key, [ref]$product)
        if (-not $isMsi -and $entry.Command -match '(?i)(?:^|[\\/" ])msiexec(?:\.exe)?[" ]+.*?/[ix]\s*(\{[0-9a-f-]{36}\})') {
            $isMsi = [Guid]::TryParse($Matches[1], [ref]$product)
        }
        if ($isMsi) {
            $commands += [pscustomobject]@{ File = "$env:SystemRoot\System32\msiexec.exe"; Args = '/x {' + $product.ToString() + '} /qn /norestart' }
            continue
        }
        $command = $entry.Quiet
        if ([string]::IsNullOrWhiteSpace($command)) {
            if ([string]::IsNullOrWhiteSpace($config.Arguments)) { throw 'No quiet uninstall command is available. Configure documented silent arguments for this application.' }
            $command = $entry.Command
            if ([string]::IsNullOrWhiteSpace($command)) { throw 'No uninstall command is registered for this application.' }
            $command += ' ' + $config.Arguments
        }
        $command = [Environment]::ExpandEnvironmentVariables($command).Trim()
        if ($command -match '^"([^"]+)"\s*(.*)$') { $file = $Matches[1]; $arguments = $Matches[2] }
        elseif ($command -match '^([^"\r\n]+?\.exe)(?:\s+(.*))?$') { $file = $Matches[1]; $arguments = $Matches[2] }
        else { throw 'The registered uninstall command is not a supported executable command.' }
        $commands += [pscustomobject]@{ File = $file; Args = $arguments }
    }
    $successCodes = @($config.SuccessCodes.Split(',') | ForEach-Object { [int]$_.Trim() })
    $exitCode = 0
    foreach ($command in @($commands | Sort-Object File,Args -Unique)) {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $command.File
        $start.Arguments = $command.Args
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $child = [Diagnostics.Process]::Start($start)
        try {
            $child.WaitForExit()
            $code = $child.ExitCode
            if ($successCodes -notcontains $code) { [Console]::Error.WriteLine("Uninstaller returned exit code $code."); exit $code }
            if ($code -ne 0) { $exitCode = $code }
        } finally { $child.Dispose() }
    }
    Write-Output 'Uninstall completed.'
    exit $exitCode
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
""";
}