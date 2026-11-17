# WinPE Boot Environments

Boot environments define the WinPE settings used to start devices for imaging. The builder prepares their WinPE media automatically, with PowerShell included for x64 and ARM64. Each boot environment has one current boot image. A successful build becomes available automatically after storage verification and boot-file preparation. Rebuilding replaces that image; the previous image stays available until the rebuild succeeds. Create another boot environment to keep a separate version.

<div class="ui-controls-spacer"></div>

**Create boot environment**

Opens a new boot environment definition.

**Search / paging**

Finds boot environments by name or description.

**Name**

Opens General, Configuration, Model matching and Drivers. General includes build results and downloads.

**Delete**

Requests deletion subject to in-use restrictions.

**Boot image / Default / Native PXE**

Shows build readiness and selection/boot behavior.


**Name / Description (creation dialog)**

Enter a required name for the boot environment, up to 200 characters, and an optional description up to 2,000 characters.

**Create / Cancel**

Create saves the boot environment and opens its configuration. It does not start a build, preparation or deployment. Cancel closes without creating it. Creation requires administrator access.


The builder caches verified clean WinPE media and adds each environment's drivers and Imager bootstrap to a separate working copy. Missing or corrupt cached media is prepared again automatically. Building a boot environment handles this preparation without a separate base-image selection.

## Permissions

Administrator access is required.
