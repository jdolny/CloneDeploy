[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path, [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MsiDatabase.ps1')
$script:Installer = New-Object -ComObject WindowsInstaller.Installer
try {
    $db = $script:Installer.OpenDatabase((Resolve-Path -LiteralPath $Path).Path, 0)
    try {
        $upgradeCode = if ($Architecture -eq 'arm64') { '{620CDB5F-90AA-4381-9FAA-6546CBC2E350}' } else { '{D75325AF-F72B-4039-9B5D-DBAA9E8AB0F5}' }
        $summary = $db.SummaryInformation(0)
        try {
            $platform = if ($Architecture -eq 'arm64') { 'Arm64' } else { 'x64' }
            if (($summary.Property(7) -split ';')[0] -cne $platform) { throw 'Incorrect MSI platform.' }
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
        $checks = @(
            @("SELECT ``Directory_`` FROM ``Shortcut`` WHERE ``Shortcut`` = 'AgentPortalStartMenu'", 'AgentProgramMenuFolder'),
            @("SELECT ``Target`` FROM ``Shortcut`` WHERE ``Shortcut`` = 'AgentPortalStartMenu'", 'MainFeature'),
            @("SELECT ``Arguments`` FROM ``Shortcut`` WHERE ``Shortcut`` = 'AgentPortalStartMenu'", ''),
            @("SELECT ``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut`` = 'AgentPortalStartMenu'", 'AgentPortal.exe'),
            @("SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory`` = 'AgentProgramMenuFolder'", 'ProgramMenuFolder'),
            @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ALLUSERS'", '1'),
            @("SELECT ``DirProperty`` FROM ``RemoveFile`` WHERE ``FileKey`` = 'RemoveAgentProgramMenuFolder'", 'AgentProgramMenuFolder'),
            @("SELECT ``InstallMode`` FROM ``RemoveFile`` WHERE ``FileKey`` = 'RemoveAgentProgramMenuFolder'", '2'),
            @("SELECT ``Value`` FROM ``Registry`` WHERE ``Registry`` = 'AgentPortalStartup'", '"[#AgentPortalExe]" --tray'),
            @("SELECT ``Root`` FROM ``Registry`` WHERE ``Registry`` = 'AgentPortalStartup'", '2'),
            @("SELECT ``Key`` FROM ``Registry`` WHERE ``Registry`` = 'AgentPortalStartup'", 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run'),
            @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ENROLLMENT_FORMAT'", '2'),
            @("SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action`` = 'ImportEnrollment'", '3090'),
            @("SELECT ``Source`` FROM ``CustomAction`` WHERE ``Action`` = 'ImportEnrollment'", 'AgentExe'),
            @("SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action`` = 'ImportEnrollment'", '--import-enrollment "[ENROLLMENT_FILE]"'),
            @("SELECT ``Arguments`` FROM ``ServiceInstall`` WHERE ``Name`` = 'CloneDeployAgent'", ''),
            @("SELECT ``StartType`` FROM ``ServiceInstall`` WHERE ``Name`` = 'CloneDeployAgent'", '2'),
            @("SELECT ``Event`` FROM ``ServiceControl`` WHERE ``Name`` = 'CloneDeployAgent'", '163'),
            @("SELECT ``Wait`` FROM ``ServiceControl`` WHERE ``Name`` = 'CloneDeployAgent'", '1'),
            @("SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action`` = 'RemoveAgentData'", '3090'),
            @("SELECT ``Source`` FROM ``CustomAction`` WHERE ``Action`` = 'RemoveAgentData'", 'AgentExe'),
            @("SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action`` = 'RemoveAgentData'", '--remove-agent-data'),
            @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'MSIRMSHUTDOWN'", '0'),
            @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'MSIDISABLERMRESTART'", '0'),
            @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'UpgradeCode'", $upgradeCode)
        )
        foreach ($check in $checks) {
            if ((Get-MsiValue $db $check[0]) -ne $check[1]) { throw "Installer validation failed: $($check[0])" }
        }
        foreach ($secretProperty in @('SERVER_URL', 'PROVISIONING_KEY', 'CA_CERT_BASE64')) {
            if (Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$secretProperty'") { throw 'Generic MSI contains legacy enrollment properties.' }
        }
        $menuName = Get-MsiValue $db "SELECT ``DefaultDir`` FROM ``Directory`` WHERE ``Directory`` = 'AgentProgramMenuFolder'"
        if (($menuName -split '\|')[-1] -ne 'CloneDeploy') { throw 'Portal Start menu folder name is incorrect.' }
        $shortcutName = Get-MsiValue $db "SELECT ``Name`` FROM ``Shortcut`` WHERE ``Shortcut`` = 'AgentPortalStartMenu'"
        if (($shortcutName -split '\|')[-1] -ne 'CloneDeploy Portal') { throw 'Portal Start menu name is incorrect.' }
        $portalComponent = Get-MsiValue $db "SELECT ``Component_`` FROM ``File`` WHERE ``File`` = 'AgentPortalExe'"
        if ((Get-MsiValue $db "SELECT ``Component_`` FROM ``Shortcut`` WHERE ``Shortcut`` = 'AgentPortalStartMenu'") -ne $portalComponent -or
            (Get-MsiValue $db "SELECT ``KeyPath`` FROM ``Component`` WHERE ``Component`` = '$portalComponent'") -ne 'AgentPortalExe') {
            throw 'Portal shortcut must activate the portal executable.'
        }
        if ((Get-MsiValue $db "SELECT ``WkDir`` FROM ``Shortcut`` WHERE ``Shortcut`` = 'AgentPortalStartMenu'") -ne
            (Get-MsiValue $db "SELECT ``Directory_`` FROM ``Component`` WHERE ``Component`` = '$portalComponent'")) {
            throw 'Portal shortcut working directory must be the UI directory.'
        }
        $version = Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ProductVersion'"
        $oldCode = Get-MsiValue $db "SELECT ``UpgradeCode`` FROM ``Upgrade`` WHERE ``ActionProperty`` = 'WIX_UPGRADE_DETECTED'"
        $maximum = Get-MsiValue $db "SELECT ``VersionMax`` FROM ``Upgrade`` WHERE ``ActionProperty`` = 'WIX_UPGRADE_DETECTED'"
        $attributes = [int](Get-MsiValue $db "SELECT ``Attributes`` FROM ``Upgrade`` WHERE ``ActionProperty`` = 'WIX_UPGRADE_DETECTED'")
        if ($oldCode -ne $upgradeCode -or $maximum -ne $version -or ($attributes -band 2) -or ($attributes -band 512)) {
            throw 'Major upgrades must remove earlier releases in the same family without treating equal versions as upgrades.'
        }
        [void](& (Join-Path $PSScriptRoot 'Get-AgentReleaseVersion.ps1') -Version $version)
        $initialize = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'InstallInitialize'")
        $remove = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'RemoveExistingProducts'")
        $install = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'InstallFiles'")
        $import = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'ImportEnrollment'")
        $start = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'StartServices'")
        if (!($install -lt $import -and $import -lt $start)) { throw 'Enrollment must import after files are installed and before service startup.' }
        if ((Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'SecureCustomProperties'") -split ';' -notcontains 'ENROLLMENT_FILE') { throw 'Enrollment path must cross the elevation boundary.' }
        if (!($initialize -gt 0 -and $initialize -lt $remove -and $remove -lt $install)) { throw 'Upgrade removal must be inside the rollback transaction before new files are installed.' }
        $otherCode = if ($Architecture -eq 'arm64') { '{D75325AF-F72B-4039-9B5D-DBAA9E8AB0F5}' } else { '{620CDB5F-90AA-4381-9FAA-6546CBC2E350}' }
        if ((Get-MsiValue $db "SELECT ``UpgradeCode`` FROM ``Upgrade`` WHERE ``ActionProperty`` = 'OTHERARCHAGENTINSTALLED'") -ne $otherCode) { throw 'Opposite-architecture detection is missing.' }
        if (Get-MsiValue $db "SELECT ``Name`` FROM ``_Tables`` WHERE ``Name`` = 'CustomAction'") {
            $actions = $db.OpenView('SELECT `Type`, `Action` FROM `CustomAction`')
            try {
                [void]$actions.Execute()
                while ($record = $actions.Fetch()) {
                    try { if ($record.StringData(2) -notin @('RemoveAgentData', 'ImportEnrollment') -and ([int]$record.StringData(1) -band 63) -notin @(19, 51)) { throw 'Unexpected executable custom action in agent MSI.' } }
                    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
                }
            } finally { [void]$actions.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($actions) }
        }
        $cleanup = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'RemoveAgentData'")
        $stop = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'StopServices'")
        $deleteService = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'DeleteServices'")
        if (!($stop -gt 0 -and $cleanup -gt $stop -and $cleanup -lt $deleteService)) { throw 'Cleanup must run after stopping and before deleting the service.' }
        if ((Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'SecureCustomProperties'") -split ';' -notcontains 'REMOVE_AGENT_DATA') { throw 'Cleanup property must cross the elevation boundary.' }
        # Evaluate installer conditions without running any installation actions.
        if ($Architecture -eq 'x64') {
            $session = $script:Installer.OpenPackage((Resolve-Path -LiteralPath $Path).Path, 1)
            try {
                $cleanupCondition = Get-MsiValue $db "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'RemoveAgentData'"
                if (!$cleanupCondition) { throw 'Cleanup condition is missing.' }
                foreach ($case in @(
                    @('', 'ALL', '1', '', 0),
                    @('1', '', '1', '', 0),
                    @('1', 'ALL', '', '', 0),
                    @('1', 'ALL', '0', '', 0),
                    @('1', 'ALL', '1', '{11111111-1111-1111-1111-111111111111}', 0),
                    @('1', 'ALL', '1', '', 1)
                )) {
                    $session.Property('Installed') = $case[0]; $session.Property('REMOVE') = $case[1]
                    $session.Property('REMOVE_AGENT_DATA') = $case[2]; $session.Property('UPGRADINGPRODUCTCODE') = $case[3]
                    if ($session.EvaluateCondition($cleanupCondition) -ne $case[4]) { throw 'Clean uninstall condition incorrectly handles install, repair, normal uninstall or upgrade.' }
                }
                foreach ($name in @('REMOVE', 'REMOVE_AGENT_DATA', 'UPGRADINGPRODUCTCODE')) { $session.Property($name) = '' }
                foreach ($table in @('InstallUISequence', 'InstallExecuteSequence')) {
                    $resolveCondition = Get-MsiValue $db "SELECT ``Condition`` FROM ``$table`` WHERE ``Action`` = 'ResolveSource'"
                    $condition = Get-MsiValue $db "SELECT ``Condition`` FROM ``$table`` WHERE ``Action`` = 'SetENROLLMENT_FILE'"
                    $cost = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``$table`` WHERE ``Action`` = 'CostInitialize'")
                    $related = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``$table`` WHERE ``Action`` = 'FindRelatedProducts'")
                    $resolve = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``$table`` WHERE ``Action`` = 'ResolveSource'")
                    $set = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``$table`` WHERE ``Action`` = 'SetENROLLMENT_FILE'")
                    if (!$condition -or !$resolveCondition -or $resolve -le $cost -or $resolve -le $related -or $set -le $resolve -or $set -ge $import) {
                        throw 'Enrollment discovery must follow upgrade detection and source resolution, before import.'
                    }
                    foreach ($case in @(
                        @('', '', '', '', 1),
                        @('1', '', '', '', 0),
                        @('', '{11111111-1111-1111-1111-111111111111}', '', '', 0),
                        @('', '', 'ALL', '', 0),
                        @('', '', '', 'C:\Explicit\enrollment.json', 0)
                    )) {
                        $session.Property('Installed') = $case[0]
                        $session.Property('WIX_UPGRADE_DETECTED') = $case[1]
                        $session.Property('REMOVE') = $case[2]
                        $session.Property('ENROLLMENT_FILE') = $case[3]
                        $session.Property('SourceDir') = 'C:\Bundle with spaces\'
                        if ($session.EvaluateCondition($condition) -ne $case[4] -or $session.EvaluateCondition($resolveCondition) -ne $case[4]) {
                            throw 'Discovery incorrectly handles fresh install, repair, upgrade, removal or explicit configuration.'
                        }
                    }
                }
                foreach ($name in @('Installed', 'WIX_UPGRADE_DETECTED', 'REMOVE', 'ENROLLMENT_FILE')) { $session.Property($name) = '' }
                $session.Property('SourceDir') = ''
                if ($session.EvaluateCondition($condition) -ne 0) { throw 'Missing source must not fall back to the working directory.' }
                $session.Property('SourceDir') = 'C:\Bundle with spaces\'
                [void]$session.DoAction('SetENROLLMENT_FILE')
                if ($session.Property('ENROLLMENT_FILE') -cne 'C:\Bundle with spaces\enrollment.json') { throw 'Discovery did not use the MSI source directory.' }
                $session.Property('ENROLLMENT_FILE') = ''
                $session.Property('SourceDir') = ''
                [void]$session.DoAction('CostInitialize')
                [void]$session.DoAction('ResolveSource')
                [void]$session.DoAction('SetENROLLMENT_FILE')
                $expectedPath = Join-Path (Split-Path (Resolve-Path -LiteralPath $Path).Path) 'enrollment.json'
                if ($session.Property('ENROLLMENT_FILE') -ine $expectedPath) { throw 'Actual MSI source resolution did not locate its adjacent enrollment file.' }

            } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($session) }
        }
        $view = $db.OpenView('SELECT `FileName` FROM `File`')
        try {
            [void]$view.Execute()
            $files = @()
            while ($record = $view.Fetch()) {
                try { $files += ($record.StringData(1) -split '\|')[-1] }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            }
            if ($files -match '^appsettings.*\.json$') { throw 'Development configuration is included in the MSI.' }
            foreach ($required in @('CloneDeploy-Agent.exe', 'CloneDeploy-Agent.runtimeconfig.json', 'coreclr.dll', 'e_sqlite3.dll',
                'CloneDeploy-Agent.Core.dll', 'CloneDeploy-Agent.Contracts.dll', 'System.Drawing.Common.dll', 'System.Private.Windows.Core.dll',
                'System.Private.Windows.GdiPlus.dll', 'Microsoft.Win32.SystemEvents.dll', 'ControlR-LICENSE.txt', 'CloneDeploy-Agent.UI.exe')) {
                if ($files -notcontains $required) { throw "Required runtime file missing: $required" }
            }
            Write-Host "Verified $($files.Count) packaged files, service lifecycle, upgrade identity, and enrollment permissions."
        } finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) }
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($script:Installer) }
