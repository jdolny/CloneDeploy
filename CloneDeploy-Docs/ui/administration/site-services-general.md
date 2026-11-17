# General

Review registration details and the latest service status in one view. Values use the same read-only layout as host and runtime information.

<div class="ui-controls-spacer"></div>

**Service name / Registration ID**

Identifies this registration; match it to the intended installation.

**Main server IP**

Destination used for SiteService’s enrollment, status and forwarded connections. It must reach the main app rather than loop to the proxy.

**Agent / imaging service URL**

Public HTTPS endpoint used by clients to reach the Site Service.

**Service and host status**

Review the latest reported state. Contact and runtime status can become stale if connectivity fails.

**Enrollment status**

Created awaits import; Pending awaits approval; Approved can retrieve configuration; Revoked has lost access.

**Proxy status**

Reports disabled, awaiting contact, running, starting/stopped or overdue contact. This is not a live network probe.

**Last contact (UTC)**

Last successful contact recorded by the server.

**Configuration status**

Applied means the service acknowledged the current revision; Waiting for service means it has not yet acknowledged it.

**Reported host / addresses**

Network identity reported by the installation, useful for checking that approval targets the right machine.

**Certificate expiry (UTC)**

Current service certificate expiry. Managed certificates renew automatically while the service and CA remain authorized.

**Refresh / errors**

Reload the latest report and read any reported startup/configuration error.

Windows Workers show worker capabilities instead of an HTTPS listener and do not show client service URLs or PXE / TFTP status.

## Permissions

Administrator access is required.
