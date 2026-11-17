# Configuration

Run a command on a managed device. Supply an executable and its arguments; test quoting and exit-code handling before assigning broadly.

<div class="ui-controls-spacer"></div>

**Command**

Executable or command to launch on the target device.

**Working directory**

Directory in which the process starts; it must exist on the target.

**Arguments**

Arguments passed to the command. Use the command’s documented unattended syntax.

**Timeout**

Maximum process execution time in minutes. Zero uses the agent default of 60 minutes.

**Success exit codes**

Comma-separated integer exit codes that count as success rather than failure, for example `0,3010`.

**Run As**

Optional managed credential for execution; otherwise the policy/deployment execution context applies.

**Log standard output / Log standard error**

Includes the selected process streams in results. Avoid printing credentials or secrets.

**Save changes**

Saves the edited action without executing it.

## Permissions

Access requires Policy View permission.
