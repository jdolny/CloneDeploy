# Directory groups

Map synchronized directory groups to this application user group. Mappings use directory source and object IDs rather than display names. Directory synchronization and Microsoft sign-in configuration are maintained separately.

<div class="ui-controls-spacer"></div>

**Search**

Find imported directory groups.

**Group selection**

Add the directory groups whose members should receive this application group's permissions, or remove an existing mapping.

**Save**

Saves the mappings. Group permissions and full administrator access are maintained on this application group's Permissions and General tabs. Existing Entra app-role assignments do not grant access.

**Search AD or Entra groups / Add selected groups (directory-group picker)**

Search imported groups, select their rows and choose Add selected groups to return pending mappings to the application group. At least one selection is required. An application group can have at most 100 directory-group mappings, including existing mappings. Cancel discards picker choices. Save on the owning Directory groups tab commits the mappings; adding a mapping does not synchronize the directory or create an application account.

## Permissions

Administrator access is required.
