# Software

Review applications known from device inventory, grouped by name and version. Counts reflect distinct devices rather than installation-record counts. Entries with no remaining installations may remain with a zero count.

<div class="ui-controls-spacer"></div>

**Search software**

Filter by application name or version.

**Name / Version / Devices**

Identify the application and the number of devices reporting that version.

**View Devices**

Opens the installed-device list, including archived devices and the last inventory time. Each device appears once. Select a device name to open its details.

**Uninstall from all devices**

Opens the application-removal review. Verify the target devices and version before queuing uninstall tasks; eligibility and permissions are checked separately.

**Paging**

Browse catalog entries and matching devices.


**Search devices / Close (installed-device dialog)**

Filters the loaded matching devices. Close dismisses the list without changing inventory or installations.

**Select eligible / Clear (uninstall dialog)**

Select eligible chooses devices with recorded uninstall commands that are eligible for removal; Clear removes the pending selection. Individual checkboxes let you review targets. Archived or otherwise ineligible devices cannot be selected.

**Silent uninstall arguments (optional)**

Supply the installer's unattended-removal switches when needed. MSI commands use silent uninstall automatically. Review arguments and the exact application/version before queuing; commands run as the endpoint system account.

**Review uninstall / Queue uninstall**

Review resolves the selected devices and removal command before submission. Changing selection or arguments requires another review. Queue uninstall creates tasks for the reviewed devices; it does not establish that the application was removed. Track execution results and collect new inventory afterward.

**Cancel / Close (uninstall dialog)**

Cancel leaves without queuing work. After submission, Close dismisses the result and does not cancel queued tasks.

## Permissions

Software report permission is required. Viewing devices and queuing uninstall work require their corresponding device permissions.
