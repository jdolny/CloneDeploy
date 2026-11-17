# Configuration

New boot environments/builds use Public application URL. Existing builds retain their captured URL.

<div class="ui-controls-spacer"></div>

**Keyboard layout**

Sets keyboard input in the x64 and ARM64 WinPE images, including Imager login. Defaults to English (US). Save and rebuild the boot environment to apply changes.

**Network boot method**

iPXE uses the managed iPXE flow. Native PXE stages Windows boot files after a successful build. Known devices use their selected boot environment; first-boot devices use the ready native default for their architecture, without inventory or model rules. Without a compatible native default, they use iPXE.

**Use Windows UEFI 2023 CA boot media**

Needs compatible ADK and firmware trust. Disable for devices requiring Windows UEFI 2011 CA media.

**Save changes**

Commits settings. Switching a ready boot environment to native also stages its current boot files.

## Permissions

Administrator access is required.
