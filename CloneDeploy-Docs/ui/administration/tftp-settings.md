# Settings

The integrated TFTP server serves downloads only from the application TFTP root. It rejects upload/write requests. Any reachable client can request files from that root; do not store secrets there.

<div class="ui-controls-spacer"></div>

**Prepare / Update PXE files**

Downloads/verifies iPXE and wimboot and regenerates `autoexec.ipxe`. Enabling TFTP alone does not prepare files. Managed files are replaced; custom files and Generated output are retained.

**Installed versions / Boot script server**

Shows the prepared versions and URL embedded in the script. Prepare again after changing the boot URL.

**Enable TFTP**

Starts serving the prepared boot content while the main app is running.

**Listen IPv4 address / Refresh addresses**

Select a local address or all interfaces, or rescan addresses.

**UDP listen port**

Normally 69 for PXE clients.

**Response timeout (milliseconds) / Retries**

Controls how long a transfer waits for a response and how many retries are attempted.

**Save settings**

Applies within about five seconds. Restarting cancels active transfers; update between boot sessions.

**Refresh status / Retry**

Reloads the listener and file status. Allow both the listening port and UDP transfer traffic through the firewall.

## Permissions

Administrator access is required.
