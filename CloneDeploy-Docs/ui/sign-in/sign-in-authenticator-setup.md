# Sign in — Authenticator setup

Local and AD administrator web password sign-ins require CloneDeploy MFA. Require imaging MFA is a separate per-account setting that can be disabled for administrators as well as other users. Imager password lockout still applies when imaging MFA is disabled. Microsoft (Entra) SSO does not require a second CloneDeploy MFA step; Microsoft manages authentication for those sessions. Entra-linked accounts using an Imager password follow the imaging MFA setting. Existing accounts retain their previous imaging MFA requirement until an administrator changes it.

Register the displayed QR code or manual setup key in your authenticator, then verify
a generated code. Opening this page does not change your account, its security stamp,
or its existing authenticator. Pending setup stays in the browser session and expires
after ten minutes. Treat the key as a secret.

<div class="ui-controls-spacer"></div>

**Shared key**

Read-only enrollment secret to add to an authenticator app.

**Code / Verify**

Enter a current code from the new authenticator. Successful verification saves the key and enables MFA. Sign in again afterward.

**Set up a replacement**

Verify a code from the current authenticator first. Its key stays active until the replacement is successfully verified. Leaving the page does not replace it.

**Sign out**

Ends this browser session.

**Lost authenticator: server administrator recovery**

Recovery codes are not issued. A trusted administrator with access to the PostgreSQL
database can reset enrollment for one account. Stop CloneDeploy first (including
other server processes using this database), and take a database backup. This also
ends existing browser circuits and in-memory administrator sessions.

Connect to the application's database with your PostgreSQL administration tool.
First find and confirm the exact account ID:

```sql
SELECT "Id", "UserName", "Membership" FROM "AppUsers" ORDER BY "Id";
```

Run the following transaction, replacing `123` with that account's ID. The update
must return exactly the intended account. If it does not, run `ROLLBACK` rather than
`COMMIT`. No password or directory mapping is changed.

```sql
BEGIN;
UPDATE "AppUsers"
SET "TwoFactorEnabled" = FALSE,
    "EnableMfa" = TRUE,
    "SecurityStamp" = gen_random_uuid()::text,
    "ConcurrencyStamp" = gen_random_uuid()::text,
    "AccessFailedCount" = 0,
    "LockoutEnd" = NULL
WHERE "Id" = 123
RETURNING "Id", "UserName";

DELETE FROM "AppUserTokens"
WHERE "UserId" = 123
  AND "LoginProvider" = '[AspNetUserStore]'
  AND "Name" IN ('AuthenticatorKey', 'RecoveryCodes');

-- After confirming the returned account:
COMMIT;
```

Start CloneDeploy again. Sign in normally and enroll a new authenticator. The account
can access only enrollment until verification succeeds. The changed security stamp
invalidates previous account sessions and remembered-browser MFA credentials.
Keep mandatory MFA enabled; resetting enrollment is sufficient to recover access.

**Group requirements**

App User Groups have a User/Admin membership dropdown and separate Require web MFA and Require imaging MFA controls. Admin groups grant administrator access to manually added members and members matched through directory mappings. Administrator web password sign-ins always require MFA; imaging MFA remains optional.

Requirements combine across the user's settings and all applicable groups: if any requires MFA, it is required. Unchecking a requirement on one group cannot override another group's requirement. Removing a group membership removes its inherited access and requirements. Directory accounts still need an allowed directory mapping; adding them manually cannot bypass revoked directory access. Microsoft/Entra sign-ins remain exempt from CloneDeploy MFA.

**Administrator recovery**

An administrator can open another user under Users, then choose Reset MFA from the actions dropdown. After confirmation, CloneDeploy clears that user's authenticator and recovery tokens, changes the session security stamp, and records the reset in the audit log. User and group MFA requirements remain in place. The user must enroll a new authenticator through web sign-in when MFA is required. Microsoft/Entra MFA is managed by Microsoft and is not reset by this action. Account lockouts are unchanged.

This recovery action is unavailable for the administrator's own account. If no other administrator can sign in, use the database recovery procedure above.
