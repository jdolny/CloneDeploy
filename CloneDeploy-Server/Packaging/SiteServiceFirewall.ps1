# Installer-owned inbound rules. Loading this file makes no firewall changes.
function Get-SiteServiceFirewallNames { return @('CloneDeploy-SiteService-Web-In', 'CloneDeploy-SiteService-PXE-In', 'CloneDeploy-SiteService-TFTP-In') }
function Assert-SiteServiceFirewallOwner($Rule, [string]$Program) {
    $application = $Rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
    if ($Rule.Group -ne 'CloneDeploy SiteService Setup' -or $application.Program -ne $Program) {
        throw "Firewall rule '$($Rule.Name)' belongs to another application or installation."
    }
}
function Install-SiteServiceFirewall([string]$Program, [scriptblock]$BeforeCreate) {
    $Program = [IO.Path]::GetFullPath($Program)
    # HTTPS uses the default service and portal ports. Custom ports require an administrator rule.
    # Keep UDP boot services limited to Domain and Private networks.
    $rules = @(
        @{ Name = 'CloneDeploy-SiteService-Web-In'; Protocol = 'TCP'; Ports = @('8443','8444'); Profiles = @('Domain','Private','Public'); Description = 'SiteService HTTPS service (8443) and portal (8444) listeners.' },
        @{ Name = 'CloneDeploy-SiteService-PXE-In'; Protocol = 'UDP'; Ports = @('67','4011'); Profiles = @('Domain','Private'); Description = 'ProxyDHCP discovery and boot requests.' },
        @{ Name = 'CloneDeploy-SiteService-TFTP-In'; Protocol = 'UDP'; Ports = @('69','49152-65535'); Profiles = @('Domain','Private'); Description = 'TFTP requests and Windows default dynamic transfer ports.' }
    )
    foreach ($spec in $rules) {
        $name = $spec.Name
        $existing = Get-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction SilentlyContinue
        if ($existing) {
            Assert-SiteServiceFirewallOwner $existing $Program
            continue # Preserve administrator restrictions on repair/upgrade.
        }
        & $BeforeCreate $name
        New-NetFirewallRule -Name $name -DisplayName $name -Group 'CloneDeploy SiteService Setup' `
            -Description $spec.Description -Direction Inbound -Action Allow -Enabled True -Profile $spec.Profiles `
            -Program $Program -Protocol $spec.Protocol -LocalPort $spec.Ports -RemotePort Any -EdgeTraversalPolicy Block -PolicyStore PersistentStore -ErrorAction Stop | Out-Null
    }
}
function Remove-SiteServiceFirewall([string]$Program, [string[]]$Names) {
    $Program = [IO.Path]::GetFullPath($Program)
    foreach ($name in $Names) {
        if ($name -notin (Get-SiteServiceFirewallNames)) { throw 'Unexpected SiteService firewall rule name.' }
        $rule = Get-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction SilentlyContinue
        if (!$rule) { continue }
        Assert-SiteServiceFirewallOwner $rule $Program
        Remove-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction Stop
    }
}
