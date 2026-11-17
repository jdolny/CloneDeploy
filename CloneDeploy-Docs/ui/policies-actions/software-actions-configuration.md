# Configuration

Install, repair, update or uninstall software using attached installers, WinGet or the device’s recorded uninstall data.

<div class="ui-controls-spacer"></div>

**Operation (install/uninstall)**

Choose installation behavior or removal of an inventoried application.

**Installed application**

Uninstall matches the exact selected application name using local uninstall information. An absent application succeeds without changes.

**Uninstall all versions**

When enabled, removes matching names regardless of version; otherwise restricts removal to the selected version.

**Silent arguments**

For uninstallers without a recorded quiet command, supply vendor-documented silent switches. Otherwise unattended removal may be unavailable.

**Installer type**

Select MSI, EXE or WinGet for Windows, or PKG for macOS. PKG uses an attached .pkg installer and does not support uninstall, custom arguments or a managed Run As credential.

**Installer file**

One primary attached installer is executed; other attachments are supporting files. Add/manage files opens attachment management; Refresh files updates readiness.

**MSI operation**

Chooses the MSI operation; successful return codes include 0 and 3010 (restart required).

**WinGet operation**

Install/update a package, update managed packages, or update all packages recognized by WinGet. Update-all can affect software outside assigned policies.

**Package ID / Browse packages**

Enter an ID such as `Google.Chrome` or choose from the imported catalog. Use package applies the choice to the editor; save configuration afterward.

**Package version / Install latest version**

Pin the selected version or let WinGet resolve latest at execution time.

**Eligible for managed updates**

Marks this package action as eligible when assigned through a published policy. Managed-update operations update installed eligible packages; they do not install missing applications.

**Installer argument override / Additional arguments**

Use the documented installer options. Override replaces the relevant WinGet installer argument selection; additional arguments are for the attached installer workflow.

**Use server download queue**

Reserves shared server download capacity while the WinGet command runs.

**Arguments / Timeout / Success exit codes**

Sets process parameters, timeout in minutes (zero uses the 60-minute agent default), and comma-separated integer success codes for the selected installer workflow.

**Run As**

Optional managed execution credential. Per-user software must be removed or updated in the correct user context.

**Capture standard output / error**

Records selected process output in results. Do not emit secrets.

**Save changes**

Saves the selected workflow. Missing, pending or failed attachments must be resolved before execution.


**Search packages / Select (WinGet package dialog)**

Search the imported catalog, then select a package to load its versions. Install latest version lets WinGet resolve the version at execution; clear it to choose a Package version. Use package applies the package ID and version choice to the action editor, not to endpoints. Save changes on the action afterward. Cancel leaves the editor's previous package selection in place.

## Permissions

Access requires Policy View permission.
