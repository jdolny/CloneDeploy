# Global configuration

Set the default user-consent requirement for new remote-viewing sessions. It applies when no enabled device or group rule matches. Changing the default does not change a session already in progress.

<div class="ui-controls-spacer"></div>

**Global default**

Choose the access mode:

- **Require approval:** the user in the selected session must explicitly approve access. Access is denied if no user can approve it. This is the initial default.
- **Allow unattended:** permits access without a user approval prompt. Operator permissions still apply.
- **Require approval when logged in:** requires approval when a user is logged in, including locked, idle and disconnected Windows sessions. Unattended access is permitted only when no user is logged in.

Approval requests expire after 60 seconds. An unavailable user or portal cannot grant approval. Approved access belongs to the selected logon session, rather than merely the username. Windows console and RDP sessions are evaluated separately.

**Save changes**

Saves the selected default for subsequent session requests. Device and group rules continue to take precedence. Access decisions, session endings and configuration changes are audited.

Agents and portals must support the configured consent controls. Older agents that cannot enforce them are blocked from remote viewing.

## Permissions

Administrator access is required.
