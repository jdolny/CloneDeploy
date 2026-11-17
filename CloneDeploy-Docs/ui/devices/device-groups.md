# Device Groups

Organize devices into reusable groups for policy targeting, imaging defaults and scheduled power actions. Groups can use manual or dynamic membership; the built-in All Devices group includes enrolled, non-archived devices automatically.

<div class="ui-controls-spacer"></div>

**Search**

Find groups by the information displayed in the list.

**Tags / Match**

Choose tags and match any or all selected tags. Remove a selected tag to stop filtering by it.

**Name**

Select a group to open its settings and tabs.

**Membership mode**

Identifies a manual, dynamic or built-in all-devices group.

**Devices**

The current recorded member count.

**Last evaluated**

When dynamic membership was last evaluated.

**New device group**

Enter a name and description to create a group, then configure its membership and assignments.

**Delete / Delete selected**

Removes a group or the groups selected with row checkboxes after confirmation. Review its assignments first. The built-in All Devices group cannot be deleted. Deleting a group does not delete its member devices.

**Wake up devices**

Requests wake-up through suitable connected-agent relays. Sending a packet does not confirm that a device booted.

**Column filters and paging**

Narrow and browse the returned groups.

**Deploy image**

Available from an opened group's actions. Reviews the group image, deployment profile and member devices before queuing Windows deployments. Use Reload review if the saved assignment has changed. Queuing imaging can overwrite member-device disks.

**Deploy multicast**

Available from an opened group's actions. Reviews a multicast deployment and its member devices. Select the multicast storage provider and review the transfer rate and TTL before choosing Queue multicast. Devices deploy on their next applicable PXE or WinPE boot.


**Membership mode (New device group dialog)**

Choose Manual to manage an explicit list or Dynamic to define membership criteria. The required Name accepts up to 200 characters; Description is optional. Create saves the group before membership is configured. Cancel creates nothing.


**Storage / sender host (multicast review)**

Select an eligible provider holding the image. The sender uses that provider's host. The group's image and deployment profile and current member list are displayed for review; this dialog does not edit device overrides.

**Maximum rate (Mbit/s) / Multicast TTL**

For group multicast, the initial rate is 100 Mbit/s and TTL is 1. Rate accepts 1–100,000 Mbit/s and TTL accepts 1–32. TTL 1 stays within the subnet; routed multicast requires network configuration. Only WIM images are supported.

**Queue deployments / Queue multicast / Cancel**

Reload review refreshes the saved group assignment and member list before submission. Queue deployments queues individual deployments; Queue multicast creates the multicast deployment with the reviewed settings. Member status shows results. Cancel closes without submitting; closing after submission does not undo queued work.

## Permissions

Device Group View is required to open the group. Device Group Edit permits creating and deleting groups. Tag View permits loading tags. Wake-up requires Device Group Power Management. Group imaging and multicast require Device Group Deploy Image and Device Group Deploy Multicast, respectively.
