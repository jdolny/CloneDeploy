# Settings

ProxyDHCP supplies boot information alongside your existing DHCP server. It does not replace address assignment.

> [!IMPORTANT]
> DHCP options 66 and 67 must be unset for clients using ProxyDHCP. DHCP-supplied boot instructions can cause clients to bypass the proxy. For clients on another subnet, discovery must reach this responder through a DHCP relay/IP helper in addition to the existing DHCP server.

<div class="ui-controls-spacer"></div>

**Enable ProxyDHCP**

Enables the boot-information service on UDP 67 and 4011. Both ports must be available on the selected listener address; another DHCP or PXE service using those ports causes a listener conflict.

**Listen IPv4 address / Refresh addresses**

Choose an address on this server; `0.0.0.0` listens on all interfaces. Refresh rescans available addresses.

**Server identifier override**

Reachable IPv4 address advertised as this server. Required for an all-interface listener; otherwise the listen address can be used.

**TFTP server IPv4 address**

Server from which clients should download their boot files.

**BIOS / UEFI x86 / UEFI x64 boot file**

Paths relative to the TFTP root. For Secure Boot x64, the managed path is `ipxeboot/x86_64-sb/ipxe-shim.efi`; its companion loader remains in the same directory.

**Save settings**

Applies within about five seconds and briefly restarts listeners. Allow the enabled UDP ports through the firewall.

**Refresh status / Retry**

Reload listener state without changing configuration.

## Permissions

Administrator access is required.
