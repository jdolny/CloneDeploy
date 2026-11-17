function Get-AgentInputFingerprint([string]$Repo, [string]$SdkVersion, [ValidateSet('Windows', 'Mac')][string]$Platform = 'Windows') {
    # Keep generated output and local configuration out of the release identity.
    function Get-InputFiles([string]$Directory) {
        foreach ($item in Get-ChildItem -LiteralPath $Directory -Force) {
            if ($item.PSIsContainer) {
                if ($item.Name -notmatch '^(bin|obj|artifacts|\.git|\.vs|\.idea)$|-cache$') {
                    Get-InputFiles $item.FullName
                }
            } elseif ($item.Name -notmatch '(^appsettings.*\.json$|\.(md|log|user|pdb)$)') { $item }
        }
    }
    $files = @(
        $directories = if ($Platform -eq 'Mac') { @('CloneDeploy-Agent.MacOS', 'CloneDeploy-Agent.Core', 'CloneDeploy-Agent.Contracts') }
            else { @('CloneDeploy-Agent.Win', 'CloneDeploy-Agent.Core', 'CloneDeploy-Agent.Contracts') }
        foreach ($directory in $directories) {
            Get-InputFiles (Join-Path $Repo $directory)
        }
        # Both Portal applications compile the shared localized UI resources.
        Get-InputFiles (Join-Path $Repo 'CloneDeploy-Agent.Localization')
        if ($Platform -eq 'Mac') {
            # Follow the source links actually compiled into the Mac project, not the whole Windows service.
            [xml]$macProject = Get-Content -LiteralPath (Join-Path $Repo 'CloneDeploy-Agent.MacOS/CloneDeploy-Agent.MacOS.csproj') -Raw
            foreach ($include in $macProject.SelectNodes('//Compile[@Include]')) {
                Get-Item -LiteralPath ([IO.Path]::GetFullPath((Join-Path $Repo ('CloneDeploy-Agent.MacOS/' + $include.Include))))
            }
            Get-InputFiles (Join-Path $Repo 'Build/MacPackage')
            Get-Item -LiteralPath (Join-Path $Repo 'Build/Sign-MacRelease.ps1')
            Get-Item -LiteralPath (Join-Path $Repo 'Build/Sign-MacRelease.sh')
            Get-Item -LiteralPath (Join-Path $Repo 'CloneDeploy-Server/Backend/Services/Agents/Installation/MacInstallerPackage.cs')
            Get-Item -LiteralPath (Join-Path $Repo 'Build/Build-MacAgent.ps1')
            Get-Item -LiteralPath (Join-Path $Repo 'Branding/CloneDeploy.icns')
        }
        Get-Item -LiteralPath (Join-Path $Repo 'Branding/CloneDeploy.ico')
        if ($Platform -eq 'Windows') { Get-Item -LiteralPath (Join-Path $Repo 'Build/Sign-WindowsArtifact.ps1') }
        Get-ChildItem -LiteralPath $Repo -File | Where-Object { $_.Name -match '^(Directory\..*\.(props|targets)|global\.json|nuget\.config)$' }
    )
    $entries = [Collections.Generic.List[string]]::new()
    $entries.Add('fingerprint-format:1')
    $entries.Add("sdk:$SdkVersion")
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($file in ($files | Sort-Object FullName -Unique)) {
            if ($file.FullName -match '[\\/]__pycache__[\\/]') { continue }
            $relative = $file.FullName.Substring($Repo.TrimEnd('\', '/').Length + 1).Replace('\', '/')
            $bytes = [IO.File]::ReadAllBytes($file.FullName)
            if ($file.Extension -match '^\.(cs|csproj|props|targets|xaml|xml|json|config|ps1|wxs|wxi|wixproj|txt|manifest|resx|sh|plist)$') {
                $content = [IO.File]::ReadAllText($file.FullName).Replace("`r`n", "`n")
                if ($relative -eq 'CloneDeploy-Agent.Win/AgentVersion.props') {
                    $content = $content -replace '<AgentReleaseNumber>\d+</AgentReleaseNumber>', '<AgentReleaseNumber>AUTO</AgentReleaseNumber>'
                }
                if ($relative -in @('CloneDeploy-Agent.MacOS/AgentVersion.props', 'Directory.Build.props')) {
                    $content = $content -replace '<Version>[^<]+</Version>', '<Version>AUTO</Version>'
                }
                $bytes = [Text.Encoding]::UTF8.GetBytes($content)
            }
            $entries.Add($relative + ':' + [BitConverter]::ToString($sha.ComputeHash($bytes)))
        }
        $entries.Sort([StringComparer]::Ordinal)
        [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($entries -join "`n")))).Replace('-', '')
    } finally { $sha.Dispose() }
}

function Get-ReleasePlan($State, [int]$SourceAgent, [string]$Fingerprint, [string]$Version, [string]$AgentVersion) {
    $number = if ($Version) { $Version } else { ([version]$State.ServerVersion).Build + 1 }
    $server = (& (Join-Path $PSScriptRoot '../CloneDeploy-Agent.Win/Packaging/Get-AgentReleaseVersion.ps1') -Version $number).MsiVersion
    $parsed = [version]$server
    if ($parsed -le [version]$State.ServerVersion) { throw 'The server version must be newer than the last successful release.' }
    $changed = $State.AgentFingerprint -ne $Fingerprint
    $agent = if ($AgentVersion) {
        (& (Join-Path $PSScriptRoot '../CloneDeploy-Agent.Win/Packaging/Get-AgentReleaseVersion.ps1') -Version $AgentVersion).Release
    } elseif ($SourceAgent -eq 1 -and [int]$State.AgentVersion -eq 0 -and !$State.AgentFingerprint) {
        # The first release has no previously built agent to increment.
        1
    } elseif ($SourceAgent -gt [int]$State.AgentVersion -and $State.AgentFingerprint) {
        $SourceAgent
    } elseif ($changed) {
        [Math]::Max($SourceAgent, [int]$State.AgentVersion) + 1
    } else { [Math]::Max($SourceAgent, [int]$State.AgentVersion) }
    if ($agent -lt $SourceAgent -or $agent -lt [int]$State.AgentVersion -or ($changed -and $agent -le [int]$State.AgentVersion)) {
        throw 'Changed agent inputs require a newer agent release; the version cannot be lower than the source release.'
    }
    if ($agent -gt 65534) { throw 'Agent release numbers cannot exceed 65534 (assembly version limit).' }
    [pscustomobject]@{ ServerVersion = "$server"; AgentVersion = [int]$agent; AgentFingerprint = $Fingerprint; AgentChanged = $changed }
}

function Save-ReleaseState([string]$Path, $State) {
    $temporary = "$Path.tmp"
    [IO.File]::WriteAllText($temporary, ($State | ConvertTo-Json) + [Environment]::NewLine)
    if (Test-Path -LiteralPath $Path) {
        [IO.File]::Replace($temporary, $Path, "$Path.bak")
        Remove-Item -LiteralPath "$Path.bak"
    }
    else { [IO.File]::Move($temporary, $Path) }
}

function Get-MacReleasePlan($State, [int]$SourceMac, [string]$Fingerprint, [string]$MacAgentVersion, [int]$LastBuiltMac = 0) {
    $macState = [pscustomobject]@{ ServerVersion = '2.0.0'; AgentVersion = [int]$State.MacAgentVersion; AgentFingerprint = [string]$State.MacAgentFingerprint }
    if ($LastBuiltMac -gt $macState.AgentVersion) {
        # A standalone release has no matching full-release fingerprint. Establish a
        # fresh baseline above it, even if source inputs match the old full release.
        $macState.AgentVersion = $LastBuiltMac
        $macState.AgentFingerprint = ''
    }
    $plan = Get-ReleasePlan $macState $SourceMac $Fingerprint '' $MacAgentVersion
    [pscustomobject]@{ MacAgentVersion = $plan.AgentVersion; MacAgentFingerprint = $Fingerprint; MacAgentChanged = $plan.AgentChanged }
}
