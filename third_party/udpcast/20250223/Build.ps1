param([string]$Bash = 'C:\msys64\usr\bin\bash.exe')
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Bash)) { throw 'Install MSYS2 with make, or supply -Bash pointing to its bash.exe.' }
$name = 'llvm-mingw-20261006-ucrt-x86_64'
$archive = Join-Path $PSScriptRoot "$name.zip"
$expected = '317492C456AA27EE607A5919F1D2D38DCDC1112516A24D0BF4B00D078F52D17A'
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot "$name\bin\clang.exe"))) {
    if (!(Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest "https://github.com/mstorsjo/llvm-mingw/releases/download/20261006/$name.zip" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Toolchain SHA-256 mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $PSScriptRoot -Force
}
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'source'))) {
    Expand-Archive -LiteralPath (Join-Path $PSScriptRoot 'source.zip') -DestinationPath $PSScriptRoot
}
Push-Location $PSScriptRoot
try {
    & $Bash './build.sh'
    if ($LASTEXITCODE -ne 0) { throw 'UDPcast build failed. Inspect configure-output.log/build-output.log in the build folders.' }
    foreach ($pair in @(@('aarch64','a64'), @('x86_64','x64'))) {
        New-Item -ItemType Directory -Path $pair[1] -Force | Out-Null
        foreach ($tool in @('udp-sender.exe','udp-receiver.exe')) {
            Copy-Item -LiteralPath (Join-Path "build-$($pair[0])" $tool) -Destination (Join-Path $pair[1] $tool) -Force
        }
    }
} finally { Pop-Location }
