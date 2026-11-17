# Installer & Updates

Prepare and download installers for the endpoint platform. Enrollment bundles contain installation secrets and should be protected.

<div class="ui-controls-spacer"></div>

**Prepare agent release**

Prepares the available Windows x64, Windows ARM64 and macOS Apple silicon releases. Agents with update support receive compatible prepared releases through their normal update process.

**Download Windows x64 / Windows ARM64 deployment ZIP**

Downloads the signed MSI with enrollment.json. Extract both into the same local folder before installation. Upgrades preserve existing enrollment unless an explicit enrollment file is supplied.

**Download macOS Apple silicon installer**

Downloads the prepared macOS installation package. Use it for Apple silicon endpoints rather than a Windows MSI.

**Current prepared version / Readiness**

Review the prepared release and package readiness for each platform. Address missing or invalid release inputs before preparing or downloading.

## Permissions

Administrator access is required.
