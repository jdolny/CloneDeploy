# Configuration

Join a Windows computer to the specified domain. An already joined computer in the same domain is left unchanged, including its OU; joining a different domain is rejected.

<div class="ui-controls-spacer"></div>

**Domain DNS name**

For example `school.example.org`; use the domain’s DNS name.

**OU selection**

Domain default / existing account location, Specific OU, or Naming rules.

**Organizational unit**

Required for Specific OU; a distinguished name such as `OU=Workstations,DC=school,DC=example,DC=org`.

**OU naming rule set**

Select an enabled rule set for the join domain. The server evaluates current rules and fallback using the agent's actual Windows computer name at execution time.

**Browse directory OUs**

Selects an OU from synchronized directory information.

**Domain-join credential**

Managed account with permission to join computers in the target OU.

**Save changes**

Saves the join settings. Joining requires a restart afterward; use policy completion behavior or restart separately for standalone actions.

Naming rules require an updated agent. Restart after renaming the computer before running the join action. Failed resolution prevents the join. Rule selection and resolved OU are included in the successful action result; credentials are not logged. Rules are shared configuration, so editing them affects subsequent executions even for previously activated policies. Rule sets referenced by saved actions cannot be deleted. See OU Naming Rules.

## Permissions

Access requires Policy View permission.
