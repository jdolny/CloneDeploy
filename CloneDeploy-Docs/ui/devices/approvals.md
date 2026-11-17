# Enrollment Requests

Review agent enrollment and certificate-recovery requests. Verify the installation and public key before approval; the decision applies to that installation and key. Agents retry while waiting. Denying a request is separate from archiving an already enrolled device.

<div class="ui-controls-spacer"></div>

**Pending / Denied**

Switch between pending requests and requests that were denied.

**Search requests**

Filter requests by the displayed identifying information.

**Device / Last IP / MAC addresses / Hardware UUID**

Review the device name and reported network and hardware identity.

**Last contact (UTC)**

When the requesting agent last contacted the server, displayed in UTC.

**Status**

Shows the current enrollment or reset stage. Approved waiting for agent indicates that the agent still needs to complete its next enrollment attempt.

**Details**

Shows the installation ID and public-key SHA-256 fingerprint used to verify the request.

**Approve**

Allows a pending enrollment to proceed.

**Pre-create and approve**

Creates the required device record and approves the matching request when enrollment is waiting for a record.

**Approve reset**

Approves certificate recovery awaiting administrator review. Eligible reset requests can be approved automatically if enabled in enrollment settings. Previous certificates become invalid when recovery completes.

**Approve all**

Approves the pending-approval requests in the currently filtered grid after confirmation. Review the confirmation list; this does not bulk-approve reset requests or requests waiting for a device record.

**Deny**

Blocks the request even if automatic approval is subsequently enabled. Deleting the request clears that recorded decision.

**Dismiss**

Removes a pending request from review after confirmation. It can reappear when the agent next requests enrollment.

**Delete**

After confirmation, removes the request, its approval or denial and its reservation. The device and certificates are kept. A later enrollment attempt is evaluated as a new request under current settings.

**Refresh**

Reloads requests and their statuses.

**Column filters and paging**

Narrow and browse the request list.

## Permissions

Device Approve is required to view and manage enrollment requests.
