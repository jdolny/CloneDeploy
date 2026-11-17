# Installer-owned inbound rules. Loading this file makes no firewall changes.
function Get-ServerFirewallNames { return @('CloneDeploy-Server-App-Web-In', 'CloneDeploy-Server-App-PXE-In', 'CloneDeploy-Server-App-TFTP-In') }
function Assert-ServerFirewallOwner($Rule, [string]$Program) {
    $application = $Rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
    if ($Rule.Group -ne 'CloneDeploy Server Application Setup' -or $application.Program -ne $Program) {
        throw "Firewall rule '$($Rule.Name)' belongs to another application or installation."
    }
}
function Get-ServerWebPorts($Configuration) {
    $ports = @()
    $listeners = $Configuration.Server.Listeners
    foreach ($name in @('HttpPort','ManagementPort','ServicePort','PortalPort')) {
        $port = [int]$listeners.$name
        if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid server listener port.' }
        $ports += $port
    }
    return @($ports | Sort-Object -Unique)
}
function Install-ServerFirewall([string]$Program, [int[]]$WebPorts, [scriptblock]$BeforeCreate) {
    $Program = [IO.Path]::GetFullPath($Program)
    if (!$WebPorts.Count -or @($WebPorts | Where-Object { $_ -lt 1 -or $_ -gt 65535 }).Count) { throw 'Invalid web firewall port.' }
    $rules = @(
        @{ Name = 'CloneDeploy-Server-App-Web-In'; Protocol = 'TCP'; Ports = [string[]]$WebPorts; Description = 'HTTP and HTTPS on configured application ports.' },
        @{ Name = 'CloneDeploy-Server-App-PXE-In'; Protocol = 'UDP'; Ports = @('67','4011'); Description = 'ProxyDHCP discovery and boot requests.' },
        @{ Name = 'CloneDeploy-Server-App-TFTP-In'; Protocol = 'UDP'; Ports = @('69','49152-65535'); Description = 'TFTP requests and Windows default dynamic transfer ports.' }
    )
    foreach ($spec in $rules) {
        $name = $spec.Name
        $existing = Get-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction SilentlyContinue
        if ($existing) {
            Assert-ServerFirewallOwner $existing $Program
            # Preserve administrator restrictions on repair/upgrade; recreate missing rules only.
            continue
        }
        & $BeforeCreate $name
        New-NetFirewallRule -Name $name -DisplayName $name -Group 'CloneDeploy Server Application Setup' `
            -Description $spec.Description -Direction Inbound -Action Allow -Enabled True -Profile Domain,Private `
            -Program $Program -Protocol $spec.Protocol -LocalPort $spec.Ports -RemotePort Any -EdgeTraversalPolicy Block -PolicyStore PersistentStore -ErrorAction Stop | Out-Null
    }
}
function Remove-ServerFirewall([string]$Program, [string[]]$Names) {
    $Program = [IO.Path]::GetFullPath($Program)
    foreach ($name in $Names) {
        if ($name -notin (Get-ServerFirewallNames)) { throw 'Unexpected installer firewall rule name.' }
        $rule = Get-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction SilentlyContinue
        if (!$rule) { continue }
        Assert-ServerFirewallOwner $rule $Program
        Remove-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction Stop
    }
}
