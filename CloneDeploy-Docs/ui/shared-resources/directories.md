# Directory Connections

Import directory objects from Active Directory over LDAPS or Microsoft Entra ID through read-only Graph requests. Each installation supports one LDAP and one Entra connection, with at most one full directory sync. The other connection may use Application access only to import identities and memberships for App User Group mappings. Disabled connections still reserve their provider and mode. Import does not create application administrator accounts. Configure Entra sync and optional SSO.

<div class="ui-controls-spacer"></div>

**Connections**

Open **Settings → Directory Connections**. Select a connection name to open its detail page.

Use **Add LDAP directory**, or **Add Entra directory** from its dropdown, to create a directory. The creation dialog asks for a name and optional description. New directories have sync disabled until their configuration is completed. One connection per provider may be saved, including disabled drafts. Only one can use Full directory sync. Select **Application access only** for the other: it imports users, groups and memberships for application permissions, without supplying managed devices or policy targets. Use **Entra SSO settings** in the actions dropdown to configure Microsoft authentication. Group-based sign-in needs an Entra membership sync and App User Group mappings.

**Configuration**

Name, description, connection settings and sync enablement. Entra includes application IDs, certificates and Microsoft sign-in settings.

**Users**

Synced identities, account details, object IDs and labeled SID types.

**Groups**

Imported directory groups and their identifiers.

**Devices**

Imported computer/device records. Unique, enabled Entra joined and hybrid-joined devices also create records in All Devices. Registered devices are excluded; normal enrollment approvals still apply.

**OUs (full LDAP sync only)**

Directory hierarchy, direct contents and OU policy assignments.

Use **Sync now** from the list or detail page to queue synchronization. Check progress in **Scheduled tasks and run results**, then refresh a data tab. The data tabs show the last successful sync; refreshing a tab does not start a sync.

**Delete directory** removes imported objects, memberships and directory policy references. Enrolled devices and inventory are retained. Entra application sign-in settings remain independent of directory sync.


**Name / Description (creation dialog)**

Set a required connection name up to 200 characters and optional description up to 2,000 characters. The action used to open the dialog determines LDAP or Entra.

**Portal / Application access only (users, groups and memberships)**

Select this for identity and application-access synchronization without device inventory or policy targeting. Leave cleared for the installation's full-sync connection. Disabled connections still reserve their provider and mode.

**Create / Cancel**

Create saves the directory connection as a disabled draft for configuration. Cancel closes without creating it.

## Permissions

Administrator access is required.
