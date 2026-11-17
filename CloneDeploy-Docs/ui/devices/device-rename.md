# Rename a Device

Change the device record name and, when the agent is connected, request the corresponding Windows computer rename.

<div class="ui-controls-spacer"></div>

**New computer name**

Enter the intended name, up to 15 characters. Server validation can reject an invalid computer name. The current saved name is initially populated.

**Domain credential (for domain-joined devices)**

Optionally choose a managed credential authorized to rename the domain computer account. This does not rejoin the domain.

**Rename**

If connected, requests the Windows rename as well as the record update. Otherwise only the saved record name changes. Windows requires a restart afterward; this action does not restart it automatically. If completion is unconfirmed, inspect the device before retrying.

**Cancel**

Closes without requesting a rename.

## Permissions

Device Edit permission is required. Loading and using rename credentials is restricted by the server.
