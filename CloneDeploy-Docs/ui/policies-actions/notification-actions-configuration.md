# Configuration

Display a notification through the agent portal/tray. Delivery and user acknowledgment are separate results.

<div class="ui-controls-spacer"></div>

**Title**

Heading shown to the user.

**Notification**

Up to 2,000 characters. Supports bold text and clickable HTTP/HTTPS URLs. User-context policies target only that user.

**Popup timeout (seconds)**

How long the popup stays visible; zero uses 60 seconds. Dismissed notifications remain in portal history.

**Position**

Where the popup appears on the desktop.

**Require acknowledgment**

Each synced directory user acknowledges once per notice across devices. Timeout or Later hides the popup; reminders continue at login until acknowledged or expired. Local or unmatched accounts remain device-specific.

**Notice expiration date**

Required notices stop prompting at the end of the selected UTC date, including on offline devices. Defaults to seven days. History is retained.

**Issue as new notice**

Prepares a new notice identity and expiration. Save configuration, then republish policies to require a fresh acknowledgement.

**Branding**

Choose organization branding or the available default style. Organization branding is configured under Agent Settings → Branding.

**Save changes**

Saves the notification; use deployment or an active policy to deliver it.

Ordinary notifications notify currently connected portal users and are not replayed at later logins. Required notifications wait for a user session and remain pending separately for each eligible user.

Agents save acknowledgements locally immediately and synchronize with the server. Before presenting a pending notice, the agent makes a brief server check; if unavailable, it uses local state. Another offline device can still prompt until it synchronizes. Local acknowledgement is never undone by a stale server response.

Repeated policy runs keep the same notice identity and absolute expiration. A deliberately changed expiration takes effect when the updated notice is delivered again; offline devices retain their last received deadline. Each new action deployment creates a separate notice shared across its targeted devices. Editing wording or republishing alone does not reset acknowledgements; use **Issue as new notice** when a fresh acknowledgement is intended. Action deployments remain device-targeted; login policies can restrict users with an audience.

## Permissions

Access requires Policy View permission.
