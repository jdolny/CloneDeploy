[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
# CloneDeploy 2.0 uses the build component as its release counter.
if ($Version -match '^[1-9][0-9]{0,4}$') {
    $release = [int]$Version
} elseif ($Version -match '^2\.0\.([1-9][0-9]{0,4})(?:\.0)?$') {
    $release = [int]$Matches[1]
} else {
    throw 'Supply a release number or CloneDeploy version, such as 256, 2.0.256 or 2.0.256.0.'
}
# Assembly version components must be less than UInt16.MaxValue.
if ($release -gt 65534) {
    throw 'CloneDeploy release numbers must be between 1 and 65534.'
}
[pscustomobject]@{ Release = $release; AssemblyVersion = "2.0.$release.0"; MsiVersion = "2.0.$release" }
