# Files

Command, script, software and file actions can attach stored files. An attached file is reusable action content; changing it can affect future executions.

<div class="ui-controls-spacer"></div>

**Files and downloads**

Policy Edit is required to manage action files.

**Upload files**

Opens file upload. Wait for upload and attachment to complete before using the file.

**Add from URL / Edit**

Opens the external-file editor.

**Download URL**

HTTP or HTTPS address that the server downloads. The transfer continues after you leave the page.

**File name**

Stored name including extension. Suggested from the URL until you edit it.

**Expected SHA-256 (optional)**

Optional required checksum. A supplied checksum pins the file and excludes it from scheduled refresh.

**Save and download**

Saves the external file and starts a server-side download.

**Cancel / Close**

Closes the editor or upload dialog. Use Cancel download to stop an active external transfer.

**Refresh / Search**

Reloads or filters file status and source details.

**Download**

Retrieves the stored file when a version is available.

**Re-download**

Requests another transfer from the saved URL.

**Cancel download**

Requests cancellation of the current external transfer.

**Delete**

Removes the selected attached file after the removal flow. Check references such as the selected installer first.

**Status / progress / last checked / hash**

Use these to verify the transfer and content. A previous downloaded version is retained if replacement has not completed.

**URL file schedule**

A URL such as a vendor latest-release link can be checked automatically for changed content.

**Auto Download Files**

Shows automatic-download scheduling controls.

**Schedule**

Chooses a shared schedule. Empty disables automatic checks. Expected-checksum files are pinned and skipped.

**New schedule**

Creates a reusable schedule; see shared schedules.

**Save schedule**

Persists the action schedule. The task scheduler evaluates checks, every five minutes by default.


**Upload / Cancel remaining uploads**

Choose or drop up to 10 nonempty files, each no larger than 10 GiB, into the upload dialog. Remove unwanted file chips before starting, then select Upload. Progress identifies the current file and transferred percentage. Cancel remaining uploads stops outstanding transfers; completed uploads or attachments are not rolled back. Wait for attachment to complete before using uploaded content. The dialog's Close action dismisses it after active work finishes.


**Done (Manage files dialog)**

Closes the attachment manager. Uploads, URL downloads and scheduling changes have their own save/start actions; Done is not a commit or rollback for those operations.

## Permissions

Access requires Policy View permission.
