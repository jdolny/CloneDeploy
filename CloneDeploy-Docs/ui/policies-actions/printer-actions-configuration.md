# Configuration

Add or remove a Windows printer connection and optionally adjust its default state.

<div class="ui-controls-spacer"></div>

**Operation**

Install, Remove, or No change. No change can be used to set the default without adding a connection.

**Printer network path**

UNC path to the shared printer, for example `\\printserver\OfficePrinter`.

**Set as default printer**

Sets and verifies the default for the policy user. Requires login or requesting-user execution. System-context actions with this option fail before making changes. Cannot be combined with Remove.

**Restart print spooler**

Restarts the Windows spooler as part of the operation; this can interrupt printing.

**Wait for printer enumeration**

Retries verification for up to about two minutes. User execution checks that user's printer connections; system execution checks computer-wide registration. Without this option, verification occurs immediately.

**Save changes**

Saves printer behavior. Use the appropriate machine/user execution context for the intended printer scope.

Printer scope follows the policy trigger and execution context. Login policies and self-service policies configured to run as the requesting user manage that user's connection. System execution manages computer-wide registration, which Windows applies when users sign in. Successful registration does not confirm that every user has installed the printer or its driver.

Windows obtains required drivers from the print server using the existing execution identity. CloneDeploy does not prompt for elevation, switch credentials, stage separate driver packages, or weaken Point and Print restrictions. If Windows blocks driver installation or the print server is unavailable, the action reports failure. A computer-wide registration can succeed before a later user driver installation fails.

Restart print spooler is optional and requires system execution. It can interrupt printing. Printer actions run in an isolated process so the action timeout and cancellation also apply to blocking Windows printer calls. Existing connections and already-absent removals are verified without repeating the change.

## Permissions

Access requires Policy View permission.
