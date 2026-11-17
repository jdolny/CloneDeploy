# Configuration

A new registration must first submit its enrollment request before configuration and approval are available.

Approved Site Services always enable their service and portal HTTPS listeners. Use the individual capability switches to control agent communication, enrollment, imaging, portal sign-in, agent tools, and multicast. Windows Workers do not expose these listeners. Existing approved Site Services with listeners disabled enable them after the main server upgrade and their next successful configuration poll.

<div class="ui-controls-spacer"></div>

**Identity key fingerprint**

Compare with local import output or `ProgramData/CloneDeploy/SiteServiceSetup/identity-fingerprint.txt` before approval.

**HTTPS listener IP**

Specific IP assigned to SiteService. Use a different IP from the main app when sharing its port; configure IP assignment separately.

**Allow agent communication**

Permits normal agent communication through this Site Service.

**Allow agent enrollment**

Permits new agent enrollment requests through the service.

**Allow user portal sign-in**

Permits user portal authentication through the service.

**Allow agent tools sign-in**

Permits direct agent-tools authentication through the service, subject to its client-network restrictions.

**Allow imaging through this proxy**

Permits imaging traffic through the service.

**Enable multicast sender (requires storage)**

Enables the multicast sender when suitable site storage is available.

**Agent tools client networks (CIDR)**

Restricts the client networks allowed to use direct agent tools.

**Enrollment requests per minute / Imager login requests per minute**

Set the per-minute authentication request limits.

**Site replication bandwidth (MiB/s)**

Set the site replication bandwidth cap. Zero applies no additional site cap; the overall replication limit still applies.

**Approve and configure**

Issues the service certificates and enables the approved configuration for a pending request.

**Save configuration**

Saves role/listening-IP changes for an approved service. The service applies changes on its next successful poll; listener restarts can interrupt transfers.

**Content storage**

Storage is available automatically when at least one enabled storage provider is assigned to this Site Service. With no enabled providers, it operates as a proxy only. Main still authorizes requests and selects a provider based on available content, client-network matching, and the existing selection rules. There is no separate storage capability switch. Disabling the last provider prevents new storage work immediately on main; the service receives the updated capability on its next successful heartbeat. Existing stored files are retained. Multicast also requires its separate capability to be enabled.

**Replication limits**

For approved services, expand **Replication limits** to set concurrent shared/management files (1–16) and an optional site replication bandwidth cap in MiB/s. The file limit covers all providers on this site. One image can copy alongside those files. A bandwidth value of 0 means no additional site cap; the overall cap in Storage Providers still applies.

Until site limits are saved, shared-file concurrency follows the global default (initially 2). Saving pins the displayed values for this site. Bandwidth changes affect active copies within a few seconds. File concurrency changes apply on a subsequent storage polling cycle, after existing work finishes. Use **Save replication limits** to save these independently of role and listening-IP changes.

## Permissions

Administrator access is required.
