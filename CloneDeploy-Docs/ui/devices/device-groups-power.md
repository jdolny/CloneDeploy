# Power schedules

Assign recurring wake-up or shutdown actions to this group. Each occurrence uses current membership and excludes archived devices. Weekly schedule times use the server time zone.

<div class="ui-controls-spacer"></div>

**Schedule / Action / Enabled**

Identify the reusable schedule, whether it wakes or shuts down devices, and whether this assignment is enabled.

**Next occurrence / Last run / Last result**

Review the next planned occurrence and the most recent execution result.

Assignments are checked periodically, so a scheduled time does not guarantee immediate execution. Occurrences more than ten minutes late are skipped, and interrupted claimed occurrences are not retried. Within one check, overlapping groups receive the same action once; conflicting wake-up and shutdown actions are skipped for the affected device.

**Assign power schedule**

Opens an assignment dialog. A group may have more than one assignment.

**Action**

In the dialog, choose Wake up or Shut down. Shutdown uses a 30-second countdown, including when users are logged in; unsaved work may be lost. Offline or busy devices are skipped rather than receiving a delayed shutdown.

**Schedule**

Choose a reusable schedule. Existing schedules are shared; editing their timing can affect other consumers.

**Create schedule**

Creates a schedule from the assignment dialog.

**Enabled**

Controls whether the assignment will run. Disabling it does not cancel a command already dispatched.

**Save**

Saves the assignment for future occurrences. It runs with the current access of the administrator who last saved it. Loss of that access stops execution; deleting the account removes its assignments.

**Edit / Remove**

Change an assignment or remove it after confirmation.

**Refresh**

Reloads assignments and their last results.


**Cancel (assignment dialog)**

Discards unsaved assignment edits. A shared schedule already saved using Create schedule remains available even if you cancel the assignment.

## Permissions

Device Group View is required to open the group. Device Group Power Management is required to manage power schedules. Creating shared schedules requires the corresponding schedule permission.
