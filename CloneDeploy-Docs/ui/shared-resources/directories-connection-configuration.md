# Configuration

Open a saved LDAP connection and select **Configuration**. Use a read-only AD account with access to the selected domain scope. LDAPS verifies the server certificate.

<div class="ui-controls-spacer"></div>

**Directory (tenant) ID / Application (client) ID**

For an Entra connection, identify its tenant and app registration. Its certificate controls prepare and activate the public certificate used for directory access. LDAP host and bind-account fields apply to LDAP connections.

**Application access only**

Imports users, groups and memberships for application access without importing managed-device and policy-target data. Only one connection may use full directory synchronization.

**Description**

Optional notes about the directory.

**Connection name**

Label displayed in directory selectors.

**Domain controller hostname / LDAPS port**

LDAPS endpoint; its certificate must cover the supplied hostname and be trusted by the server.

**Search base DNs**

Optional one-per-line distinguished names in the same domain. Empty imports the whole domain; overlapping results are combined.

**Read-only AD credential / New credential**

Select or create the managed account used for directory reads.

**Enable this connection**

Allows synchronization for this definition.

**LDAP page size**

Number of objects requested per page of directory results.

**Sync timeout (seconds)**

Maximum allowed synchronization duration.

**Maximum objects**

Bounds the imported result size.

**Save**

Save connection changes. Configure automatic synchronization in Task Scheduler.

## Permissions

Administrator access is required.
