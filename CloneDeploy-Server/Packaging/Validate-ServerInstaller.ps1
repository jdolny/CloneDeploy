[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path, [ValidateSet("App", "SiteService", "WindowsWorker")][string]$Role = "App")
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../CloneDeploy-Agent.Win/Packaging/MsiDatabase.ps1')
$script:Installer = New-Object -ComObject WindowsInstaller.Installer
$checks = 0
function Assert-Msi([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
try {
    $db = $script:Installer.OpenDatabase([IO.Path]::GetFullPath($Path), 0)
    try {
        $packagedRole = Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'PackagedRole'"
        Assert-Msi ($packagedRole -eq $Role) 'Package must have its own fixed role.'
        foreach ($action in @('ValidateVisualCpp', 'ValidateDotNet')) {
            $sequence = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = '$action'")
            Assert-Msi ($sequence -gt 0 -and $sequence -lt 1500) "$action must reject missing prerequisites before installation changes."
            Assert-Msi ((Get-MsiValue $db "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = '$action'") -eq 'NOT (REMOVE="ALL")') "$action must permit uninstall."
        }
        $upgradeCode = Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'UpgradeCode'"
        $expectedUpgrade = if ($Role -eq 'App') { '{85F5604C-DCEE-4B26-809C-95E4692D5074}' } elseif ($Role -eq 'WindowsWorker') { '{83994981-5CD9-4BDE-8465-0870F8D1D8CD}' } else { '{CA329D40-C256-455C-B82C-54F5C2F3AA67}' }
        Assert-Msi ($upgradeCode -eq $expectedUpgrade) 'App and Site Service must use independent upgrade families.'
        $marker = Get-MsiValue $db "SELECT ``Key`` FROM ``Registry`` WHERE ``Registry`` = 'InstallationRole'"
        Assert-Msi ($marker -eq "SOFTWARE\CloneDeploy\Installation\$Role") 'Role ownership must be isolated.'
        $next = Get-MsiValue $db "SELECT ``Argument`` FROM ``ControlEvent`` WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control_`` = 'Next' AND ``Event`` = 'NewDialog'"
        Assert-Msi ($next -eq 'FolderDlg') 'Separate installers must not offer a role switch.'
        $welcome = Get-MsiValue $db "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action`` = 'WelcomeDlg'"
        Assert-Msi ($welcome -eq 'AI_INSTALL') 'Fresh-install wizard uses the installation-mode flag.'
        $exitDialog = Get-MsiValue $db "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action`` = 'ExitDialog'"
        Assert-Msi ($exitDialog -eq 'NOT AiSkipExitDlg OR NOT AI_INSTALL') 'Completion dialog honors the skip-exit flag.'
        foreach ($entry in @(@('SkipUpgradeWizard', '1229', 'AI_INSTALL', '{}', 'OLDPRODUCTS AND AI_INSTALL'),
                              @('SkipUpgradeExit', '1277', 'AiSkipExitDlg', '1', 'OLDPRODUCTS AND NOT Installed'),
                              @('RestoreUpgradeInstallMode', '1278', 'AI_INSTALL', '1', 'OLDPRODUCTS AND NOT Installed'))) {
            $action = $entry[0]
            Assert-Msi ((Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallUISequence`` WHERE ``Action`` = '$action'") -eq $entry[1]) "Upgrade property action must bracket the wizard: $action"
            Assert-Msi ((Get-MsiValue $db "SELECT ``Condition`` FROM ``InstallUISequence`` WHERE ``Action`` = '$action'") -eq $entry[4]) "Only major upgrades bypass questions: $action"
            Assert-Msi ((Get-MsiValue $db "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action`` = '$action'") -eq '51') "Upgrade UI action must only set a property: $action"
            Assert-Msi ((Get-MsiValue $db "SELECT ``Source`` FROM ``CustomAction`` WHERE ``Action`` = '$action'") -eq $entry[2]) "Upgrade UI property must match: $action"
            Assert-Msi ((Get-MsiValue $db "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action`` = '$action'") -eq $entry[3]) "Upgrade UI property value must match: $action"
        }
        $findRelated = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallUISequence`` WHERE ``Action`` = 'FindRelatedProducts'")
        $resolveUi = [int](Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallUISequence`` WHERE ``Action`` = 'ResolveRole'")
        Assert-Msi ($findRelated -gt 0 -and $findRelated -lt $resolveUi -and $resolveUi -lt 1230) 'Detect upgrades and restore the installation folder before entering the wizard.'
        $hidden = Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'MsiHiddenProperties'"
        $httpPort = Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'HTTP_PORT'"
        Assert-Msi ($httpPort -eq '80') 'New installations must default to HTTP port 80.'
        $portInput = Get-MsiValue $db "SELECT ``Property`` FROM ``Control`` WHERE ``Dialog_`` = 'ServerAccountDlg' AND ``Control`` = 'HTTP_PORT'"
        Assert-Msi ($portInput -eq 'HTTP_PORT') 'HTTP port must be configurable in setup.'
        $httpsPort = Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'HTTPS_PORT'"
        Assert-Msi ([string]::IsNullOrEmpty($httpsPort)) 'The installer must not configure HTTPS.'
        foreach ($property in @('DB_PASSWORD','SERVICE_PASSWORD','SERVICE_PASSWORD_CONFIRM')) {
            Assert-Msi ($property -in $hidden.Split(';')) "Password property must be hidden: $property"
            $value = Get-MsiValue $db "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$property'"
            Assert-Msi ([string]::IsNullOrEmpty($value)) "An installer password must never have a default value: $property"
            $attributes = Get-MsiValue $db "SELECT ``Attributes`` FROM ``Control`` WHERE ``Control`` = '$property'"
            Assert-Msi (([int]$attributes -band 0x200000) -ne 0) "Password input must be masked: $property"
        }
        foreach ($control in @('SERVICE_USER')) {
            $prompt = Get-MsiValue $db "SELECT ``Control`` FROM ``Control`` WHERE ``Control`` = '$control'"
            Assert-Msi ([string]::IsNullOrEmpty($prompt)) "Setup must not prompt for $control."
        }
        foreach ($control in @('DB_HOST','DB_PORT','DB_NAME','DB_USER','DB_PASSWORD','DB_CA','DB_HOSTLabel','DB_PORTLabel','DB_NAMELabel','DB_USERLabel','DB_PASSWORDLabel','DB_CALabel','Requirements')) {
            $hide = Get-MsiValue $db "SELECT ``Condition`` FROM ``ControlCondition`` WHERE ``Dialog_`` = 'ServerDatabaseDlg' AND ``Control_`` = '$control' AND ``Action`` = 'Hide'"
            Assert-Msi ($hide -eq 'DB_MODE="Local"') "Local setup hides external database control: $control"
            $show = Get-MsiValue $db "SELECT ``Condition`` FROM ``ControlCondition`` WHERE ``Dialog_`` = 'ServerDatabaseDlg' AND ``Control_`` = '$control' AND ``Action`` = 'Show'"
            Assert-Msi ($show -eq 'DB_MODE="Existing"') "External setup exposes database control: $control"
        }
        $siteBrowse = Get-MsiValue $db "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action`` = 'BrowseEnrollment'"
        Assert-Msi ($siteBrowse -eq 'BrowseEnrollment') 'SiteService enrollment must provide a file picker.'
        $browseUpdate = Get-MsiValue $db "SELECT ``Argument`` FROM ``ControlEvent`` WHERE ``Dialog_`` = 'SiteEnrollmentDlg' AND ``Control_`` = 'Browse' AND ``Event`` = '[SITE_ENROLLMENT_FILE]'"
        Assert-Msi ($browseUpdate -eq '[SITE_ENROLLMENT_SELECTION]') 'The file picker must publish the selected path to the visible field.'
        Assert-Msi ('SITE_ENROLLMENT_SELECTION' -in $hidden.Split(';')) 'The file picker selection must be hidden in MSI logs.'
        $resolver = Get-MsiValue $db "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'ResolveRole'"
        Assert-Msi ([int]$resolver -lt 1000 -and [int]$resolver -gt 0) 'Persisted role must resolve before component costing.'
        $roleRegistry = Get-MsiValue $db "SELECT ``Value`` FROM ``Registry`` WHERE ``Registry`` = 'InstallationRole'"
        Assert-Msi ($roleRegistry -eq '[INSTALL_ROLE]') 'Installed role must be persisted for repair and upgrade.'
        Assert-Msi ('SITE_ENROLLMENT_FILE' -in $hidden.Split(';')) 'The enrollment file path must be hidden in MSI logs.'
        foreach ($entry in @(@('ServerInformation','App'),@('SiteServiceInformation','SiteService'),@('WindowsWorkerInformation','WindowsWorker'))) {
            $componentCondition = Get-MsiValue $db "SELECT ``Condition`` FROM ``Component`` WHERE ``Component`` = '$($entry[0])'"
            if ($entry[1] -eq $Role) {
                Assert-Msi ($componentCondition -eq ('INSTALL_ROLE="' + $entry[1] + '"')) "Service ownership must be limited to its role: $($entry[0])"
            } else { Assert-Msi ([string]::IsNullOrEmpty($componentCondition)) "Other role component must be absent: $($entry[0])" }
        }
        $view = $db.OpenView('SELECT `File`.`FileName`, `Component`.`Condition` FROM `File`, `Component` WHERE `File`.`Component_` = `Component`.`Component`')
        try {
            [void]$view.Execute()
            while ($record = $view.Fetch()) {
                try {
                    $filename = ($record.StringData(1) -split '\|')[-1]
                    $condition = $record.StringData(2)
                    if ($filename -in @('CloneDeploy-Server.exe','postgres.exe','CloneDeploy-WinPeBuilder.exe','Configure-Server.ps1','ServerFirewall.ps1')) {
                        Assert-Msi ($condition -eq 'INSTALL_ROLE="App"') "App payload must not install for SiteService: $filename"
                    }
                    if ($filename -in @('CloneDeploy-SiteService.exe','Configure-SiteService.ps1','SiteServiceFirewall.ps1','CloneDeploy-WindowsWorker.exe')) {
                        Assert-Msi ($condition -eq ('INSTALL_ROLE="' + $Role + '"')) "Service payload must have the package's fixed role: $filename"
                    }
                } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            }
        } finally { $view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        $credential = Get-MsiValue $db "SELECT ``Registry`` FROM ``Registry`` WHERE ``Name`` = 'DatabasePassword'"
        Assert-Msi ([string]::IsNullOrEmpty($credential)) 'Credentials must not enter MSI Registry tables or action logs.'
        foreach ($action in @('ConfigureServer','RollbackServer','CommitServer','UninstallServer')) {
            $type = [int](Get-MsiValue $db "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action`` = '$action'")
            Assert-Msi (($type -band 0xC00) -eq 0xC00) "$action must be deferred and elevated."
            Assert-Msi (($type -band 0x2000) -ne 0) "$action must hide CustomActionData."
            Assert-Msi ($action -in $hidden.Split(';')) "$action data must be a hidden property."
            $target = Get-MsiValue $db "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action`` = '$action'"
            Assert-Msi ($target -notmatch 'PASSWORD') 'Passwords must not be passed on command lines.'
        }
        $condition = Get-MsiValue $db "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'UninstallServer'"
        Assert-Msi ($condition -match 'NOT UPGRADINGPRODUCTCODE') 'Major upgrades must retain the service registrations.'
        foreach ($value in @('Local','Existing')) {
            $mode = Get-MsiValue $db "SELECT ``Value`` FROM ``RadioButton`` WHERE ``Property`` = 'DB_MODE' AND ``Value`` = '$value'"
            Assert-Msi ($mode -eq $value) "Missing database option: $value"
        }
        $view = $db.OpenView('SELECT `FileName` FROM `File`')
        try {
            [void]$view.Execute()
            $files = @()
            while ($record = $view.Fetch()) {
                try { $files += ($record.StringData(1) -split '\|')[-1] }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            }
        } finally { $view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        $appFiles = @('CloneDeploy-Server.exe','CloneDeploy-WinPeBuilder.exe','pg_ctl.exe','postgres.exe','psql.exe','citext.control','Configure-Server.ps1','ServerFirewall.ps1','ServerSecrets.ps1','AccountRights.cs','CloneDeploy-Agent-x64.msi','CloneDeploy-Agent-x64.msi.json','CloneDeploy-Agent-arm64.msi.json','win-x64.zip')
        $siteFiles = @('CloneDeploy-SiteService.exe','Configure-SiteService.ps1','SiteServiceFirewall.ps1')
        $workerFiles = @('CloneDeploy-WindowsWorker.exe','Configure-SiteService.ps1','AccountRights.cs')
        $required = if ($Role -eq 'App') { $appFiles } elseif ($Role -eq 'WindowsWorker') { $workerFiles } else { $siteFiles + @('AccountRights.cs') }
        if ($Role -ne 'WindowsWorker') { $required += @('MulticastFirewall.ps1', 'udp-sender.exe') }
        $excluded = if ($Role -eq 'App') { $siteFiles + @('CloneDeploy-WindowsWorker.exe') } else { @($appFiles | Where-Object { $_ -ne 'AccountRights.cs' }) }
        if ($Role -eq 'WindowsWorker') { $excluded += @('CloneDeploy-SiteService.exe','Yarp.ReverseProxy.dll','SiteServiceFirewall.ps1','udp-sender.exe','MulticastFirewall.ps1') }
        if ($Role -eq 'SiteService') { $excluded += @('CloneDeploy-WindowsWorker.exe','CloneDeploy-WinPe.dll','7za.exe') }
        foreach ($name in $excluded) { Assert-Msi ($name -notin $files) "Other role payload must not ship: $name" }
        foreach ($name in $required) {
            Assert-Msi ($name -in $files) "Missing required payload: $name"
        }
        Assert-Msi ('appsettings.Development.json' -notin $files) 'Development settings must not ship.'
        foreach ($runtime in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
            Assert-Msi ($runtime -notin $files) "Server MSI must use the shared runtime: $runtime"
        }
        $builderControl = Get-MsiValue $db "SELECT ``Event`` FROM ``ServiceControl`` WHERE ``Name`` = 'CloneDeploy WinPE Builder'"
        if ($Role -eq 'App') { Assert-Msi (([int]$builderControl -band 34) -eq 34) 'The WinPE builder must stop before upgrade or uninstall replaces its files.' }
        else { Assert-Msi ([string]::IsNullOrEmpty($builderControl)) 'Site Service must not control the WinPE builder.' }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) }
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($script:Installer) }
Write-Host "$checks server MSI checks passed. No installation was performed."
