# General

These limits control shared server capacity and apply across server instances.

<div class="ui-controls-spacer"></div>

**Default check-in interval (minutes)**

Default 60; allowed 1–1440. Agents receive the value at their next successful check-in. An immediate request may run sooner.

**Maximum simultaneous downloads**

Shared by policies and direct actions. File transfers release their slot before installation; WinGet commands retain theirs until completion.

**Maximum simultaneous device tasks**

Limits direct action deployments across devices through both download and execution. Unknown outcomes reserve capacity until resolved.

**Maximum simultaneous check-in requests**

Limits authenticated check-in/policy retrieval processing. Busy agents retry after a short randomized delay. Inventory submissions and persistent connections have separate behavior.

**Maximum simultaneous inventory submissions**

Limits inventory processing, including policy and quick-action scans. Busy agents retry the collected inventory without collecting it again.

**Save agent settings / Reload settings**

Save current limits or retrieve the saved settings after a load failure. Concurrency values allow 1–10000.

## Permissions

Administrator access is required.
