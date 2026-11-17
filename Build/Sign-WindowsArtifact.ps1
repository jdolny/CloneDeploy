[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [switch]$VerifyOnly,
    [string]$ExpectedProfile,
    [string]$SignTool = 'C:\Microsoft Trusted Signing\Microsoft.Windows.SDK.BuildTools\bin\10.0.22621.0\x64\signtool.exe',
    [string]$SigningDll = 'C:\Microsoft Trusted Signing\Microsoft.Trusted.Signing.Client\bin\x64\Azure.CodeSigning.Dlib.dll',
    [string]$SigningProfile = 'C:\Microsoft Trusted Signing\profile.json'
)
$ErrorActionPreference = 'Stop'
$requiredInputs = @($SignTool)
if (!$VerifyOnly) { $requiredInputs += @($SigningDll, $SigningProfile) }
foreach ($required in $requiredInputs) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Signing input is missing: $required" }
}
. (Join-Path $PSScriptRoot '../CloneDeploy-Agent.Win/Updates/Verify-AgentPublisher.ps1')
if (!$Path.Count) { throw 'No Windows artifacts were selected for signing.' }
function Invoke-SignTool([string[]]$ToolArguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $SignTool
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # All variable arguments are file paths, which cannot contain a double quote.
    $start.Arguments = ($ToolArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(300000)) {
            $process.Kill()
            throw 'SignTool timed out after five minutes. Check Azure authentication and network connectivity.'
        }
        if ($process.ExitCode -ne 0) {
            Write-Host $stdout.GetAwaiter().GetResult()
            Write-Host $stderr.GetAwaiter().GetResult()
            throw "SignTool failed with exit code $($process.ExitCode)."
        }
    } finally { $process.Dispose() }
}
foreach ($artifact in $Path) {
    if (!$VerifyOnly) {
        Write-Host "Signing $artifact"
        Invoke-SignTool @('sign', '/v', '/fd', 'SHA256', '/tr', 'http://timestamp.acs.microsoft.com', '/td', 'SHA256', '/dlib', $SigningDll, '/dmdf', $SigningProfile, $artifact)
    }
    Write-Host "Verifying $artifact"
    Invoke-SignTool @('verify', '/pa', '/all', '/v', '/tw', $artifact)
    if (!(Get-AuthenticodeSignature -LiteralPath $artifact).TimeStamperCertificate) {
        throw "Windows artifact is missing its signing timestamp: $artifact"
    }
    if (!$ExpectedProfile) { $ExpectedProfile = Get-AgentSigningProfile $artifact }
    Assert-AgentPublisher $artifact $ExpectedProfile
}
