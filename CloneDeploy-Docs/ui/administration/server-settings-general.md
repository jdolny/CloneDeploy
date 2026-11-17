# General

Set the addresses published to devices. These settings do not configure DNS, firewall rules or web-server bindings.

<div class="ui-controls-spacer"></div>

**Public application URL**

HTTPS base URL used by new Imager/WinPE preparations and imaging. The hostname must be covered by the server or proxy certificate. Include the application path if one is used.

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
