# Policies

Policies combine targets, triggers, conditions and an ordered action sequence. Saving an inactive policy does not run it; activation publishes a saved version for the next eligible trigger.

Create a policy with New Policy, a name and description. Open a name to edit its tabs.

<div class="ui-controls-spacer"></div>

**Search / Tags / Match / Include archived**

Filters the policy catalog without changing targeting.

**Activate**

Validates and publishes saved settings/actions. Review the publication before confirming.

**Deactivate**

Stops admitting new runs and unlocks configuration editing. Target assignments remain editable while active.

**Re-Run**

Requests another eligible run; it is not a connected-device command.

**Archive / Restore / Delete**

Archive retains the record out of use; restore makes it available again; deletion requests permanent removal after confirmation.


**Name / Description / Create / Cancel (New Policy dialog)**

Supply the required name and optional description. Create saves an inactive policy and opens its configuration; it does not run or activate it. Cancel creates nothing. Creating requires Policy Edit permission.

**Activation review / View targets / View audience**

The activation dialog checks saved settings and displays trigger, frequency, Run As, ordered actions, built-in functions, device targets and audience. Matching-device and audience buttons open read-only previews when permitted. Correct any validation errors outside the dialog before reopening it.

**Activate saved policy / Deactivate / Cancel**

Activate saved policy is available only after a successful review and publishes the reviewed saved version. Deactivate stops future admission. Cancel does neither.

## Permissions

Access requires Policy View permission.
