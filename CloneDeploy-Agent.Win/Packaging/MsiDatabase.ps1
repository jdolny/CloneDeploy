# Shared Windows Installer database operations. No installation or execution of MSI custom actions.
function Invoke-MsiSql($Database, [string]$Sql, [string[]]$Values = @()) {
    $view = $Database.OpenView($Sql)
    try {
        if ($Values.Count) {
            $record = $script:Installer.CreateRecord($Values.Count)
            try {
                for ($i = 0; $i -lt $Values.Count; $i++) { $record.StringData($i + 1) = $Values[$i] }
                [void]$view.Execute($record)
            } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        } else { [void]$view.Execute() }
    } finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}

function Set-MsiProperty($Database, [string]$Name, [string]$Value) {
    Invoke-MsiSql $Database 'DELETE FROM `Property` WHERE `Property` = ?' @($Name)
    if ($Value) { Invoke-MsiSql $Database 'INSERT INTO `Property` (`Property`, `Value`) VALUES (?, ?)' @($Name, $Value) }
}

function Get-MsiValue($Database, [string]$Sql) {
    $view = $Database.OpenView($Sql)
    try {
        [void]$view.Execute(); $record = $view.Fetch()
        if ($record) {
            try { return $record.StringData(1) }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        }
    } finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}

function Protect-MsiEnrollment($Database) {
    # Windows Installer 5 applies this ACL when writing the enrollment registry key.
    # SIDs make this independent of the Windows display language.
    if (!(Get-MsiValue $Database "SELECT ``Name`` FROM ``_Tables`` WHERE ``Name`` = 'MsiLockPermissionsEx'")) {
        $create = $Database.OpenView('CREATE TABLE `MsiLockPermissionsEx` (`MsiLockPermissionsEx` CHAR(72) NOT NULL, `LockObject` CHAR(72) NOT NULL, `Table` CHAR(32) NOT NULL, `SDDLText` CHAR(255) NOT NULL, `Condition` CHAR(255) PRIMARY KEY `MsiLockPermissionsEx`)')
        try { [void]$create.Execute(); [void]$Database.Commit() }
        finally { [void]$create.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($create) }
    }
    Invoke-MsiSql $Database "DELETE FROM ``MsiLockPermissionsEx`` WHERE ``MsiLockPermissionsEx`` = 'EnrollmentAcl'"
    Invoke-MsiSql $Database 'INSERT INTO `MsiLockPermissionsEx` (`MsiLockPermissionsEx`, `LockObject`, `Table`, `SDDLText`) VALUES (?, ?, ?, ?)' @('EnrollmentAcl', 'EnrollmentServer', 'Registry', 'D:P(A;;KA;;;SY)(A;;KA;;;BA)')
    $summary = $Database.SummaryInformation(2)
    try {
        $summary.Property(14) = 500
        $summary.Property(9) = [guid]::NewGuid().ToString('B').ToUpperInvariant()
        [void]$summary.Persist()
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
}
