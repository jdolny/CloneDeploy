# Services

Inspect a snapshot of Windows services on the connected endpoint. Check its update time before taking action.

<div class="ui-controls-spacer"></div>

**Service / Name**

The display name and internal service name.

**Status / Startup type**

The reported service state and configured startup mode. This tool does not edit startup type.

**Search services**

Filter the displayed snapshot.

**Refresh**

Collects a new service snapshot.

**Start**

Starts a stopped service.

**Stop / Restart**

Requests the operation for a running service. Dependent applications may be interrupted. The CloneDeploy agent service is protected from these controls.

## Permissions

Device View is required to open the device. Device Services is required for service operations.
