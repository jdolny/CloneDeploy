# Active

Includes unfinished imaging and devices whose imaging finished but whose setup policies are still pending.

<div class="ui-controls-spacer"></div>

**Refresh tasks**

Reloads task state immediately.

**Search imaging tasks**

Filters the displayed records.

**Device name**

Opens the device workspace.

**Queue position**

Shows waiting order while the device is in the imaging queue.

**Progress / phase / elapsed / network rate**

Shows the latest reported progress. Rate is adapter traffic, including other traffic and protocol overhead.

**Not reporting**

No recent report for more than 30 seconds during imaging. Setup-pending work is excluded; this label does not itself prove failure.

**Setup policies**

Expand to see each policy and its result or blocking detail.

**Retry failed policy**

After confirmation, runs that policy again from its beginning. Inspect partial changes first. Previously completed policies do not repeat.

**Skip and continue**

After confirmation, skips a failed policy and continues setup. Later policies may depend on it; the decision is audited.

**Cancel remaining setup / Cancel deployment**

Cancels remaining work and releases normal policies after confirmation. Completed changes remain. A running policy must report its result before cancellation.

**Cancel upload**

After confirmation, discards an unfinished upload.

**Keep task / Keep running / Cancel in confirmation**

Dismisses the proposed operation without requesting it.
