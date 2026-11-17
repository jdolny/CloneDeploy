# Understanding ProxyDHCP and IP Helpers

CloneDeploy's PXE Proxy helps computers find the correct boot files when starting from the network. Your existing DHCP server continues to assign IP addresses, subnet masks, gateways and DNS settings. ProxyDHCP supplies the additional boot information needed to start WinPE; it has no address pool and does not replace your DHCP server.

This guide covers IPv4 network boot using either the Main App or an approved Site Service as the PXE responder. PXE Proxy is separate from the Site Service HTTPS reverse proxy. ISO/USB boot does not require ProxyDHCP or TFTP discovery.

> [!IMPORTANT]
> **Remove DHCP options 66 and 67 for clients that will use CloneDeploy ProxyDHCP.** Check server defaults, scopes, reservations and DHCP policies, including inherited settings. These options can direct clients to a fixed boot server or file, causing them to bypass the proxy's selection or contact the wrong server. Adding an IP helper does not override boot instructions already supplied by DHCP.

## Why CloneDeploy uses ProxyDHCP

ProxyDHCP lets CloneDeploy **select the boot environment dynamically for each client**, instead of requiring a fixed boot server and filename in DHCP. You can maintain multiple WinPE environments and change their selection in CloneDeploy without editing DHCP boot options whenever hardware or imaging requirements change.

This supports several choices on the same imaging network:

- **Different architectures:** return the appropriate boot files for x64 and ARM64 clients, with separate default boot environment selections.
- **Native PXE or iPXE:** use the boot method configured for the selected environment, allowing environments with different boot methods to coexist.
- **Different computer models:** use manufacturer/model matching to select a WinPE environment with the drivers or settings needed by a particular hardware family.
- **Default environments:** use a ready default when no model rule matches, so every supported model does not need its own rule.

For example, you can use a default x64 environment for most computers, a separate WinPE environment with additional network drivers for a particular laptop model, and an ARM64 default for ARM64 computers. Configure those environments, boot methods and matching rules in CloneDeploy; the DHCP server continues supplying the same address settings.

Architecture is available during PXE discovery. Model-based selection depends on the manufacturer/model information already known to CloneDeploy; the initial PXE request does not supply a full hardware inventory. A first-boot device without inventory can use a ready Native PXE default for its architecture. Without a compatible native default, it follows the iPXE flow. Build environments successfully and resolve conflicting model rules before relying on automatic selection.

## What happens during network boot

1. The computer's PXE firmware broadcasts a discovery request on its local network.
2. The DHCP server offers an IP address and the usual network settings. CloneDeploy responds separately to identify itself as a PXE boot service, without assigning an address.
3. After obtaining an address, the client requests boot information from CloneDeploy on UDP 4011.
4. CloneDeploy returns the TFTP server address and an appropriate boot file for the client's architecture and boot environment selection.
5. The client downloads the initial boot files using TFTP. The selected iPXE or Native PXE flow then loads WinPE, which connects to CloneDeploy using the server settings captured in its build.

Ordinary clients that are not requesting PXE boot information continue to use DHCP normally. CloneDeploy's discovery handler responds to PXE requests, rather than offering leases to every device. A failure of the PXE Proxy can prevent network boot while DHCP address assignment continues to work.

Enabling PXE Proxy makes a boot responder available to PXE clients on the networks that can reach it. Plan which responder serves each imaging network, especially if WDS, Configuration Manager or another PXE service is already present. Multiple boot responders can give clients competing instructions. Test on an intended imaging VLAN before extending the configuration to other networks.

## Why DHCP options 66 and 67 interfere

Option **66 (Boot Server Host Name)** identifies a boot server. Option **67 (Bootfile Name)** specifies a boot file. Supplying them through DHCP establishes a separate source of boot instructions, which can prevent CloneDeploy's ProxyDHCP flow from taking effect. A fixed filename can also be inappropriate for a different firmware architecture.

For the separate DHCP and CloneDeploy responder arrangement described here, leave these options unset for the imaging clients and let CloneDeploy select the boot information. Also remove a legacy DHCP option **60** value of `PXEClient` if it was added to make that separate DHCP server advertise itself as a PXE service. This concerns options configured on the DHCP server; PXE clients still send their own PXE identification.

Microsoft documents boot failures caused by DHCP-supplied PXE options and recommends forwarding discovery to the PXE responder instead. Its examples concern Microsoft PXE services, but explain the same competing boot-information problem. [Microsoft's explanation of DHCP options and PXE failures](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/pxe-clients-not-start-dhcp-60-66-67-option).

## Same subnet: clients can discover the responder directly

When the client and CloneDeploy PXE responder are on the same VLAN/subnet, the responder can receive the local PXE discovery broadcast. No IP helper is needed to reach that local responder. If DHCP is on another subnet, retain the relay configuration that already lets clients reach DHCP.

Allow the required traffic through host and network firewalls. Network controls such as DHCP snooping or relay filtering must also permit the intended PXE responses. Simply being on the same subnet does not bypass those controls.

## Different subnet: add an IP helper for the PXE responder

Routers normally keep local broadcasts within their subnet. A DHCP relay, often configured as an **IP helper** on the client VLAN's gateway, forwards discovery to specified server addresses. An existing helper that forwards only to DHCP lets the computer obtain an address, but does not tell the CloneDeploy responder that a PXE client is waiting.

Keep the existing DHCP helper destinations and add the CloneDeploy PXE responder as another destination on each client VLAN that needs remote network boot. Microsoft describes this arrangement in its [guidance for PXE boot across networks](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/os-deployment/boot-from-pxe-server).

For example, with clients on `192.168.20.0/24`:

| Role | Example address | Gateway configuration |
| --- | --- | --- |
| Client VLAN gateway | `192.168.20.1` | Configure the relay/helpers here. |
| Existing DHCP server | `192.168.10.10` | Keep this helper destination for address assignment. |
| CloneDeploy PXE responder | `192.168.10.20` | Add this helper destination for PXE discovery. |

Use the addresses for your network. Configure the helper on the **client VLAN's gateway interface**, rather than only on the server VLAN. Forward to the Main App's PXE listener address, or to the Site Service hosting PXE for that site. Forwarding discovery only to a TFTP server is insufficient if it does not also run the PXE responder.

Relay commands and forwarded protocols vary by router vendor. Have the network administrator configure DHCP/PXE forwarding, return routing and access rules for the selected responder. The later UDP 4011 request and boot downloads also need connectivity; a helper alone does not open firewalls or provide all required routes.

## Traffic and server placement

| Purpose | Required path |
| --- | --- |
| Address assignment | Client/relay to the existing DHCP server using UDP 67/68, with replies allowed. |
| PXE discovery | Client broadcast, or relay forwarding, to the CloneDeploy PXE responder on UDP 67, with replies allowed. |
| Boot selection | Client to the advertised PXE responder on UDP 4011, with replies allowed. |
| Initial boot downloads | Client to the selected TFTP server on UDP 69, plus the UDP transfer traffic negotiated for downloads. |
| Subsequent boot and imaging connections | Client to the configured boot/application endpoints, including the configured agent/imaging HTTPS listener. |

The Main App's PXE Proxy binds UDP 67 and 4011 when enabled. Another DHCP or PXE service cannot own the same listener address and ports. Use a separate host or Site Service for the PXE role when necessary; adding DHCP option 60 does not resolve a local port conflict.

For a remote site, an approved Site Service can provide local ProxyDHCP and TFTP. Configure its **PXE / TFTP** roles and verify their reported status. It receives prepared boot content from the Main App. Use that service as the responder for its client networks instead of sending discovery to both it and the Main App. Clients must still be able to reach the application endpoints used by their boot environment.

## Verify before the first capture

1. Confirm that DHCP options 66 and 67 are absent from the effective configuration for the test client. Check policies and reservations as well as scope defaults.
2. Verify that the intended PXE responder and TFTP service are running and that the WinPE boot environment and prepared boot files are ready.
3. For a remote responder, confirm that the client VLAN has helper destinations for both DHCP and the intended PXE responder.
4. Network boot a test computer. Confirm that it receives an address, contacts the intended boot service and reaches CloneDeploy Imager.
5. Inspect **Administration → Imaging Settings → PXE Proxy → Live Activity** and **TFTP & Boot Files → Live Activity**, or the Site Service's reported boot status, to identify where progress stops.

| Symptom | What to check first |
| --- | --- |
| No client IP address | Existing DHCP service, available leases, VLAN and DHCP relay path. |
| IP address received, but no CloneDeploy discovery activity | Missing PXE helper destination, wrong listener address, filtering, or another PXE responder. |
| Wrong boot server/file, or proxy appears to be ignored | Effective DHCP options 66/67, legacy option 60, and competing PXE services. |
| UDP 4011 timeout | Advertised responder address, routing and firewall rules; confirm the client is contacting CloneDeploy rather than the DHCP server. |
| Boot file selected, but TFTP download fails | Advertised TFTP address, prepared files, UDP 69 and negotiated transfer traffic. |
| WinPE starts, but cannot connect to CloneDeploy | Network drivers, DNS, routing, HTTPS access and the application URL captured in the WinPE build. |
