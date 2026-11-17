# Configuration

Control availability, offline naming, and domain-join OU selection.

<div class="ui-controls-spacer"></div>

**Enabled**

Allows use of the profile.

**Show in WinPE deployment profile selection**

Makes the profile visible to the WinPE operator.

**Set computer name to device name**

Sets offline Windows naming and updates existing answer files during deployment.

**OU naming rules**

Optional domain-scoped rule set evaluated using the final Windows name.

**Prompt for OU at end of imaging**

Never, if no rule matches, or always for a new AD computer account. Only runs when `Windows\Panther\unattend.xml` contains a domain join in the specialize pass.

**Save Configuration**

Commits the options.

Before applying OU rules or prompting, the server checks the live target AD domain for the final computer name using the directory connection's managed credential over LDAPS. If an account exists, its current location is retained and no OU selector appears. This does not bypass Windows account-reuse permissions. If no account exists, the configured OU rules and prompt settings apply.

The lookup searches the whole domain, independently of inventory import OU filters. The bind account needs read access across that domain. An enabled, synced AD connection is required; if multiple connections serve the domain, select an OU naming rule set to identify the connection. A lookup error stops finalization rather than assuming the account does not exist. Directory credentials stay on the server.

## Permissions

Access requires Deployment Profile View permission.
