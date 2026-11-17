# Requirements & Planning


## Supported platforms

**Application Server**

The main application can be installed on Windows x64 Server 2016 or later, or on any Linux x64 distribution that supports .NET 10.
The Windows installer includes PostgreSQL 18, or an existing PostgreSQL database can be used.
- Requires a stable IP address and DNS hostname.
- Recommended minimum 4 CPU cores, 16 GB RAM (32 GB recommended for large deployments > 1000 devices), and 100 GB storage for the application and database. Additional storage is required for deployment content.
- 10GbE network is recommended for large deployments, especially when imaging.

**Agent**

The device agent runs on Windows x64, ARM64, and macOS on Apple silicon (ARM64).  It can be installed on computer that supports .NET 10.  The agent package includes its runtime; available actions vary by platform.

**Imaging**

Imaging is supported on Windows x64 and ARM64, including both legacy BIOS and UEFI boot modes.

**Site Service**

Site Services can be installed on Windows x64 Server 2016 or later, or on any Linux x64 distribution that supports .NET 10.  The site services provides a reverse proxy for agents and imaging clients to
reach the main application when it is not directly accessible, such as the Internet or a separate network segment.  It can also provide PXE boot services and replicate content to a local storage share.
- Requires a stable IP
- Recommended minimum 4 CPU cores, 16 GB RAM, and 50 GB storage for the application. Additional storage is required for deployment content.
- 10GbE network is recommended for large deployments, especially when imaging.

**Windows Worker**

A Windows Worker requires a Windows 11 or Windows Server 2016 or later host.  This is required if the main application is installed on Linux and OS imaging is used.
- Requires a stable IP
- Recommended minimum 2 CPU cores, 8 GB RAM, and 50 GB storage for the application.
- 1GbE network is recommended.

## Required network ports


### Application and storage

| Connection | Default | Purpose |
| --- | --- | --- |
| Administrator browser → Main App | TCP 443 | Management Web UI. Access is restricted to private network addresses and loopback. |
| Agents and Imager → Main App or approved Site Service | TCP 8443 | Authenticated agent and imaging communication, including content transfers and remote tools. |
| Site Services and Windows Worker → Main App | TCP 8443 | Enrollment, configuration, work requests and authenticated service communication. |
| Endpoint portal sign-in browser → Main App or Site Service | TCP 8444 | Portal and agent-tools browser sign-in. |
| Setup browser or network boot client → Main App | TCP 80 | Initial HTTP setup and HTTP boot content when used. After HTTPS is enabled, the Main App's HTTP listener serves boot routes only. |
| Main App, Site Service or direct SMB imaging client → file server | TCP 445 | SMB storage access when used. |

Agents and Imager can connect through an approved Site Service. Ensure DNS and routing work from each client network, and plan how clients will trust the HTTPS certificate.

### Network boot and multicast imaging

| Connection | Default | Purpose |
| --- | --- | --- |
| PXE client ↔ existing DHCP server, directly or through a relay | UDP 67 on the server; UDP 68 on the client | IP address assignment by the existing DHCP service. CloneDeploy ProxyDHCP does not assign addresses. |
| PXE client or DHCP relay → Main App or Site Service running PXE Proxy | UDP 67 | PXE discovery. Relayed replies return to the relay on UDP 67; local client discovery replies use UDP 68. |
| PXE client → advertised Main App or Site Service PXE responder | UDP 4011 | Requests boot-server and boot-file selection after receiving an IP address. |
| PXE client → selected TFTP server | UDP 69 initially, then negotiated UDP transfer ports | Downloads initial boot files. Both the Main App and Site Service can provide TFTP. |
| Multicast sender (Main App or Site Service) ↔ Imager clients | UDP 39000–40999 | WIM multicast data and receiver control/reply traffic. Required only for multicast deployment. |


### Supporting services and optional integrations

| Connection | Default | Purpose |
| --- | --- | --- |
| Application hosts and clients → configured DNS servers | UDP/TCP 53 | Resolve the public application hostname and other configured service names. |
| Main App → LDAP directory server | TCP 636 by default | LDAPS directory synchronization, when configured. Use the directory connection's saved port. |
| Main App → SMTP relay | TCP 587 by default | Email notifications with STARTTLS, when enabled. Use the saved SMTP port; implicit TLS on TCP 465 is not supported. |
| Main App and sign-in browsers → Microsoft identity/Graph endpoints | TCP 443 | Microsoft sign-in and Entra synchronization, when configured. |
| Main App or Windows builder → configured download sources | TCP 443; TCP 80 for HTTP sources | Required downloads for enabled features, such as ADK/WinPE prerequisites, PXE tools, driver catalogs, WinGet metadata or URL-based content. Allow only the sources used by your deployment. |
| Connected wake-up relay agent → target subnet broadcast | UDP 9 | Wake-on-LAN magic packets, when wake-up is used. The relay receives its instruction through the normal authenticated agent connection. |


