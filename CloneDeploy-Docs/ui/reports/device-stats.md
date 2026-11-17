# Device Stats

Inspect a snapshot of non-archived devices. Report-builder filters do not affect this tab. Switching away and returning reloads its data.

<div class="ui-controls-spacer"></div>

**Summary cards**

Review fleet size, recent check-ins, low disk space, disk warnings, overdue check-ins and missing inventory. Attention counts may overlap.

**Charts**

Review inventory and check-in distributions by OS, manufacturer, model, agent version, memory and disk state. High-cardinality charts use eight groups plus Other; missing inventory is identified.

**Disk space**

Uses fixed logical volumes. Critical means at most 5 GiB or 5% free; low means at most 10 GiB or 10% free. Each device uses its worst category. Invalid or zero-capacity measurements are excluded.

**Disk warnings**

Uses reported Windows disk states, not detailed SMART analysis. Unknown or absent statuses do not establish healthy disks.

**Matching devices**

Select a card or chart bar to inspect its devices, search the list and open device details.


**Search matching devices / Close (matching-device dialog)**

Search narrows the devices returned for the selected statistic. Open a device name to inspect it. Close dismisses the snapshot without changing the statistic or device records.

## Permissions

Device report permission is required; opening devices requires device access.
