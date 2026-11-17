[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo 'artifacts/linux-packages'
$windowsPayload = Join-Path $repo 'artifacts/server-installer/payload'
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Supply a numeric release version.' }
[void](Get-Command tar -ErrorAction Stop)
# These are release inputs, including signatures, not Linux executables.
$clientPackages = @('Agent/CloneDeploy-Agent-x64.msi', 'Agent/CloneDeploy-Agent-x64.msi.json',
    'Agent/CloneDeploy-Agent-arm64.msi', 'Agent/CloneDeploy-Agent-arm64.msi.json',
    'Agent/CloneDeploy-Agent-macOS-arm64.pkg', 'Agent/CloneDeploy-Agent-macOS-arm64.pkg.json',
    'Imager/win-x64.zip', 'Imager/win-arm64.zip')
foreach ($relative in $clientPackages) {
    if (!(Test-Path -LiteralPath (Join-Path $windowsPayload "Packaging/$relative") -PathType Leaf)) {
        throw "Build the Windows release payload first. Missing client package: $relative"
    }
}
$stage = Join-Path $output ('stage-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
foreach ($product in @('CloneDeploy-Server', 'CloneDeploy-SiteService')) {
    $publish = Join-Path $stage $product
    & dotnet publish (Join-Path $repo "$product/$product.csproj") -c Release -r linux-x64 --self-contained false "-p:Version=$Version" -p:UseAppHost=false -p:DebugType=None "-p:PreparedImagerBundleDirectory=$windowsPayload/Packaging/Imager" -o $publish
    if ($LASTEXITCODE) { throw "$product Linux publish failed." }
    foreach ($required in @("$product.dll", "$product.deps.json", "$product.runtimeconfig.json")) {
        if (!(Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Incomplete Linux publish: $required" }
    }
    if ($product -eq 'CloneDeploy-Server') {
        foreach ($relative in $clientPackages) {
            $destination = Join-Path $publish "Packaging/$relative"
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            Copy-Item -LiteralPath (Join-Path $windowsPayload "Packaging/$relative") -Destination $destination -Force
        }
        Copy-Item -LiteralPath (Join-Path $repo 'CloneDeploy-Server/Packaging/server.template.json') -Destination (Join-Path $publish 'server.example.json')
    } else {
        Copy-Item -LiteralPath (Join-Path $repo 'CloneDeploy-SiteService/siteservice.example.json') -Destination (Join-Path $publish 'siteservice.example.json') -Force
    }
    [IO.File]::WriteAllText((Join-Path $publish 'release.json'), (@{ Version=$Version; Runtime='linux-x64'; Product=$product } | ConvertTo-Json))
    $archive = Join-Path $stage "$product-linux-x64.tar.gz"
    & tar -czf $archive -C $publish .
    if ($LASTEXITCODE) { throw "$product Linux archive creation failed." }
    $entries = & tar -tzf $archive
    if ($LASTEXITCODE -or $entries -notcontains "./$product.dll") { throw "$product Linux archive validation failed." }
}
# Publish final archive names only after both products have packaged successfully.
foreach ($product in @('CloneDeploy-Server', 'CloneDeploy-SiteService')) {
    $name = "$product-linux-x64.tar.gz"
    Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $output $name) -Force
    $hash = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $output "$name.sha256"), "$hash  $name`n")
    Write-Host "Linux package: $(Join-Path $output $name)"
}
