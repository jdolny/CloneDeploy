# PXE / TFTP

Approve the Site Service before configuring its boot roles. Prepare boot files and publish WinPE builds on the main server; the service synchronizes verified files automatically. Configure network DHCP relays and firewall rules separately.

<div class="ui-controls-spacer"></div>

**Enable ProxyDHCP**

Enables boot-information responses.

**Boot listening IPv4 address**

A specific address assigned to this Site Service, shared by ProxyDHCP and TFTP. Reported addresses help identify it.

**TFTP server IPv4 address**

The boot listening address for local TFTP, or an external TFTP server's address.

**Listen on UDP 67**

Enables DHCP discovery listening. Disable it if DHCP already owns this port on the address.

**Listen on UDP 4011**

Enables PXE boot request listening.

**Enable read-only TFTP (UDP 69)**

Serves boot files from the service's dedicated local cache.

**Save Changes**

Saves the boot configuration.

Boot filenames and native WinPE assignments come from the main server. Active transfers delay configuration changes; disabling the roles stops transfers. Reported status shows whether each role is running and any errors. Boot status requires a Site Service version with boot-role support.

## Permissions

Administrator access is required.
