# Rules

Define exceptions to the global remote-access default. A rule matches any of its selected devices or device groups. The enabled matching rule with the lowest priority number wins; the global default applies if none match. Changes affect new sessions.

<div class="ui-controls-spacer"></div>

**Search rules**

Filter the saved rule list. Column filters and paging help browse the matching records.

**Create rule**

Opens the rule editor. Up to 100 rules can be saved.

**Rule name**

Enter a descriptive name, up to 100 characters, to identify the exception.

**Priority**

Set a unique number from 0 to 100,000. Lower numbers are considered first; creation suggests an unused priority.

**Enabled**

Controls whether the rule participates in matching. Disabling a rule does not end sessions already admitted under it.

**Access mode**

Choose Require approval, Allow unattended or Require approval when logged in. Approval requires the selected user to accept; conditional approval permits unattended access only when no user is logged in, while Allow unattended skips the prompt. These modes do not grant operator permissions.

**Filter device and group choices**

Narrow the choices in the target selectors.

**Groups / Devices**

Select the targets for the exception. Matching any selected device or group is sufficient. Review unavailable targets before saving so the exception covers the intended endpoints.

**Save rule**

Validates and saves the rule. Cancel discards the editor changes. Saved changes apply to future session requests and are audited.

**Edit / Delete**

Change an existing rule or remove it from the list. After removal, another matching rule or the global default determines consent for new requests.


**Device groups**

Select one or more device groups in the rule dialog, or use Devices for individual endpoints. Either selector can provide a match; the filter only narrows displayed choices.

## Permissions

Administrator access is required.
