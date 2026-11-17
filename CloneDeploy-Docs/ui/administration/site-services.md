# Site Services

Register remote SiteService installations and manage their reverse-proxy role. Agents and Imager use one public URL; SiteService connects to the main server IP while validating HTTPS against the public hostname.

<div class="ui-controls-spacer"></div>

**Search and add**

Search by service name, hostname or main server IP. Open a name to view its tabs.

**Add SiteService**

Opens the registration dialog for Service name and the optional Windows Worker role. Save the public application URL and Main server IP in Server Settings before creating the setup file.

**Create and download setup file**

Creates a registration and downloads JSON containing its backend IP, inherited URL, public CA and single-use token. The token is valid for 24 hours; no private key is exported.

**Download setup file again**

Retries the same in-memory download after a browser download failure, without creating another service.

**Installer import**

Run the installer for the selected Site Service or Windows Worker role and select the JSON. The service generates its keys locally and requests approval.

**Refresh**

Reloads registration and contact status. A refresh does not send a wake-up or prove connectivity.

**General**

Review registration details and the latest service status in one view. Values use the same read-only layout as host and runtime information.

**Configuration**

A new registration must first submit its enrollment request before configuration and approval are available.

**Enrollment**

Track enrollment, download replacement setup files before redemption, and compare the installation fingerprint.

**PXE / TFTP**

Approve the Site Service before configuring its boot roles. Prepare boot files and publish WinPE builds on the main server; the service synchronizes verified files automatically. Configure network DHCP relays and firewall rules separately.


**Windows Worker (one per Linux main server)**

In Add SiteService, select this only when registering the Windows builder for a Linux Main App. Availability depends on whether this installation can create a worker. Leave it cleared for a normal Site Service.

**Service name**

Required registration name, up to 100 characters. The public application URL and backend connection settings come from saved server configuration; this dialog does not provide a Main server IP field.

**Cancel / Open service**

Cancel closes before creation. If registration succeeded but the setup-file download failed, Open service opens the created registration. Retrying the download uses that registration rather than creating another.

## Permissions

Administrator access is required.
