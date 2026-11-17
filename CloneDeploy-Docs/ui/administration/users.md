# Application users

Manage accounts that sign in to the web application and Imager. These accounts are separate from directory identities and enrolled agents.

<div class="ui-controls-spacer"></div>

**Search and create**

Search matches username or email. Open a name to view its tabs. Permission-dependent buttons are available only to authorized administrators.

**New user**

Opens the creation dialog. Supply username, email, membership and initial password; select lockout and MFA requirements, then Create.

**Search and column filters**

Narrow the displayed accounts. Paging changes which rows are visible, not account access.

**Delete / Delete selected**

Removes the selected accounts after confirmation. The app protects against removal of the last administrator.

**Cancel**

Closes the creation dialog without creating an account.

**General**

Update identity and security requirements. Save General commits this tab; entering a new password resets it, while an empty password leaves it unchanged.

**Options**

Manage this account's appearance, language, and e-mail digests.

**Permissions**

Assign the operations a regular user may perform. Administrators already have all permissions. Group grants supplement direct grants; they are not denials.

**Audit**

Review changes associated with the account. The view is limited to the most recent 200 entries.

**Search history / column filters**

Filter the loaded entries; this does not search older records beyond the displayed history limit.


**Username / Email / Membership (New user dialog)**

Enter the required username and optional email, then choose Admin or User membership. Admin accounts receive full access; User accounts use their assigned grants.

**Password**

Set the initial password. It must satisfy the server's password policy; the default minimum is 15 characters.

**Lockout Enabled**

Controls whether failed sign-ins can lock this account under the configured lockout policy.

**Require web MFA / Require imaging MFA**

Choose the account's MFA requirements. Web MFA is mandatory for administrators and cannot be cleared while Admin membership is selected. Imaging MFA is a separate requirement.

**Create**

Validates and creates the account. A validation failure leaves the dialog open for correction; Cancel discards it.

## Permissions

Administrator access is required.
