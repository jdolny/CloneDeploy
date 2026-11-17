# Configuration

Store a script used for execution, a condition, or custom inventory. A script has one purpose; selecting both condition and inventory is not supported.

<div class="ui-controls-spacer"></div>

**Script type**

Choose Windows PowerShell, PowerShell 7, or Batch for Windows; Bash or Zsh for macOS. PowerShell 7 requires PowerShell 7 to be installed on that device.

**Purpose**

Choose ordinary execution, condition or inventory. Conditions influence whether work proceeds; inventory scripts contribute data only when assigned to inventory collection.

**Script contents**

Edit the script body in the code editor; save the action configuration to retain it.

**Working directory / Arguments**

Controls the script’s starting directory and supplied parameters on the device.

**Timeout / Success exit codes**

Timeout is in minutes; zero uses the agent default of 60 minutes. Success codes are comma-separated integers, for example `0,3010`.

**Run As**

Select an optional managed credential where the execution context allows it.

**Log standard output / Log standard error**

Records selected output streams in action results. Do not write secrets to them.

**Save changes**

Saves the edited action without executing it.

## Permissions

Access requires Policy View permission.
