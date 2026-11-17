# WinPE Builder

Prepare the Windows builder and review its setup jobs. Queued builds wait while the builder is offline. Restart its host when setup reports a restart requirement.

<div class="ui-controls-spacer"></div>

**Status**

Inspect the builder connection and required toolchain readiness.

**History**

Review prerequisite installation outcomes and logs.

**Install prerequisites**

Opens the pinned ADK/WinPE download and license review. Accept and install queues setup; Cancel leaves it unchanged.


**Accept and install / Cancel (prerequisite dialog)**

Review the pinned ADK/WinPE version and Microsoft's download and license information. Accept and install records acceptance and queues prerequisite installation on the Windows builder. Cancel queues nothing. Installation runs the pinned installers, rather than providing an upgrade or automatic uninstall option. Resolve installer failures in History; restart the builder host if Windows requests it.


Prerequisite installation waits for current builds and does not restart the host automatically. Successful installation verifies the exact required versions and build tools. If an installer cannot replace an existing kit, inspect its failure and logs in History before retrying.

## Permissions

Administrator access is required.
