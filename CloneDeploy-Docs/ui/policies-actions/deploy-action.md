# Deploy an action

Run a saved action once on selected devices without creating a policy. Save action edits first.

<div class="ui-controls-spacer"></div>

**Targets**

Membership is resolved when you queue deployment; future group members are not added.

**Add devices**

Search/select up to the displayed 100 matches. Selections persist across searches; Use selected devices returns them to the wizard.

**Add groups / Add OUs**

Adds targets. Include descendant OUs expands an OU.

**Remove target**

Removes the pending assignment. Overlap produces one task per device.

**Continue / Back**

Moves between steps without executing.

**Timing**

Choose task delivery timing.

**Deploy now**

Notifies connected agents. Offline devices fetch later; capacity limits can delay execution.

**Next check-in**

Leaves task delivery to the next check-in.

**Expire unstarted tasks after (days)**

Limits how long work can wait to start, from 1 to 365 days.

**Review and deploy**

Review the saved action, count and timing. Preview can show only 100 devices even when more receive tasks.

**Review deployment**

Displays the pending summary.

**Queue deployment**

Creates tasks; does not guarantee immediate execution/success.

**View deployment**

Opens per-device progress/results.

**Test selected updates**

From update reporting, runs only chosen revisions without changing global approval.


**Search devices / Use selected devices (device picker)**

Select device checkboxes across searches; Use selected devices returns the retained selection to the wizard. Cancel discards this picker selection.

**Search groups / Add selected groups (group picker)**

Search and select group rows, then add the selected groups to the pending target list. At least one selection is required. Cancel adds nothing.

**Search OUs / Include descendants / Add selected OUs (OU picker)**

Browse the directory tree or search OUs. Select OUs and choose whether each includes descendants. Remove selection removes a pending OU. Add selected OUs returns the selection; Cancel leaves targets unchanged. In single-selection use, Use selected OU returns only the selected location.

**Close (embedded deployment dialog)**

Closing before Queue deployment abandons pending wizard choices. Closing after successful queuing does not cancel the resulting action deployment.

## Permissions

Access requires Action Deploy permission.
