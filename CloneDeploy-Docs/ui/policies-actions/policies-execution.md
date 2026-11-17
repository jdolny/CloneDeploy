# Execution

Configure run behavior and whole-policy conditions. Use action order for dependencies and separate machine policies for operations unavailable to user-context runs.

<div class="ui-controls-spacer"></div>

**On completion**

Do Nothing leaves the device running. Reboot requests restart after successful installation; Reboot If No Logins requests it only when no users are logged in. Cache-only runs do not trigger completion restarts.

**Execution type**

Install downloads content and executes actions. Cache downloads policy files without executing actions.

**On error**

Abort Current Policy stops the current sequence; Abort Remaining Policies also stops later policies in that run; Continue allows the sequence to proceed after an action failure.

**Execution priority**

Lower numbers are considered first among eligible policies; default 100. Does not alter schedules or guarantee dependencies.

**Condition script / If condition fails**

Optional whole-policy condition. If it fails, record Not Applicable, Skipped, Success or Failed according to the selection. Go to action is available only for action-level conditions in the sequence.

**Log level**

Full includes detailed execution logging; Hidden Arguments suppresses command arguments in execution logging; None suppresses policy detail reporting. Avoid embedding secrets in output regardless of this setting.

**Auto archive / Archive after days**

The editor stores None, When Complete or After Xdays (with a positive number of days). Automatic archival is not implemented in the current server; use Archive from the policy actions when needed.

**Remove install cache**

Removes cached policy files after a successful Install run. Cache-only runs retain their files.

**Skip sending results to server**

Suppresses server result reporting; this reduces the evidence available in history.

**Save Execution**

Commits run behavior and advanced options.

## Permissions

Access requires Policy View permission.
