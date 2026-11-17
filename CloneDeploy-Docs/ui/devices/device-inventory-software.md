# Software

Review installed applications from the last saved inventory. Queuing an uninstall does not refresh inventory.

<div class="ui-controls-spacer"></div>

**Name**

The application name reported by the agent.

**Version**

The original reported application version.

**Major / Minor / Build / Revision**

Numeric components parsed from the reported version, provided for filtering and comparison.

**Search**

Find applications by name, original version or parsed numeric version components.

**Column filters and paging**

Use the available column filters to narrow the loaded records, and the pager to browse them. Filtering does not request a fresh inventory collection.

**Uninstall**

For Windows devices, opens a review for the selected application and version. The device must be unarchived and have a recorded uninstall command. This action is unavailable for macOS.

**Silent uninstall arguments (optional)**

In the uninstall dialog, supply arguments appropriate to the installer. MSI uninstall uses silent mode automatically; other installers depend on their recorded commands and arguments. Execution uses the device system account.

**Queue uninstall**

Queues the reviewed uninstall task. Follow its result in device action history. The task expires after seven days; application inventory changes only after a subsequent collection.


**Cancel (uninstall dialog)**

Closes without queuing the reviewed uninstall. Once Queue uninstall succeeds, closing the dialog does not cancel the queued task.


Uninstall matches the selected application's recorded version; an empty recorded version matches only an empty version. A per-user installation may require a separate software action in the appropriate user context rather than a system-account removal.

## Permissions

Device View is required to open the device. Device Uninstall Software is required to queue an uninstall.
