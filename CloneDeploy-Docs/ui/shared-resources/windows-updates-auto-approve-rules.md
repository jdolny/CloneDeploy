# Auto-Approve Rules

Rules evaluate inventoried revisions on a schedule, normally every five minutes. Policies/actions still control installation.

<div class="ui-controls-spacer"></div>

**New Rule / edit**

Defines name, enabled state and criteria.

**Severities**

Selected severities must match; missing severity never matches. Empty allows any, but select a classification then.

**Classifications**

Empty allows any. When both criteria are selected, both must match.

**Delay after first discovery (days)**

Waits from discovery of this revision. Zero means next rule check; new revisions start a new delay.

**Save Rule**

Saves matching/delay settings.

**Disable / delete**

Stops future approvals; does not revoke existing ones.

**Refresh rules / Search**

Reloads or filters rules.

**Task scheduler**

Changes evaluation schedule or runs it now.


**Name / Enabled (rule dialog)**

Name is required, up to 128 characters. Enabled controls whether future evaluation uses the rule.

**Classifications from inventory**

Choose the classifications represented in collected update data. This is the classification selector described above.

**Delay after first discovery of this revision (days)**

Set 0–365 days. The delay is specific to this revision's first discovery, rather than the original update's publication date.

**Cancel**

Closes the rule dialog without saving changed criteria.

## Permissions

Access requires Report Windows Updates permission.
