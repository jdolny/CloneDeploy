# Devices

Find devices, review their status and open their details for management or support. The list includes enrolled computers and records created before agent enrollment.

Connection and activity information updates for displayed devices while the page is open. A recent check-in does not guarantee a current connection. Inventory and other recorded information reflect the last data received.

<div class="ui-controls-spacer"></div>

**Active / All / Unmanaged**

Choose which enrollment states to include. **Active** shows enrolled devices and is selected by default. **All** includes other enrollment states and archived records. **Unmanaged** shows records marked as unmanaged. Active does not mean the agent is currently connected.

**Search devices**

Search by device name, serial number, last known IP address, device GUID, installation ID, recorded login name or custom attribute value. Results update as you type. The information icon beside the search box displays search guidance.

**Result limit**

Set the maximum number of records returned. The default is **50**; select **All** to remove the limit. Results are fetched in order of most recent check-in. Increase the limit if an expected device is missing.

**Filter by tags**

Shows or hides the tag filters. Hiding the panel keeps the selected filters applied; the button displays the number of selected tags.

**Tags**

Find and select tags to filter the devices. Selected tags appear as chips. Remove a chip to stop filtering by that tag.

**Match**

Choose **Any tag (OR)** to include devices with at least one selected tag, or **All tags (AND)** to require every selected tag.

**Clear tags**

Removes all selected tag filters. The search text and enrollment-state selection remain applied.

**Name**

Select a device name to open its details, including inventory, imaging settings, policies and history.

**Column filters and paging**

Use the available column filters to narrow the returned records. Use the pager to move through results or change the number of rows displayed per page. These controls work within the records fetched by the search, enrollment-state and tag filters; they cannot find records excluded by the result limit.

**Add device**

Creates device records before agent enrollment. In the dialog, enter one name per line, up to 500 names, then select **Add devices**. Duplicate names and existing records are skipped. Creating a record does not install an agent; after creation, the list switches to **All**.

**Columns & actions**

Choose the columns and row actions displayed in your list. Drag items between **Available** and **Shown**, then reorder them within their column or action group. Columns include device identity, connection and activity information, inventory details, enrollment state and custom attributes.

Row actions depend on your saved layout and permissions.

Select **Save** to store your layout, **Reset to defaults** to restore the default selections in the dialog, or **Cancel** to discard edits. Save after resetting to keep the default layout.

**Connect**

Opens the device's connection panel from its row. Available tools depend on your permissions, enabled features, endpoint platform and agent connectivity.

**Archive / Archive selected**

Archives one device or the devices selected with the row checkboxes. Review the device names in the confirmation before proceeding. Archiving retains the records while taking them out of active management.

**Archive selected** appears after selecting devices. The result reports the number of successful and failed operations.

**Restore**

Replaces the Archive row action for an archived device. After confirmation, removes its archived state. Previously enrolled devices return to enrolled status when their enrollment information is still present; other records return to pre-created status.

**Delete / Delete selected**

Permanently removes one device or the selected records after confirmation. Review the listed names carefully. Use Archive when you need to retain the device's record and history.

**Delete selected** appears after selecting devices. The result reports the number of successful and failed operations.


**Device names (Add devices dialog)**

Enter one device name per line. The input accepts up to 65,000 characters; submission accepts up to 500 names. Cancel closes without adding records.


**Wake up**

Where the saved layout and permissions expose this action, requests wake-up through a suitable connected-agent relay. Sending a magic packet does not confirm startup. Wake-up requires Device Wake permission.

## Permissions

**Device View** is required to open this page. **Device Add** permits creating device records. **Device Edit** permits archiving, restoring and deleting them. Selecting tags requires **Tag View** permission. Individual connection tools enforce their own permissions.
