# Targets

Choose devices, groups and imported OUs. Overlapping targets count once; archived devices are excluded.

<div class="ui-controls-spacer"></div>

**Add devices / Add groups / Add OUs**

Adds pending target assignments.

**Include descendant OUs**

Includes devices under child OUs for that assignment.

**Remove target**

Removes the selected assignment, not the underlying device/group/OU.

**Preview devices**

Resolves current selections against current group membership and the latest directory import. Shows up to 100 devices; it does not activate a policy or evaluate condition scripts.

**Search targets / affected devices**

Filters the respective displayed lists.

**Reload saved targeting / Save Targets**

Discard pending edits and reload, or persist assignments. Dynamic groups use their latest membership rebuild.


**Target review dialog**

The activation review can open a read-only list of affected devices and their assignment sources. Paging browses the preview, not every possible future device. Close returns to the review without changing saved targeting.

## Permissions

Access requires Policy View permission.
