# Installer-owned UDPcast rules. Loading this file makes no firewall changes.
function Get-MulticastFirewallNames([string]$Role) {
    if ($Role -notin @('App','SiteService')) { throw 'Invalid multicast firewall role.' }
    return @("CloneDeploy-$Role-Multicast-x64-In", "CloneDeploy-$Role-Multicast-a64-In")
}
function Get-MulticastFirewallProgram([string]$Root, [string]$Name) {
    $arch = if ($Name -like '*-a64-*') { 'a64' } else { 'x64' }
    return Join-Path ([IO.Path]::GetFullPath($Root)) "Tools\udpcast\$arch\udp-sender.exe"
}
function Assert-MulticastFirewallOwner($Rule, [string]$Program) {
    $application = $Rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
    if ($Rule.Group -ne 'CloneDeploy Multicast Setup' -or $application.Program -ne $Program) {
        throw 'Multicast firewall rule belongs to another installation.'
    }
}
function Install-MulticastFirewall([string]$Root, [string]$Role, [scriptblock]$BeforeCreate) {
    foreach ($name in (Get-MulticastFirewallNames $Role)) {
        $program = Get-MulticastFirewallProgram $Root $name
        if (!(Test-Path -LiteralPath $program -PathType Leaf)) { throw 'The bundled multicast sender is missing.' }
        $existing = Get-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction SilentlyContinue
        if ($existing) {
            Assert-MulticastFirewallOwner $existing $program
            continue # Repairs/upgrades preserve administrator restrictions.
        }
        & $BeforeCreate $name
        New-NetFirewallRule -Name $name -DisplayName $name -Group 'CloneDeploy Multicast Setup' `
            -Description 'UDPcast imaging sessions and receiver replies.' -Direction Inbound -Action Allow `
            -Enabled True -Profile Domain,Private -Program $program -Protocol UDP -LocalPort '39000-40999' `
            -RemotePort '39000-40999' -EdgeTraversalPolicy Block -PolicyStore PersistentStore -ErrorAction Stop | Out-Null
    }
}
function Remove-MulticastFirewall([string]$Root, [string]$Role, [string[]]$Names) {
    $allowed = Get-MulticastFirewallNames $Role
    foreach ($name in $Names) {
        if ($name -notin $allowed) { throw 'Unexpected multicast firewall rule name.' }
        $rule = Get-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction SilentlyContinue
        if (!$rule) { continue }
        Assert-MulticastFirewallOwner $rule (Get-MulticastFirewallProgram $Root $name)
        Remove-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction Stop
    }
}
