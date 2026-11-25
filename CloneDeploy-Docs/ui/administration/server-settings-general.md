# General

Set the addresses published to devices. These settings do not configure DNS, firewall rules or web-server bindings.

<div class="ui-controls-spacer"></div>

**Public application URL**

HTTPS base URL whose hostname and application path are shared by management, agent/service and portal URLs. The hostname must be covered by the server or proxy certificate and reachable by all three uses. Include the application path if one is used.

Accepts a custom port, for example `https://clonedeploy.example.org:8445/`. Without an explicit port, HTTPS uses 443. The port shown here is used for management links and management Entra sign-in callbacks; it does not change the management listener port. When using a reverse proxy, enter its externally accessible management URL.

When generating agent/service URLs, including new Imager/WinPE preparations, the server replaces this port with the configured `ServicePort` (default 8443). For portal URLs and portal Entra sign-in callbacks, it replaces the port with `PortalPort` (default 8444). These ports must be reachable on the shared hostname.

**Main server IP**

Address reachable from Site Services and Windows Worker; used in new setup files.

**PXE boot URL override**

Optional base URL for iPXE scripts, wimboot and boot WIM downloads. Blank uses the application hostname and path over HTTP on port 80. Include the actual port/path for a custom boot endpoint.

**Management / Imaging**

Enables each feature's client API and navigation. Disabling preserves saved configuration and data.

**Save Changes**

Saves all General options together, including Management and Imaging. Address changes apply to future preparations; prepare PXE files again and rebuild affected WinPE media when needed. Existing media and installations retain their saved addresses.

## Permissions

Administrator access is required.
