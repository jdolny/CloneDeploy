# Enrollment Rules

Agents present the shared enrollment key during initial registration, then use their own certificates after enrollment.

<div class="ui-controls-spacer"></div>

**Enrollment mode**

Choose Allow new device records or Require an existing device record. In existing-record mode, unknown devices wait for a record or an explicit pre-create-and-approve operation.

**Approval for existing devices**

Shown in existing-record mode. Choose whether matching records require approval or may enroll automatically.

**Enrollment approval**

Shown when new device records are allowed. Choose whether valid-key enrollment proceeds automatically or waits for approval.

**Auto approve reset requests**

Controls whether eligible certificate-reset requests may be approved automatically. Leave disabled when reset requests must be reviewed by an administrator.

**Expire inactive pending requests after (days)**

Set how long inactive pending requests are retained, from 1 to 3,650 days. The initial value is 30 days.

**Save changes**

Saves the admission and retention settings. Previously denied requests remain blocked until their recorded decision is changed or cleared.

## Permissions

Administrator access is required.
