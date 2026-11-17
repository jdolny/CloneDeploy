# Report

Define filters and choose the information to export. The preview shows at most 100 devices; exporting includes every match, regardless of preview pagination.

<div class="ui-controls-spacer"></div>

**New report**

Starts a new report with default columns and no filters.

**Save Report**

Beside Run report. Opens a dialog asking for a name and saves the filters and column choices.

**Save as a new copy**

In the save dialog for an opened report. Choose a different name to save a separate definition; otherwise saving updates the opened report.

**Filters**

Uses the dynamic-group rule builder. No conditions means all non-archived devices.

**Choose columns**

Adds or removes columns and changes their order with up/down arrows.

**Run report**

Counts matches and previews the first 100 devices.

**Preview paging**

Browses the preview without restricting the export.

**Export all matches**

Exports all matching devices using current data and the selected column order.

Columns include device identity, hardware, operating system, agent version, last user, timestamps, directory path, tags and custom attributes. Missing values are blank. Date columns use UTC. Tags are joined in one cell; installed applications, disks and policy histories are not expanded into additional rows.

Changing filters or columns clears the preview. Run the report again before exporting. The export reads current data at export time, so changes since the preview can affect its results.


**Report name / Save Report / Cancel (save dialog)**

Enter a required name up to 200 characters. Save Report persists the current filters and ordered columns; Cancel saves nothing. Save as a new copy is available when editing an existing saved report.

**Search columns / Use columns (column picker)**

Search available columns, add or remove them and use the arrows to order the selection. Choose between 1 and 100 columns. Use columns applies the pending selection to the report editor; it does not save the report. Cancel preserves the previous selection.

## Permissions

Device report permission is required.
