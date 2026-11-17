# General

Name a boot environment. Builds include x64 and ARM64 media with PowerShell.

<div class="ui-controls-spacer"></div>

**Name / Description**

Identifying labels.

**Save changes**

Stores settings without starting a build.

Build results and downloads appear in General. Use Build or Rebuild in the actions dropdown beside Save Changes to build saved settings. Save changes in the tab bar saves only the current tab for an existing boot environment.

You can create a boot environment with only a name and save each tab while setup is incomplete. Building requires a configured builder, imaging storage, public application URL and ready selected drivers. WinPE media is prepared automatically.


**Build / Rebuild**

Uses the saved boot environment settings. Open the actions dropdown beside Save Changes to start it. Building requires a ready builder, imaging storage, public application URL and prepared selected drivers. The previous boot image remains available until the replacement completes storage verification and boot-file preparation.

**x64 ISO / ARM64 ISO**

Downloads the current bootable ISO for the selected architecture from the build results in General. PXE content is prepared automatically; the agent's bootable USB tool uses the authenticated ISO download.

**Cancel build / Retry preparation**

Cancel build stops queued or running work. Retry preparation retries a failed storage transfer or boot-file preparation. A failed replacement does not remove the previous ready boot image.

## Permissions

Administrator access is required.
