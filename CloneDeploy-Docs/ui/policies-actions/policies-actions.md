# Actions

Actions run from top to bottom. Sequence order expresses dependencies; policy priority does not.

<div class="ui-controls-spacer"></div>

**Edit Actions / View Actions**

Opens the action catalog and execution order. Active policies are read-only here.

**Search actions / Action type**

Finds actions to assign.

**Add / drag into execution order**

Adds an action to the pending sequence.

**Move up / Move down / drag handle**

Changes execution order.

**Remove**

Removes an assignment from the sequence without deleting the shared action.

**Condition script / If condition fails / Next action**

Sets per-action gating and its failure response. Go to action requires a condition and a valid destination in the sequence.

**Save Actions / Cancel**

Commits the sequence or discards dialog edits.

**Run inventory**

Collects inventory after actions. Use an earlier inventory-only policy when a scan must precede other work.

**Remote access**

Leave Not Configured. Disabled, Enabled and Force Reinstall are retained in the editor but unsupported by the current agent; selecting them causes policy execution to fail. Use the device Connect panel for supported remote viewing.

**Run login tracker**

Collects login tracking as a built-in policy function.

**Save built-in functions**

Commits these functions separately from the action sequence. Domain join belongs in a Join Domain action.

## Permissions

Access requires Policy View permission.
