# Options

Manage this account's appearance, language, and e-mail digests.

Open **Options** from the account menu to manage your own preferences. The `/account/options` shortcut opens the Options tab for the signed-in user. Administrators can also open **Application Users > user > Options** to manage another user's preferences.

<div class="ui-controls-spacer"></div>

**Appearance and language**

**Color Mode**

Select light, dark, or system appearance.

**Language**

Select an application language or use the browser language.

**Save Options**

Save appearance, language, and digest preferences together.

**E-mail digests**

Notifications use the email address on your application user account. There is no separate delivery address or verification code. Administrators manage account email addresses, and directory-linked accounts can receive email updates from their directory source.

Subscriptions are off by default. A valid account email is required to enable daily attention reports, weekly summaries, or both. Set a delivery time, timezone, weekly day, categories, and whether to skip emails with no attention items. The top-right **Save Options** button saves appearance, language, and email preferences together. Administrators can view and manage another user's appearance, digest preferences, and delivery history from **Application Users > user > Options**. Digest categories and delivered content follow the recipient's permissions.

Delivery uses the current account email, including later administrator or directory changes. Missing or invalid addresses prevent delivery; existing opt-in preferences are retained. Existing subscriptions continue using the account email after upgrade, replacing any former separate delivery address. The first delivery is the next chosen occurrence. Both digest types share the chosen delivery time.

Daily digests include selected pending enrollment/reset approvals and failed imaging, policy, action, and directory-sync jobs. Failures use a rolling 24-hour window. Weekly digests use a seven-day failure window and can add current fleet count, low disk space, reported disk warnings, stale check-ins, and stale inventory. They do not store historical trends. Each section includes a count, up to ten example names, and a link to the application.

Permissions are checked against the recipient's current account and directory access at delivery time. Approval notices require device-approval permission. Policy failures require policy-view and device-view permission. Weekly device warnings require device-view permission. Imaging, action, and directory-sync failures require administrator access. Locked or directory-disabled accounts are skipped. Unselected or unauthorized categories are omitted.

Device warnings use the same thresholds as Device Stats: at most 10 GiB or 10% free on a valid fixed volume, warning Windows disk status, or missing/older-than-30-day check-ins and inventory. Disk status is not detailed SMART telemetry. Failures appear only while the application retains their source records.

Select **Helpdesk tickets needing attention** to include two sections in either digest: open, unclaimed tickets you can access, and open tickets assigned to you whose latest message is from the requester. A reply from any staff member clears the second reminder until the requester replies again. Closed tickets are excluded. Ticket permissions and queue membership are rechecked at delivery.

Helpdesk sections show the current backlog regardless of age, ordered oldest waiting first, with up to ten tickets per section and a total count. Each entry includes its number, subject, requester, queue, waiting time, and direct link. Unclaimed waiting time starts at ticket creation; reply waiting time starts at the latest requester message. These tickets count toward **Skip emails when nothing needs attention**. The helpdesk option is off until you select and save it.

**Security activity**

Administrators can select **Security activity** in their email options. It is off by default and uses the existing daily (24-hour) or weekly (seven-day) delivery schedule. Administrator access is checked again when each digest is built; non-administrators cannot enable this category.

The security sections include:

- Five or more failed sign-ins for the same account and source IP during the reporting period.
- Account lockouts and attempts while an account is locked, grouped by account and source IP.
- Successful sign-ins preceded by at least five failures or locked attempts for the same account and source IP within 15 minutes. The lookback includes failures just before the reporting period.
- Account creation/deletion, account access and MFA setting saves, user/group permission and membership changes, directory access mappings, password changes, and authenticator enrollment/replacement through the application UI.
- Directory authentication and managed credential administration changes, plus certificate and administrator MFA-reset audit events.

Authentication recording covers main web password/MFA, imaging password/MFA, and Entra sign-ins completing at CloneDeploy. Entra failures that occur only at Microsoft are not available to this digest. Events begin recording after this update; historical sign-ins cannot be reconstructed. Source addresses are the connection addresses seen by the server, which may be a proxy or Site Service. The recorder does not trust arbitrary forwarded-IP headers.

Each authentication section lists up to ten account/source groups; change sections list up to twenty events, with full counts. Routine successful logins and isolated failures do not generate attention items. These are activity notices, not confirmed intrusion findings. New-IP/geolocation anomaly detection is not included.

Security records store timestamps, event types, actor/account names, source addresses and bounded target summaries. Passwords, MFA codes, tokens and raw configuration/audit payloads are not included in the digest. Records have no automatic deletion policy. Recording failures are logged without changing an authentication result or an already committed setting change.

**Scheduling and delivery status**

The existing **Task Scheduler** automatically receives an **Email digest delivery** task and a shared **Email digest checks** interval schedule, initially every five minutes. The task respects user-local delivery times. It is safe to run manually: already completed occurrences are not resent. Administrators can disable it or change its check interval through the existing scheduler and Shared Resources schedules. Restarting preserves those choices. Long-running scheduler tasks can delay email processing.

Only the latest due daily and weekly occurrence is considered after downtime. DST spring gaps skip a nonexistent local time; fall overlaps use the first occurrence once. Repeated checks and concurrent workers are protected by database locks and unique delivery records.

Your options page shows the ten most recent delivery records:

- **Sent**: SMTP accepted the message; mailbox delivery is not confirmed.
- **Failed**: SMTP explicitly rejected it. The current occurrence may be retried up to three times, at least 30 minutes apart.
- **Uncertain**: SMTP acceptance could not be established, or a send was interrupted. Automatic retry is suppressed to avoid duplicate digests.
- **Skipped**: No attention items with skip-empty selected, or the account is unavailable.

Task execution failures are also visible in Task Scheduler. Delivery history stores status rather than email bodies or credentials.

**Password changes**

Use the username menu in the top right to choose **Change Password** for a local account or **Change Imager Password** for an Entra account. Enter the current password, new password, and confirmation. Entra users who have no Imager password can set their first one without a current password. Sign in again after saving. AD passwords are managed in Active Directory, so this menu option is hidden for AD accounts.
