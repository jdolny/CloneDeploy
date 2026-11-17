# Attachments

Store supporting files against the device record for later download.

<div class="ui-controls-spacer"></div>

**Name**

Select an attachment name to download the file.

**Attached by / Attached**

The author and attachment time, displayed in local time.

**Upload a file**

Select a file in the upload dialog and wait for both upload and attachment to complete before closing it.

**Paging and sorting**

Browse attachments and order them by the available columns.


**Choose files / Upload progress / Close**

The upload control transfers files in chunks. Wait for the server upload and the separate attachment step to succeed. If upload succeeded but attaching failed, use Retry attaching for that file rather than uploading another copy. Close is blocked while an upload or attachment is running; closing afterward leaves completed attachments in place.


**Upload / Cancel remaining uploads**

Choose or drop up to 10 nonempty files, each no larger than 10 GiB, into the upload dialog. Remove unwanted file chips before starting, then select Upload. Progress identifies the current file and transferred percentage. Cancel remaining uploads stops outstanding transfers; completed uploads or attachments are not rolled back. Wait for attachment to complete before using uploaded content. The dialog's Close action dismisses it after active work finishes.

## Permissions

Device View is required to open the device and download attachments. Device Edit is required to upload and attach files.
