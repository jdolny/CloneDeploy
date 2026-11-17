# Versions

Uploads become independent versions; select the intended one before deployment.

For FFU images, **Minimum disk size** shows the required destination capacity reported by DISM during capture inspection. The Imager calculates it from the minimum sector count and sector size after the upload is flushed, before publishing the version. This is separate from the image file size. The displayed GiB value rounds up; hover over it for the exact byte count.

Older versions and captures from older Imagers show **Unknown**. WIM versions do not display this field. This records capacity only; it does not resize partitions or change approval or deployment behavior.

<div class="ui-controls-spacer"></div>

**Refresh / Search versions**

Reloads or filters versions.

**Edit details / Save details**

Changes version name/description, not disk content.

**Set active**

Selects the version used by new deployments.

**Delete**

Requests removal of a version, subject to protection/use restrictions.

**No versions**

Complete a capture/upload before expecting deployable content.


**Name / Description (Edit version dialog)**

Set a required version name up to 200 characters and optional description up to 2,000 characters. These are labels for the existing captured version; changing them does not alter its disk contents or approval.

**Save details / Cancel**

Saves the version labels, or closes without changes. Editing requires Image Edit permission.

## Permissions

Access requires Image View permission.
