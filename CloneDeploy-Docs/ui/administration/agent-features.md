# Features

Open **Agent Settings → Features** to manage agent sign-in preferences and enable Help desk, Self service and WinGet integration globally. Only administrators can save these settings. Help desk, Self service and WinGet default to off when no configuration exists.

<div class="ui-controls-spacer"></div>

**Prompt for Mac Sign-In**

Show a reminder popup when a Mac user is not signed in to their work account. Enabled by default. Disabling closes an open reminder and prevents further reminder popups after the agent receives the setting. The portal's sign-in banner and manual sign-in remain available. Windows sign-in behavior is unchanged.

**Mac portal sign-in duration (days)**

Set how long new work-account sessions last, from 1 to 365 days. The default is 90 days. Existing sessions survive server restarts.

**Help desk**

Enabling Help makes the configured Help tab available in the agent portal and allows ticket activity notifications. Users still need a recognized, synchronized Entra or LDAP identity. Disabling Help blocks end-user ticket operations and notifications. Existing tickets remain available to authorized technicians in the web UI.

**Self service**

Enabling self service lets users see and run policies offered to their audience. Individual policy settings and permissions continue to apply. Disabling hides the catalog and prevents new self-service runs. Scheduled and login policy execution continues. Existing policies and audience assignments are preserved.

**WinGet integration**

Enable WinGet to allow catalog synchronization and WinGet actions. An administrator must acknowledge unattended deployments when enabling or re-enabling the integration. The server records the administrator, time, acknowledgment text and feature changes in its audit log.

CloneDeploy passes WinGet options that automatically accept applicable source and package agreements. Your organization remains responsible for reviewing those agreements and obtaining required software licenses; enabling this feature does not grant application licenses. The settings page links to Microsoft’s agreement documentation and Store terms.

Disabling blocks catalog imports and new WinGet execution, including scheduled, login, self-service and direct deployment actions. Configured actions and imported catalog entries are retained. Registry-based software removal remains available.

**Applying changes**

Save changes to apply the server controls. Connected agents receive display settings through their existing heartbeat, normally within about 20 seconds including the local UI refresh. Offline agents receive them after reconnecting. WinGet execution additionally checks the server before starting and will not start if availability cannot be verified. Work already running can finish.

Update the server, agent service and agent UI together to use these controls. No database schema migration is needed.

## Permissions

Administrator access is required.
