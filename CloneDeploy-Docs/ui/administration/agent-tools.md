# Agent tools

Restrict the networks allowed to use direct agent-tools sign-in. Account authentication and permissions still apply.

<div class="ui-controls-spacer"></div>

**Allowed client networks**

Enter IPv4 or IPv6 CIDRs separated by commas or newlines. Both the browser and agent must connect from an allowed network. Use /32 for one IPv4 address or /128 for one IPv6 address; unrestricted /0 networks are rejected. An empty list blocks sign-in.

**Save Changes**

Saves the networks. Restart CloneDeploy-Server to apply the change. Editing requires installed-server configuration. Site Service access uses its own saved network settings.

## Permissions

Administrator access is required.
